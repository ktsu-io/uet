﻿namespace Redpoint.OpenGE.Component.Worker
{
    using Redpoint.OpenGE.Core;
    using Redpoint.OpenGE.Protocol;
    using Redpoint.Reservation;
    using System;
    using System.Threading.Tasks;
    using System.IO.Hashing;
    using Grpc.Core;

    internal class DefaultToolManager : IToolManager, IAsyncDisposable
    {
        private readonly IReservationManagerForOpenGE _reservationManagerForOpenGE;
        private readonly SemaphoreSlim _toolsReservationSemaphore;
        private IReservation? _toolsReservation;
        private IReservation? _toolBlobsReservation;
        private bool _disposed;

        public DefaultToolManager(
            IReservationManagerForOpenGE reservationManagerForOpenGE)
        {
            _reservationManagerForOpenGE = reservationManagerForOpenGE;
            _toolsReservationSemaphore = new SemaphoreSlim(1);
            _toolsReservation = null;
            _toolBlobsReservation = null;
            _disposed = false;
        }

        private string HashAsHex(long hash)
        {
            return Convert.ToHexString(BitConverter.GetBytes(hash)).ToLowerInvariant();
        }

        public async Task<QueryToolResponse> QueryToolAsync(
            QueryToolRequest request,
            CancellationToken cancellationToken)
        {
            var toolsPath = await GetToolsPath();

            return new QueryToolResponse
            {
                Present = Directory.Exists(Path.Combine(toolsPath, HashAsHex(request.ToolXxHash64)))
            };
        }

        private async Task<long> HashFile(string path, CancellationToken cancellationToken)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var hasher = new XxHash64();
                await hasher.AppendAsync(stream, cancellationToken);
                return BitConverter.ToInt64(hasher.GetCurrentHash());
            }
        }

        public async Task<HasToolBlobsResponse> HasToolBlobsAsync(
            HasToolBlobsRequest request,
            CancellationToken cancellationToken)
        {
            var toolBlobsPath = await GetToolBlobsPath();

            var requested = new HashSet<long>(request.ToolBlobs.Select(x => x.XxHash64));
            var exists = new HashSet<long>();
            foreach (var file in request.ToolBlobs)
            {
                var targetPath = Path.Combine(toolBlobsPath, HashAsHex(file.XxHash64));
                if (File.Exists(targetPath))
                {
                    exists.Add(file.XxHash64);
                }
                else if (File.Exists(file.LocalHintPath))
                {
                    var localHintHash = await HashFile(file.LocalHintPath, cancellationToken);
                    if (localHintHash == file.XxHash64)
                    {
                        try
                        {
                            File.Copy(file.LocalHintPath, targetPath + ".tmp", true);
                            if (await HashFile(targetPath + ".tmp", cancellationToken) == file.XxHash64)
                            {
                                File.Move(targetPath + ".tmp", targetPath, true);
                                exists.Add(file.XxHash64);
                            }
                        }
                        catch
                        {
                            // Unable to copy local file into place.
                        }
                    }
                }
            }
            var notExists = new HashSet<long>(requested);
            notExists.ExceptWith(exists);

            var response = new HasToolBlobsResponse();
            response.Existence.AddRange(exists.Select(x => new ToolBlobExistence
            {
                XxHash64 = x,
                Exists = true,
            }));
            response.Existence.AddRange(notExists.Select(x => new ToolBlobExistence
            {
                XxHash64 = x,
                Exists = false,
            }));
            return response;
        }

        public async Task<WriteToolBlobResponse> WriteToolBlobAsync(
            WriteToolBlobRequest initialRequest,
            IWorkerRequestStream requestStream,
            CancellationToken cancellationToken)
        {
            var toolBlobsPath = await GetToolBlobsPath();

            if (initialRequest.InitialOrSubsequentCase != WriteToolBlobRequest.InitialOrSubsequentOneofCase.ToolBlobXxHash64)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Expected first WriteToolBlobRequest to have hash"));
            }

            var targetPath = Path.Combine(toolBlobsPath, HashAsHex(initialRequest.ToolBlobXxHash64));
            var lockPath = targetPath + ".lock";
            var temporaryPath = targetPath + ".tmp";
            long committedSize = 0;

            // When we run into scenarios where another RPC has already
            // written this file, we just need to consume all the data that
            // the caller is sending our way and then tell them that the file
            // got written.
            async Task<WriteToolBlobResponse> ConsumeAndDiscardAsync(WriteToolBlobRequest request)
            {
                committedSize += request.Data.Length;
                if (!request.FinishWrite)
                {
                    while (await requestStream.MoveNext(cancellationToken))
                    {
                        committedSize += requestStream.Current.WriteToolBlob.Data.Length;

                        if (requestStream.Current.WriteToolBlob.FinishWrite)
                        {
                            break;
                        }
                    }
                }
                return new WriteToolBlobResponse
                {
                    CommittedSize = committedSize,
                };
            }

            // Obtain the lock file, which prevents any other RPC from doing work
            // with this tool blob until we're done.
            IDisposable? @lock = null;
            do
            {
                // If another RPC wrote this while we were waiting for the lock, bail.
                if (File.Exists(targetPath))
                {
                    return await ConsumeAndDiscardAsync(initialRequest);
                }

                @lock = LockFile.TryObtainLock(lockPath);
                if (@lock == null)
                {
                    await Task.Delay(2000, cancellationToken);
                }
            }
            while (@lock == null);
            using (@lock)
            {
                // If another RPC wrote this while we were waiting for the lock, bail.
                if (File.Exists(targetPath))
                {
                    return await ConsumeAndDiscardAsync(initialRequest);
                }

                // Write the temporary file and hash at the same time.
                var xxHash64 = new XxHash64();
                using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var request = initialRequest;
                    while (true)
                    {
                        await stream.WriteAsync(request.Data.Memory, cancellationToken);
                        committedSize += request.Data.Length;
                        xxHash64.Append(request.Data.Span);
                        if (request.FinishWrite)
                        {
                            break;
                        }
                        else
                        {
                            if (!await requestStream.MoveNext(cancellationToken))
                            {
                                throw new RpcException(new Status(StatusCode.InvalidArgument, "WriteToolBlobRequest stream ended early."));
                            }
                            if (requestStream.Current.RequestCase != ExecutionRequest.RequestOneofCase.WriteToolBlob)
                            {
                                throw new RpcException(new Status(StatusCode.InvalidArgument, "WriteToolBlobRequest stream ended early."));
                            }
                            request = requestStream.Current.WriteToolBlob;
                        }
                    }

                    // Ensure that what we've written matches the hash that the caller advertised.
                    if (BitConverter.ToInt64(xxHash64.GetCurrentHash()) != initialRequest.ToolBlobXxHash64)
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "The provided file stream did not hash to the provided hash"));
                    }
                }

                // The temporary file is good now. Move it into place.
                File.Move(
                    temporaryPath,
                    targetPath,
                    true);
            }

            return new WriteToolBlobResponse
            {
                CommittedSize = committedSize,
            };
        }

        public async Task<ConstructToolResponse> ConstructToolAsync(
            ConstructToolRequest request,
            CancellationToken cancellationToken)
        {
            var toolsPath = await GetToolsPath();
            var toolBlobsPath = await GetToolBlobsPath();

            var hash = HashAsHex(request.ToolXxHash64);
            var targetPath = Path.Combine(toolsPath, hash);
            var temporaryPath = Path.Combine(toolsPath, hash + ".tmp");
            var lockPath = Path.Combine(toolsPath, hash + ".lock");

            // If another RPC assembled this tool since tool synchronisation
            // started, return immediately.
            if (Directory.Exists(targetPath))
            {
                return new ConstructToolResponse
                {
                    ToolXxHash64 = request.ToolXxHash64
                };
            }

            // Obtain the lock file, which prevents any other RPC from doing work
            // with this tool until we're done.
            IDisposable? @lock = null;
            do
            {
                // If another RPC assembled this tool while we were waiting for
                // the lock, bail.
                if (Directory.Exists(targetPath))
                {
                    return new ConstructToolResponse
                    {
                        ToolXxHash64 = request.ToolXxHash64
                    };
                }

                @lock = LockFile.TryObtainLock(lockPath);
                if (@lock == null)
                {
                    await Task.Delay(2000, cancellationToken);
                }
            }
            while (@lock == null);
            using (@lock)
            {
                // Create a directory for us to layout the tool.
                if (Directory.Exists(temporaryPath))
                {
                    DeleteRecursive(temporaryPath);
                }
                Directory.CreateDirectory(temporaryPath);

                // Construct the tool layout based on the request.
                foreach (var pathToHash in request.UnixRelativePathToToolBlobXxHash64)
                {
                    var directoryName = Path.GetDirectoryName(pathToHash.Key);
                    if (!string.IsNullOrWhiteSpace(directoryName))
                    {
                        Directory.CreateDirectory(Path.Combine(temporaryPath, directoryName));
                    }
                    if (!File.Exists(Path.Combine(toolBlobsPath, HashAsHex(pathToHash.Value))))
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "Missing tool blob for tool construction!"));
                    }
                    File.Copy(
                        Path.Combine(toolBlobsPath, HashAsHex(pathToHash.Value)),
                        Path.Combine(temporaryPath, pathToHash.Key),
                        true);
                }

                // @todo: Verify that the layout matches the hash.

                // Move the directory into place.
                Directory.Move(temporaryPath, targetPath);

                return new ConstructToolResponse
                {
                    ToolXxHash64 = request.ToolXxHash64
                };
            }
        }

        private void DeleteRecursive(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (UnauthorizedAccessException)
            {
                // Try and remove "Read Only" flags on files and directories.
                foreach (var entry in Directory.GetFileSystemEntries(
                    path,
                    "*",
                    new EnumerationOptions
                    {
                        AttributesToSkip = FileAttributes.System,
                        RecurseSubdirectories = true
                    }))
                {
                    var attrs = File.GetAttributes(entry);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        attrs ^= FileAttributes.ReadOnly;
                        File.SetAttributes(entry, attrs);
                    }
                }

                // Now try to delete again.
                Directory.Delete(path, true);
            }
        }

        private async Task<string> GetToolsPath()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DefaultToolManager));
            }
            if (_toolsReservation != null)
            {
                return _toolsReservation.ReservedPath;
            }
            await _toolsReservationSemaphore.WaitAsync();
            try
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(DefaultToolManager));
                }
                if (_toolsReservation != null)
                {
                    return _toolsReservation.ReservedPath;
                }
                _toolsReservation = await _reservationManagerForOpenGE.ReservationManager.ReserveAsync("Tools");
                return _toolsReservation.ReservedPath;
            }
            finally
            {
                _toolsReservationSemaphore.Release();
            }
        }

        private async Task<string> GetToolBlobsPath()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DefaultToolManager));
            }
            if (_toolBlobsReservation != null)
            {
                return _toolBlobsReservation.ReservedPath;
            }
            await _toolsReservationSemaphore.WaitAsync();
            try
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(DefaultToolManager));
                }
                if (_toolBlobsReservation != null)
                {
                    return _toolBlobsReservation.ReservedPath;
                }
                _toolBlobsReservation = await _reservationManagerForOpenGE.ReservationManager.ReserveAsync("ToolBlobs");
                return _toolBlobsReservation.ReservedPath;
            }
            finally
            {
                _toolsReservationSemaphore.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _toolsReservationSemaphore.WaitAsync();
            try
            {
                if (_toolBlobsReservation != null)
                {
                    await _toolBlobsReservation.DisposeAsync();
                }
                if (_toolsReservation != null)
                {
                    await _toolsReservation.DisposeAsync();
                }
                _disposed = true;
            }
            finally
            {
                _toolsReservationSemaphore.Release();
            }
        }
    }
}

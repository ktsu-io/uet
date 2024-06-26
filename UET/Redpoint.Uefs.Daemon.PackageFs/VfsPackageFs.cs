﻿namespace Redpoint.Uefs.Daemon.PackageFs
{
    using Docker.Registry.DotNet.Models;
    using Microsoft.Extensions.Logging;
    using Redpoint.Uefs.ContainerRegistry;
    using Redpoint.Uefs.Daemon.PackageFs.CachingStorage;
    using Redpoint.Uefs.Daemon.RemoteStorage;
    using Redpoint.Uefs.Protocol;
    using Redpoint.Vfs.Abstractions;
    using Redpoint.Vfs.Driver;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.Versioning;
    using System.Text.Json;
    using System.Threading.Tasks;

    [SupportedOSPlatform("windows6.2")]
    internal sealed class VfsPackageFs : CachingPackageFs
    {
        private readonly IVfsDriverFactory _vfsFactory;
        private readonly ILogger<IVfsLayer> _logger;
        private readonly IRemoteStorage<ManifestLayer> _registryRemoteStorage;
        private readonly IRemoteStorage<RegistryReferenceInfo> _referenceRemoteStorage;
        private readonly CachedFilePool _cachedFilePool;
        private readonly string _storagePath;

        private IDisposable? _vfs = null;

        public VfsPackageFs(
            IVfsDriverFactory vfsFactory,
            ILogger<IVfsLayer> logger,
            IRemoteStorage<ManifestLayer> registryRemoteStorage,
            IRemoteStorage<RegistryReferenceInfo> referenceRemoteStorage,
            string storagePath) : base(logger, storagePath)
        {
            _vfsFactory = vfsFactory;
            _logger = logger;
            _registryRemoteStorage = registryRemoteStorage;
            _referenceRemoteStorage = referenceRemoteStorage;
            _cachedFilePool = new CachedFilePool(
                logger,
                Path.Combine(
                    storagePath,
                    "hostpkgs",
                    "cache"));
            _storagePath = storagePath;

            Init();
        }

        protected override void Mount()
        {
            _logger.LogInformation($"Mounting VFS to: {_storagePath}");
            _vfs = _vfsFactory.InitializeAndMount(
                new StorageProjectionLayer(
                    _logger,
                    this,
                    _storagePath),
                GetVFSMountPath(),
                null);
        }

        protected override void Unmount()
        {
            _logger.LogInformation($"Unmounting VFS from: {_storagePath}");
            _vfs?.Dispose();
            _vfs = null;
        }

        protected override Task<bool> VerifyPackageAsync(
            bool isFixing,
            string normalizedPackageHash,
            CachingInfoJson info,
            Action<Action<PollingResponse>> updatePollingResponse)
        {
            // Open the remote resource.
            IRemoteStorageBlobFactory blobFactory;
            switch (info!.Type)
            {
                case "reference":
                    blobFactory = _referenceRemoteStorage.GetFactory(JsonSerializer.Deserialize(info.SerializedObject, UefsRegistryJsonSerializerContext.Default.RegistryReferenceInfo)!);
                    break;
                case "registry":
                    blobFactory = _registryRemoteStorage.GetFactory(JsonSerializer.Deserialize(info.SerializedObject, PackageFsJsonSerializerContext.Default.ManifestLayer)!);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported info type for {normalizedPackageHash}: {info!.Type}");
            }

            // Open the cache file so we can verify it.
            using (var cache = _cachedFilePool.Open(blobFactory, normalizedPackageHash))
            {
                // This will set the error in the operation if it fails.
                var didFix = cache.VfsFile.VerifyChunks(isFixing, updatePollingResponse);

                if (isFixing)
                {
                    // Immediately flush all indexes to disk, ensuring that they are in-sync before the verification process returns.
                    _cachedFilePool.FlushImmediately();
                }

                if (!didFix)
                {
                    return Task.FromResult(false);
                }
            }

            return Task.FromResult(true);
        }

        private sealed class StorageProjectionLayer : IVfsLayer
        {
            private readonly ILogger<IVfsLayer> _logger;
            private readonly VfsPackageFs _storageFS;

            private readonly DateTimeOffset _timestamp;
            private readonly string _infoStoragePath;
            private readonly DirectoryInfo _infoDirectoryInfo;
            private readonly ConcurrentDictionary<string, IRemoteStorageBlobFactory> _resolvedBlobs;

            public StorageProjectionLayer(
                ILogger<IVfsLayer> logger,
                VfsPackageFs storageFS,
                string storagePath)
            {
                _logger = logger;
                _storageFS = storageFS;

                _timestamp = DateTimeOffset.UtcNow;
                _infoStoragePath = Path.Combine(
                    storagePath,
                    "hostpkgs",
                    "info");
                _infoDirectoryInfo = new DirectoryInfo(_infoStoragePath);

                _resolvedBlobs = new ConcurrentDictionary<string, IRemoteStorageBlobFactory>();
            }

            public bool ReadOnly => true;

            public void Dispose()
            {
            }

            public VfsEntryExistence Exists(string path)
            {
                var fileExists = List(string.Empty)!.Any(x => string.Equals(x.Name, path, StringComparison.OrdinalIgnoreCase));
                if (!fileExists)
                {
                    _logger.LogWarning($"VHD on-demand storage layer: Requested file does not exist (FileExists): {path}");
                    return VfsEntryExistence.DoesNotExist;
                }

                return VfsEntryExistence.FileExists;
            }

            public VfsEntry? GetInfo(string path)
            {
                var entry = List(string.Empty)!.FirstOrDefault(x => string.Equals(x.Name, path, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    _logger.LogWarning($"VHD on-demand storage layer: Requested file does not exist (GetInfo): {path}");
                }
                return entry;
            }

            public IEnumerable<VfsEntry>? List(string path)
            {
                if (!string.IsNullOrEmpty(path))
                {
                    yield break;
                }

                foreach (var file in _infoDirectoryInfo.GetFiles("*.info"))
                {
                    var info = JsonSerializer.Deserialize(
                        File.ReadAllText(file.FullName).Trim(),
                        PackageFsInternalJsonSerializerContext.Default.CachingInfoJson);
                    yield return new VfsEntry
                    {
                        Name = Path.GetFileNameWithoutExtension(file.Name) + ".vhd",
                        Attributes = FileAttributes.Archive,
                        CreationTime = _timestamp,
                        LastAccessTime = _timestamp,
                        LastWriteTime = _timestamp,
                        ChangeTime = _timestamp,
                        Size = info!.Length,
                    };
                }
            }

            public IVfsFileHandle<IVfsFile>? OpenFile(string path, FileMode fileMode, FileAccess fileAccess, FileShare fileShare, ref VfsEntry? metadata)
            {
                var filename = path;

                if (_resolvedBlobs == null)
                {
                    throw new InvalidOperationException($"The resolved blobs cache is null.");
                }
                var blobFactory = _resolvedBlobs.GetOrAdd(filename, _ =>
                {
                    var infoFilePath = Path.Combine(
                        _infoStoragePath,
                        Path.GetFileNameWithoutExtension(filename) + ".info");
                    var info = JsonSerializer.Deserialize(
                        File.ReadAllText(infoFilePath).Trim(),
                        PackageFsInternalJsonSerializerContext.Default.CachingInfoJson);
                    if (info == null)
                    {
                        throw new InvalidOperationException($"The info file at '{infoFilePath}' is corrupt and can't be deserialized to a CachingInfoJson.");
                    }

                    IRemoteStorageBlobFactory blobFactory;
                    switch (info.Type)
                    {
                        case "reference":
                            var referenceInfo = JsonSerializer.Deserialize(
                                info.SerializedObject,
                                UefsRegistryJsonSerializerContext.Default.RegistryReferenceInfo);
                            if (referenceInfo == null)
                            {
                                throw new InvalidOperationException($"The info file at '{infoFilePath}' contains an invalid serialized object and can't be deserialized to a RegistryReferenceInfo.");
                            }
                            blobFactory = _storageFS._referenceRemoteStorage.GetFactory(referenceInfo);
                            break;
                        case "registry":
                            var registryInfo = JsonSerializer.Deserialize(
                                info.SerializedObject,
                                PackageFsJsonSerializerContext.Default.ManifestLayer);
                            if (registryInfo == null)
                            {
                                throw new InvalidOperationException($"The info file at '{infoFilePath}' contains an invalid serialized object and can't be deserialized to a ManifestLayer.");
                            }
                            blobFactory = _storageFS._registryRemoteStorage.GetFactory(registryInfo);
                            break;
                        default:
                            throw new InvalidOperationException($"Unsupported info type for {filename}: {info!.Type}");
                    }
                    if (blobFactory == null)
                    {
                        throw new InvalidOperationException($"The returned blob factory from the remote storage was null.");
                    }
                    return blobFactory;
                });

                if (_storageFS == null)
                {
                    throw new InvalidOperationException($"The current storage filesystem is null.");
                }
                if (_storageFS._cachedFilePool == null)
                {
                    throw new InvalidOperationException($"The cached file pool in the current storage filesystem is null.");
                }

                var filenameWithoutExtension = Path.GetFileNameWithoutExtension(filename);
                if (filenameWithoutExtension == null)
                {
                    throw new InvalidOperationException($"The filename '{filename}' does not return a version without an extension from Path.GetFileNameWithoutExtension.");
                }

                var handle = _storageFS._cachedFilePool.Open(blobFactory, filenameWithoutExtension);
                if (handle == null)
                {
                    throw new InvalidOperationException($"The cached file pool returned a null handle, which should be impossible.");
                }
                if (handle.VfsFile == null)
                {
                    throw new InvalidOperationException($"The cached file pool returned a handle with a null VfsFile, which should not exist in the cached file pool.");
                }
                metadata = new VfsEntry
                {
                    Name = filename,
                    Attributes = FileAttributes.Archive,
                    CreationTime = _timestamp,
                    LastAccessTime = _timestamp,
                    LastWriteTime = _timestamp,
                    ChangeTime = _timestamp,
                    Size = handle.VfsFile.Length,
                };
                return handle;
            }

            #region Unsupported Write Methods

            public bool CreateDirectory(string path)
            {
                _logger.LogError("Creating directories is not permitted in the storage projection layer.");
                return false;
            }

            public bool DeleteDirectory(string path)
            {
                _logger.LogError("Deleting directories is not permitted in the storage projection layer.");
                return false;
            }

            public bool DeleteFile(string path)
            {
                _logger.LogError("Deleting files is not permitted in the storage projection layer.");
                return false;
            }

            public bool MoveFile(string oldPath, string newPath, bool replace)
            {
                _logger.LogError("Moving files is not permitted in the storage projection layer.");
                return false;
            }

            public bool SetBasicInfo(string path, uint? attributes, DateTimeOffset? creationTime, DateTimeOffset? lastAccessTime, DateTimeOffset? lastWriteTime, DateTimeOffset? changeTime)
            {
                _logger.LogError("Setting file information is not permitted in the storage projection layer.");
                return false;
            }

            #endregion
        }
    }
}

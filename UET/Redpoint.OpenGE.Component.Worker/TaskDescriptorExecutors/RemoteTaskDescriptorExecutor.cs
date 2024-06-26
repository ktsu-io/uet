﻿namespace Redpoint.OpenGE.Component.Worker.TaskDescriptorExecutors
{
    using Redpoint.OpenGE.Core;
    using Redpoint.OpenGE.Protocol;
    using Redpoint.ProcessExecution.Enumerable;
    using Redpoint.ProcessExecution;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Runtime.CompilerServices;
    using Microsoft.Extensions.Logging;
    using System.Diagnostics;
    using System.Runtime.Versioning;
    using Redpoint.IO;
    using System.Net;
    using Redpoint.Concurrency;

    internal class RemoteTaskDescriptorExecutor : ITaskDescriptorExecutor<RemoteTaskDescriptor>
    {
        private readonly ILogger<RemoteTaskDescriptorExecutor> _logger;
        private readonly IReservationManagerForOpenGE _reservationManagerForOpenGE;
        private readonly IToolManager _toolManager;
        private readonly IBlobManager _blobManager;
        private readonly IProcessExecutor _processExecutor;
        private readonly IProcessExecutorResponseConverter _processExecutorResponseConverter;
        private static HashSet<string> _knownDirectoryEnvironmentVariables = new HashSet<string>
        {
            "ALLUSERSPROFILE",
            "APPDATA",
            "CommonProgramFiles",
            "CommonProgramFiles(x86)",
            "CommonProgramW6432",
            "OneDrive",
            "ProgramData",
            "ProgramFiles",
            "ProgramFiles(x86)",
            "ProgramW6432",
            "PUBLIC",
            "TEMP",
            "TMP",
            "USERPROFILE"
        };
        private static HashSet<string> _knownDirectorySpecialFolders = new HashSet<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Personal),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetTempPath(),
        };

        public RemoteTaskDescriptorExecutor(
            ILogger<RemoteTaskDescriptorExecutor> logger,
            IReservationManagerForOpenGE reservationManagerForOpenGE,
            IToolManager toolManager,
            IBlobManager blobManager,
            IProcessExecutor processExecutor,
            IProcessExecutorResponseConverter processExecutorResponseConverter)
        {
            _logger = logger;
            _reservationManagerForOpenGE = reservationManagerForOpenGE;
            _toolManager = toolManager;
            _blobManager = blobManager;
            _processExecutor = processExecutor;
            _processExecutorResponseConverter = processExecutorResponseConverter;
        }

        public IAsyncEnumerable<ExecuteTaskResponse> ExecuteAsync(
            IPAddress peerAddress,
            RemoteTaskDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            if (descriptor.UseFastLocalExecution && OperatingSystem.IsWindows())
            {
                return ExecuteLocalAsync(descriptor, cancellationToken);
            }
            else if (descriptor.StorageLayerCase == RemoteTaskDescriptor.StorageLayerOneofCase.TransferringStorageLayer && OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600))
            {
                return ExecuteTransferringRemoteAsync(descriptor, cancellationToken);
            }
            else
            {
                throw new NotSupportedException("This remote descriptor can't be executed on this machine.");
            }
        }

        [SupportedOSPlatform("windows")]
        private async IAsyncEnumerable<ExecuteTaskResponse> ExecuteLocalAsync(
            RemoteTaskDescriptor descriptor,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _logger.LogTrace("Executing remote task with local execution strategy.");

            // Set up the environment variable dictionary.
            Dictionary<string, string>? environmentVariables = null;
            if (descriptor.EnvironmentVariables.Count > 0)
            {
                environmentVariables = new Dictionary<string, string>();
                foreach (var kv in descriptor.EnvironmentVariables)
                {
                    environmentVariables[kv.Key] = kv.Value;
                }
            }

            // Set up the process specification.
            var processSpecification = new ProcessSpecification
            {
                FilePath = descriptor.ToolLocalAbsolutePath,
                Arguments = descriptor.Arguments,
                EnvironmentVariables = environmentVariables,
                WorkingDirectory = descriptor.WorkingDirectoryAbsolutePath,
            };

            // Execute the process in the virtual root.
            await foreach (var response in _processExecutor.ExecuteAsync(
                processSpecification,
                cancellationToken))
            {
                switch (response)
                {
                    case ExitCodeResponse exitCode:
                        yield return new ExecuteTaskResponse
                        {
                            Response = new Protocol.ProcessResponse
                            {
                                ExitCode = exitCode.ExitCode,
                            },
                        };
                        break;
                    case StandardOutputResponse standardOutput:
                        _logger.LogTrace(standardOutput.Data);
                        yield return new ExecuteTaskResponse
                        {
                            Response = new Protocol.ProcessResponse
                            {
                                StandardOutputLine = standardOutput.Data,
                            },
                        };
                        break;
                    case StandardErrorResponse standardError:
                        _logger.LogTrace(standardError.Data);
                        yield return new ExecuteTaskResponse
                        {
                            Response = new Protocol.ProcessResponse
                            {
                                StandardErrorLine = standardError.Data,
                            },
                        };
                        break;
                    default:
                        throw new InvalidOperationException("Received unexpected ProcessResponse type from IProcessExecutor!");
                };
            }
        }

        [SupportedOSPlatform("windows5.1.2600")]
        private async IAsyncEnumerable<ExecuteTaskResponse> ExecuteTransferringRemoteAsync(
            RemoteTaskDescriptor descriptor,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _logger.LogTrace("Executing remote task with remote execution strategy.");

            // Get a workspace to work in.
            await using ((await _reservationManagerForOpenGE.ReservationManager.ReserveAsync(
                "OpenGEBuild",
                descriptor.ToolExecutionInfo.ToolExecutableName).ConfigureAwait(false)).AsAsyncDisposable(out var reservation).ConfigureAwait(false))
            {
                // Ask the tool manager where our tool is located.
                var st = Stopwatch.StartNew();
                var toolPath = await _toolManager.GetToolPathAsync(
                    descriptor.ToolExecutionInfo.ToolXxHash64,
                    descriptor.ToolExecutionInfo.ToolExecutableName,
                    cancellationToken).ConfigureAwait(false);
                _logger.LogTrace($"Tool path obtained in: {st.Elapsed}");
                st.Restart();

                // Ask the blob manager to lay out all of the files in the reservation
                // based on the input files.
                await _blobManager.LayoutBuildDirectoryAsync(
                    reservation.ReservedPath,
                    descriptor.TransferringStorageLayer.InputsByBlobXxHash64,
                    cancellationToken).ConfigureAwait(false);
                _logger.LogTrace($"Laid out build directory in: {st.Elapsed}");
                st.Restart();

                // Inside our reservation directory, we need to create a junction
                // for the folder that our tool is in.
                var toolFolder = Path.GetDirectoryName(toolPath)!;
                Junction.CreateJunction(
                    _blobManager.ConvertAbsolutePathToBuildDirectoryPath(reservation.ReservedPath, toolFolder),
                    toolFolder,
                    true);

                // We also need to map SYSTEMROOT, since that is where our Windows
                // folder is.
                var systemRoot = Environment.GetEnvironmentVariable("SYSTEMROOT")!;
                if (Directory.Exists(systemRoot))
                {
                    Junction.CreateJunction(
                        _blobManager.ConvertAbsolutePathToBuildDirectoryPath(reservation.ReservedPath, systemRoot),
                        systemRoot,
                        true);
                }

                // Create directories that should exist based on the environment.
                var effectiveEnvironmentVariables = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
                foreach (string key in Environment.GetEnvironmentVariables().Keys)
                {
                    effectiveEnvironmentVariables[key] = Environment.GetEnvironmentVariable(key)!;
                }
                foreach (var kv in descriptor.EnvironmentVariables)
                {
                    effectiveEnvironmentVariables[kv.Key] = kv.Value;
                }
                foreach (var key in _knownDirectoryEnvironmentVariables)
                {
                    if (effectiveEnvironmentVariables.TryGetValue(key, out var value) &&
                        !string.IsNullOrWhiteSpace(value))
                    {
                        var targetPath = _blobManager.ConvertAbsolutePathToBuildDirectoryPath(
                            reservation.ReservedPath,
                            value);
                        if (!Path.Exists(targetPath))
                        {
                            Directory.CreateDirectory(targetPath);
                        }
                    }
                }
                foreach (var path in _knownDirectorySpecialFolders)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        var targetPath = _blobManager.ConvertAbsolutePathToBuildDirectoryPath(
                            reservation.ReservedPath,
                            path);
                        if (!Path.Exists(targetPath))
                        {
                            Directory.CreateDirectory(targetPath);
                        }
                    }
                }

                // Set up the environment variable dictionary.
                Dictionary<string, string> environmentVariables = new Dictionary<string, string>();
                if (descriptor.EnvironmentVariables.Count > 0)
                {
                    foreach (var kv in descriptor.EnvironmentVariables)
                    {
                        environmentVariables[kv.Key] = kv.Value;
                    }
                }

                // @note: We *MUST* set these environment variables. If we don't, cl.exe will
                // output with a weird "cannot create temporarily il file" failure, even though the error
                // has nothing to do with the TMP directory or disk space.
                environmentVariables["SystemRoot"] = systemRoot ?? @"C:\Windows";
                environmentVariables["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                // Set up the process specification.
                var processSpecification = new ProcessSpecification
                {
                    FilePath = toolPath,
                    Arguments = descriptor.Arguments,
                    EnvironmentVariables = environmentVariables,
                    WorkingDirectory = descriptor.WorkingDirectoryAbsolutePath,
                };
                if (OperatingSystem.IsWindows())
                {
                    processSpecification.PerProcessDriveMappings = DriveInfo
                        .GetDrives()
                        .Where(x => Path.Exists(Path.Combine(reservation.ReservedPath, x.Name[0].ToString())))
                        .ToDictionary(
                            k => k.Name[0],
                            v => Path.Combine(reservation.ReservedPath, v.Name[0].ToString()));
                }

                // Execute the process in the virtual root.
                _logger.LogTrace($"File path: {toolPath}");
                _logger.LogTrace($"Arguments: {string.Join(" ", descriptor.Arguments)}");
                _logger.LogTrace($"Working directory: {descriptor.WorkingDirectoryAbsolutePath}");
                if (processSpecification.PerProcessDriveMappings != null)
                {
                    foreach (var mapping in processSpecification.PerProcessDriveMappings)
                    {
                        _logger.LogTrace($"Drive mapping: '{mapping.Key}:\\' -> '{mapping.Value}'");
                    }
                    if (processSpecification.PerProcessDriveMappings.Count == 0)
                    {
                        _logger.LogTrace($"No drive mappings are being applied (empty).");
                    }
                }
                else
                {
                    _logger.LogTrace($"No drive mappings are being applied (not configured).");
                }
                await foreach (var response in _processExecutor.ExecuteAsync(
                    processSpecification,
                    cancellationToken))
                {
                    switch (response)
                    {
                        case ExitCodeResponse exitCode:
                            var outputBlobs = await _blobManager.CaptureOutputBlobsFromBuildDirectoryAsync(
                                reservation.ReservedPath,
                                descriptor.TransferringStorageLayer.OutputAbsolutePaths,
                                cancellationToken).ConfigureAwait(false);
                            yield return new ExecuteTaskResponse
                            {
                                Response = new Protocol.ProcessResponse
                                {
                                    ExitCode = exitCode.ExitCode,
                                },
                                OutputAbsolutePathsToBlobXxHash64 = outputBlobs,
                            };
                            break;
                        default:
                            yield return _processExecutorResponseConverter.ConvertResponse(response);
                            break;
                    };
                }
            }
        }
    }
}

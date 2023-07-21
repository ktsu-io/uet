namespace Redpoint.Uet.SdkManagement
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Redpoint.ProcessExecution;
    using Redpoint.Reservation;
    using Redpoint.Uet.Core;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Text.Json;
    using System.Threading.Tasks;

    internal class DefaultLocalSdkManager : ILocalSdkManager
    {
        private readonly IReservationManagerFactory _reservationManagerFactory;
        private readonly ILogger<DefaultLocalSdkManager> _logger;
        private readonly IStringUtilities _stringUtilities;
        private readonly Dictionary<string, ISdkSetup> _sdkSetupsByPlatformName;
        private ConcurrentDictionary<string, IReservationManager> _reservationManagers;

        public DefaultLocalSdkManager(
            IReservationManagerFactory reservationManagerFactory,
            IServiceProvider serviceProvider,
            ILogger<DefaultLocalSdkManager> logger,
            IStringUtilities stringUtilities)
        {
            _reservationManagers = new ConcurrentDictionary<string, IReservationManager>(StringComparer.InvariantCultureIgnoreCase);
            _reservationManagerFactory = reservationManagerFactory;
            _logger = logger;
            _stringUtilities = stringUtilities;
            _sdkSetupsByPlatformName = new Dictionary<string, ISdkSetup>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var sdkSetup in serviceProvider.GetServices<ISdkSetup>())
            {
                foreach (var platform in sdkSetup.PlatformNames)
                {
                    if (!_sdkSetupsByPlatformName.ContainsKey(platform))
                    {
                        _sdkSetupsByPlatformName[platform] = sdkSetup;
                    }
                }
            }
        }

        public async Task<Dictionary<string, string>> SetupEnvironmentForBuildGraphNode(
            string enginePath,
            string sdksPath,
            string buildGraphNodeName,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Determining SDKs required for build graph node '{buildGraphNodeName}'...");

            var sdkSetups = new HashSet<ISdkSetup>();
            var environmentVariableName = $"UET_PLATFORMS_FOR_BUILD_GRAPH_NODE_{buildGraphNodeName.Replace(" ", "_")}";
            var overriddenPlatforms = Environment.GetEnvironmentVariable(environmentVariableName);
            if (!string.IsNullOrWhiteSpace(overriddenPlatforms))
            {
                // Platforms are determined by environment variable.
                var platforms = overriddenPlatforms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var platform in platforms)
                {
                    if (_sdkSetupsByPlatformName.ContainsKey(platform))
                    {
                        sdkSetups.Add(_sdkSetupsByPlatformName[platform]);
                    }
                }
            }
            else
            {
                // Platforms are determined by detecting them being mentioned in the BuildGraph node name.
                var components = buildGraphNodeName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var component in components)
                {
                    if (_sdkSetupsByPlatformName.ContainsKey(component))
                    {
                        sdkSetups.Add(_sdkSetupsByPlatformName[component]);
                    }
                }
            }

            if (sdkSetups.Count == 0)
            {
                _logger.LogWarning($"This BuildGraph node has no automatic SDK providers. The necessary dependencies and environment for the build must already be installed on this machine.");
                return new Dictionary<string, string>();
            }

            _logger.LogInformation($"Selected SDK platforms {string.Join(", ", sdkSetups.Select(x => $"'{x.PlatformNames.First()}'"))} based on environment variable '{environmentVariableName}'.");

            var reservationManager = _reservationManagers.GetOrAdd(
                sdksPath.TrimEnd(new[] { '\\', '/' }),
                _reservationManagerFactory.CreateReservationManager);

            // Download and set up all of the SDKs.
            Dictionary<string, string> autoSdkMappings = new Dictionary<string, string>();
            Dictionary<string, string> environmentVariables = new Dictionary<string, string>();
            var allPackageIds = new List<string>();
            foreach (var sdkSetup in sdkSetups)
            {
                _logger.LogInformation($"Requesting SDK for platform {sdkSetup.CommonPlatformNameForPackageId}...");

                var packageId = $"{sdkSetup.CommonPlatformNameForPackageId}-{await sdkSetup.ComputeSdkPackageId(enginePath, cancellationToken)}";
                allPackageIds.Add(packageId);
                await using (var reservation = await reservationManager.ReserveExactAsync(packageId, cancellationToken))
                {
                    if (!File.Exists(Path.Combine(reservation.ReservedPath, "sdk-ready")))
                    {
                        var packageWorkingPath = Path.Combine(sdksPath, $"{packageId}-tmp-{Process.GetCurrentProcess().Id}");
                        var packageOldPath = Path.Combine(sdksPath, $"{packageId}-old-{Process.GetCurrentProcess().Id}");
                        if (Directory.Exists(packageWorkingPath))
                        {
                            await DirectoryAsync.DeleteAsync(packageWorkingPath, true);
                        }
                        if (Directory.Exists(packageOldPath))
                        {
                            await DirectoryAsync.DeleteAsync(packageOldPath, true);
                        }
                        Directory.CreateDirectory(packageWorkingPath);
                        await sdkSetup.GenerateSdkPackage(enginePath, packageWorkingPath, cancellationToken);
                        await File.WriteAllTextAsync(Path.Combine(packageWorkingPath, "sdk-ready"), "ready", cancellationToken);
                        try
                        {
                            if (Directory.Exists(reservation.ReservedPath))
                            {
                                await DirectoryAsync.MoveAsync(reservation.ReservedPath, packageOldPath);
                            }
                            await DirectoryAsync.MoveAsync(packageWorkingPath, reservation.ReservedPath);
                        }
                        catch
                        {
                            if (!Directory.Exists(reservation.ReservedPath) &&
                                Directory.Exists(packageOldPath))
                            {
                                await DirectoryAsync.MoveAsync(packageOldPath, reservation.ReservedPath);
                            }
                        }
                        finally
                        {
                            if (Directory.Exists(packageOldPath))
                            {
                                await DirectoryAsync.DeleteAsync(packageOldPath, true);
                            }
                        }
                    }

                    switch (sdkSetup)
                    {
                        case IManualSdkSetup manualSdkSetup:
                            var sdkEnvironment = await manualSdkSetup.GetRuntimeEnvironmentForSdkPackage(reservation.ReservedPath, cancellationToken);
                            foreach (var kv in sdkEnvironment.EnvironmentVariables)
                            {
                                environmentVariables[kv.Key] = kv.Value;
                            }
                            break;
                        case IAutoSdkSetup autoSdkSetup:
                            foreach (var mapping in await autoSdkSetup.GetAutoSdkMappingsForSdkPackage(reservation.ReservedPath, cancellationToken))
                            {
                                var autoSdkPath = mapping.RelativePathInsideAutoSdkPath;
                                // @note: This does allow the reservation path to escape the reservation, but that's fine for the moment
                                // because we know this path will continue to exist on disk and we expect platform SDKs to be usable by
                                // multiple compiler operations at once.
                                var absoluteTargetPath = Path.GetFullPath(Path.Combine(reservation.ReservedPath, mapping.RelativePathInsideSdkPackagePath));

                                autoSdkMappings[autoSdkPath] = absoluteTargetPath;
                            }
                            break;
                        default:
                            throw new NotSupportedException("SdkSetup did not implement IManualSdkSetup or IAutoSdkSetup");
                    }
                }
            }

            // Are there any AutoSDK mappings? If so, set up the AutoSDK environment.
            if (autoSdkMappings.Count > 0)
            {
                var autoSdkId = "AutoSDK-" + _stringUtilities.GetStabilityHash(string.Join(';', allPackageIds), 20);
                await using (var reservation = await reservationManager.ReserveExactAsync(autoSdkId, cancellationToken))
                {

                }
            }

            EnvironmentForSdkUsage env;


            if (env.EnvironmentVariables.Count == 0)
            {
                _logger.LogInformation($"The {platform} SDK setup did not provide any environment variables for the build.");
            }
            else
            {
                _logger.LogInformation($"The {platform} SDK setup provided the following environment variables:");
                foreach (var kv in env.EnvironmentVariables)
                {
                    _logger.LogInformation($"  {kv.Key}={kv.Value}");
                }
            }

            return env.EnvironmentVariables;
        }
    }
}

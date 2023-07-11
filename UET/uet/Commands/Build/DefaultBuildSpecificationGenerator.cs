﻿namespace UET.Commands.Build
{
    using Microsoft.Extensions.Logging;
    using Redpoint.ProcessExecution;
    using Redpoint.Uet.BuildPipeline.BuildGraph;
    using Redpoint.Uet.BuildPipeline.BuildGraph.Dynamic;
    using Redpoint.Uet.BuildPipeline.Environment;
    using Redpoint.Uet.BuildPipeline.Executors;
    using Redpoint.Uet.BuildPipeline.Executors.Engine;
    using Redpoint.Uet.Configuration;
    using Redpoint.Uet.Configuration.Engine;
    using Redpoint.Uet.Configuration.Plugin;
    using Redpoint.Uet.Configuration.Project;
    using Redpoint.Uet.Core.Permissions;
    using Redpoint.Uet.Uat;
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using UET.Commands.EngineSpec;
    using UET.Services;

    internal class DefaultBuildSpecificationGenerator : IBuildSpecificationGenerator
    {
        private readonly ILogger<DefaultBuildSpecificationGenerator> _logger;
        private readonly ISelfLocation _selfLocation;
        private readonly IPluginVersioning _versioning;
        private readonly IDynamicBuildGraphIncludeWriter _dynamicBuildGraphIncludeWriter;
        private readonly IWorldPermissionApplier _worldPermissionApplier;
        private readonly IEngineWorkspaceProvider _engineWorkspaceProvider;
        private readonly IBuildGraphExecutor _buildGraphExecutor;
        private readonly IGlobalArgsProvider? _globalArgsProvider;

        public DefaultBuildSpecificationGenerator(
            ILogger<DefaultBuildSpecificationGenerator> logger,
            ISelfLocation selfLocation,
            IPluginVersioning versioning,
            IDynamicBuildGraphIncludeWriter dynamicBuildGraphIncludeWriter,
            IWorldPermissionApplier worldPermissionApplier,
            IEngineWorkspaceProvider engineWorkspaceProvider,
            IBuildGraphExecutor buildGraphExecutor,
            IGlobalArgsProvider? globalArgsProvider = null)
        {
            _logger = logger;
            _selfLocation = selfLocation;
            _versioning = versioning;
            _dynamicBuildGraphIncludeWriter = dynamicBuildGraphIncludeWriter;
            _worldPermissionApplier = worldPermissionApplier;
            _engineWorkspaceProvider = engineWorkspaceProvider;
            _buildGraphExecutor = buildGraphExecutor;
            _globalArgsProvider = globalArgsProvider;
        }

        private struct TargetConfig
        {
            public required string Targets;
            public required string TargetPlatforms;
            public required string Configurations;
        }

        private TargetConfig ComputeTargetConfig(string name, BuildConfigProjectBuildTarget? target, bool localExecutor)
        {
            if (target == null)
            {
                return new TargetConfig
                {
                    Targets = string.Empty,
                    TargetPlatforms = string.Empty,
                    Configurations = string.Empty,
                };
            }

            var targets = target.Targets ?? new[] { $"Unreal{name}" };
            var targetPlatforms = FilterIncompatiblePlatforms(target.Platforms, localExecutor);
            var configurations = target.Configurations ?? new[] { "Development", "Shipping" };

            return new TargetConfig
            {
                Targets = string.Join(";", targets),
                TargetPlatforms = string.Join(";", targetPlatforms),
                Configurations = string.Join(";", configurations),
            };
        }

        private TargetConfig ComputeTargetConfig(string name, BuildConfigPluginBuildTarget? target, bool localExecutor)
        {
            if (target == null)
            {
                return new TargetConfig
                {
                    Targets = string.Empty,
                    TargetPlatforms = string.Empty,
                    Configurations = string.Empty,
                };
            }

            var targets = new[] { $"Unreal{name}" };
            var targetPlatforms = FilterIncompatiblePlatforms(target.Platforms, localExecutor);
            var configurations = target.Configurations ?? new[] { "Development", "Shipping" };

            return new TargetConfig
            {
                Targets = string.Join(";", targets),
                TargetPlatforms = string.Join(";", targetPlatforms),
                Configurations = string.Join(";", configurations),
            };
        }

        private string GetFilterInclude(
            string repositoryRoot,
            BuildConfigPluginDistribution distribution)
        {
            if (distribution.Package?.Filter == null)
            {
                return string.Empty;
            }
            var filterRules = new List<string>();
            var rawFilterRules = File.ReadAllLines(Path.Combine(repositoryRoot, distribution.Package.Filter));
            foreach (var rawFilterRule in rawFilterRules)
            {
                if (rawFilterRule == "[FilterPlugin]" ||
                    rawFilterRule.StartsWith(";") ||
                    rawFilterRule.StartsWith("-") ||
                    rawFilterRule.Trim().Length == 0)
                {
                    continue;
                }
                filterRules.Add(rawFilterRule);
            }
            return string.Join(";", filterRules);
        }

        private string GetFilterExclude(
            string repositoryRoot,
            BuildConfigPluginDistribution distribution)
        {
            if (distribution.Package?.Filter == null)
            {
                return string.Empty;
            }
            var filterRules = new List<string>();
            var rawFilterRules = File.ReadAllLines(Path.Combine(repositoryRoot, distribution.Package.Filter));
            foreach (var rawFilterRule in rawFilterRules)
            {
                if (rawFilterRule == "[FilterPlugin]" ||
                    rawFilterRule.StartsWith(";") ||
                    !rawFilterRule.StartsWith("-") ||
                    rawFilterRule.Trim().Length == 0)
                {
                    continue;
                }
                filterRules.Add(rawFilterRule.Substring(1));
            }
            return string.Join(";", filterRules);
        }

        public async Task<BuildSpecification> BuildConfigEngineToBuildSpecAsync(
            BuildEngineSpecification engineSpec,
            BuildGraphEnvironment buildGraphEnvironment,
            BuildConfigEngineDistribution distribution,
            CancellationToken cancellationToken)
        {
            var availablePlatforms = new HashSet<string>();
            var availablePlatformsMac = new HashSet<string>();
            await using (var engine = await _engineWorkspaceProvider.GetEngineWorkspace(
                engineSpec,
                "EngineBuildOptionAnalysis",
                cancellationToken))
            {
                var installedEngineBuildPath = Path.Combine(
                    engine.Path,
                    "Engine",
                    "Build",
                    "InstalledEngineBuild.xml");
                var installedEngineBuild = await File.ReadAllTextAsync(installedEngineBuildPath);
                await _buildGraphExecutor.ListGraphAsync(
                    engine.Path,
                    BuildGraphScriptSpecification.ForEngine(),
                    CaptureSpecification.CreateFromDelegates(new CaptureSpecificationDelegates
                    {
                        ReceiveStdout = (line) =>
                        {
                            line = line.Trim();
                            if (line.StartsWith("-set:With") &&
                                !line.StartsWith("-set:WithDDC") &&
                                !line.StartsWith("-set:WithClient") &&
                                !line.StartsWith("-set:WithServer") &&
                                !line.StartsWith("-set:WithFullDebugInfo"))
                            {
                                line = line.Substring("-set:With".Length);
                                line = line.Split('=')[0];
                                availablePlatforms.Add(line);
                                if (installedEngineBuild.Contains($@"<Option Name=""With{line}"""))
                                {
                                    // macOS only knows about public (non-console) platforms.
                                    availablePlatformsMac.Add(line);
                                }
                            }
                            return false;
                        }
                    }),
                    cancellationToken);
            }

            var settings = new Dictionary<string, string>
            {
                // Target types
                { "WithClient", distribution.Build.TargetTypes.Contains("Client") ? "true" : "false" },
                { "WithServer", distribution.Build.TargetTypes.Contains("Server") ? "true" : "false" },

                // Cook options
                { "WithDDC", distribution.Cook.GenerateDDC ? "true" : "false" },
            };
            foreach (var platform in availablePlatforms)
            {
                settings[$"With{platform}"] = distribution.Build.Platforms.Contains(platform) ? "true" : "false";
            }

            return new BuildSpecification
            {
                Engine = engineSpec,
                BuildGraphScript = BuildGraphScriptSpecification.ForEngine(),
                BuildGraphTarget = "Make Installed Build Win64",
                BuildGraphSettings = settings,
                BuildGraphEnvironment = buildGraphEnvironment,
                BuildGraphRepositoryRoot = string.Empty,
                UETPath = _selfLocation.GetUETLocalLocation(),
                GlobalEnvironmentVariables = new Dictionary<string, string>(),
                ProjectFolderName = null,
                ArtifactExportPath = Environment.CurrentDirectory,
            };
        }

        private string[] FilterIncompatiblePlatforms(string[] platforms, bool localExecutor)
        {
            if (!localExecutor)
            {
                return platforms;
            }
            if (OperatingSystem.IsWindows())
            {
                return platforms.Where(x => !x.Equals("Mac", StringComparison.InvariantCultureIgnoreCase) && !x.Equals("IOS", StringComparison.InvariantCultureIgnoreCase)).ToArray();
            }
            else
            {
                return platforms.Where(x => x.Equals("Mac", StringComparison.InvariantCultureIgnoreCase) || x.Equals("IOS", StringComparison.InvariantCultureIgnoreCase)).ToArray();
            }
        }

        private async Task<(string nodeInclude, string macroInclude)> WriteDynamicBuildGraphIncludeAsync(
            BuildGraphEnvironment env,
            bool localExecutor,
            object distribution,
            bool executeTests,
            bool executeDeployment)
        {
            var sharedStorageAbsolutePath = OperatingSystem.IsWindows() ?
                env.Windows.SharedStorageAbsolutePath :
                env.Mac!.SharedStorageAbsolutePath;
            Directory.CreateDirectory(sharedStorageAbsolutePath);

            var nodeFilename = $"DynamicBuildGraph-{Process.GetCurrentProcess().Id}.Nodes.xml";
            var macroFilename = $"DynamicBuildGraph-{Process.GetCurrentProcess().Id}.Macros.xml";

            using (var stream = new FileStream(Path.Combine(sharedStorageAbsolutePath, nodeFilename), FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await _dynamicBuildGraphIncludeWriter.WriteBuildGraphNodeInclude(
                    stream,
                    localExecutor,
                    distribution,
                    executeTests,
                    executeDeployment);
            }
            await _worldPermissionApplier.GrantEveryonePermissionAsync(Path.Combine(sharedStorageAbsolutePath, nodeFilename), CancellationToken.None);

            using (var stream = new FileStream(Path.Combine(sharedStorageAbsolutePath, macroFilename), FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await _dynamicBuildGraphIncludeWriter.WriteBuildGraphMacroInclude(
                    stream,
                    localExecutor,
                    distribution);
            }
            await _worldPermissionApplier.GrantEveryonePermissionAsync(Path.Combine(sharedStorageAbsolutePath, macroFilename), CancellationToken.None);

            return ($"__SHARED_STORAGE_PATH__/{nodeFilename}", $"__SHARED_STORAGE_PATH__/{macroFilename}");
        }

        public async Task<BuildSpecification> BuildConfigPluginToBuildSpecAsync(
            BuildEngineSpecification engineSpec,
            BuildGraphEnvironment buildGraphEnvironment,
            BuildConfigPluginDistribution distribution,
            BuildConfigPlugin pluginInfo,
            string repositoryRoot,
            bool executeBuild,
            bool executePackage,
            bool executeTests,
            bool executeDeployment,
            bool strictIncludes,
            bool localExecutor,
            bool isPluginRooted,
            string? commandlinePluginVersionName,
            long? commandlinePluginVersionNumber)
        {
            // Determine build matrix.
            var editorTargetPlatforms = FilterIncompatiblePlatforms((distribution.Build.Editor?.Platforms ?? new[] { BuildConfigPluginBuildEditorPlatform.Win64 }).Select(x =>
            {
                switch (x)
                {
                    case BuildConfigPluginBuildEditorPlatform.Win64:
                        return "Win64";
                    case BuildConfigPluginBuildEditorPlatform.Mac:
                        return "Mac";
                    case BuildConfigPluginBuildEditorPlatform.Linux:
                        return "Linux";
                    default:
                        throw new NotSupportedException();
                }
            }).ToArray(), localExecutor);
            var gameConfig = ComputeTargetConfig("Game", distribution.Build.Game, localExecutor);

            // Compute directories to clean.
            var cleanDirectories = new List<string>();
            foreach (var filespec in distribution.Clean?.Filespecs ?? new string[0])
            {
                cleanDirectories.Add(filespec);
            }

            // If strict includes is turned on at the distribution level, enable it
            // regardless of the --strict-includes setting.
            var strictIncludesAtPluginLevel = distribution.Build?.StrictIncludes ?? false;

            // Compute packaging settings.
            var isForMarketplaceSubmission = distribution.Package != null &&
                (distribution.Package.Marketplace ?? false);
            var versionInfo = await _versioning.ComputeVersionNameAndNumberAsync(engineSpec, true, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(commandlinePluginVersionName))
            {
                versionInfo.versionName = commandlinePluginVersionName;
            }
            if (commandlinePluginVersionNumber.HasValue)
            {
                versionInfo.versionNumber = commandlinePluginVersionNumber.Value.ToString();
            }

            // Validate packaging settings. If the plugin has custom configuration files
            // but does not specify a filter file, then it's almost certainly misconfigured
            // as the plugin configuration files will not be included for distribution.
            var configPath = Path.Combine(repositoryRoot, pluginInfo.PluginName, "Config");
            if (Directory.Exists(configPath) &&
                Directory.GetFiles(configPath, "*.ini").Length > 0 &&
                distribution?.Package?.Filter == null)
            {
                throw new BuildMisconfigurationException("This plugin contains configuration files underneath Config/, but no filter file was specified for Package.Filter in BuildConfig.json. This almost certainly means the distribution is misconfigured, as plugin configuration files will not be included in the package unless you explicitly include them with a filter file.");
            }

            // Write dynamic build includes for tests and deployments.
            var (scriptNodeIncludes, scriptMacroIncludes) = await WriteDynamicBuildGraphIncludeAsync(
                buildGraphEnvironment,
                localExecutor,
                distribution,
                executeTests,
                executeDeployment);

            // Compute the Gauntlet config paths.
            var gauntletPaths = new List<string>();
            if (distribution.Gauntlet != null)
            {
                foreach (var path in distribution.Gauntlet.ConfigFiles ?? new string[0])
                {
                    gauntletPaths.Add(path);
                }
            }

            // Compute copyright header.
            var copyrightHeader = string.Empty;
            var copyrightExcludes = string.Empty;
            if (isForMarketplaceSubmission)
            {
                if (pluginInfo.Copyright == null)
                {
                    throw new BuildMisconfigurationException("You must configure the 'Copyright' section in BuildConfig.json to package for the Marketplace.");
                }
                else if (pluginInfo.Copyright.Header == null)
                {
                    throw new BuildMisconfigurationException("You must configure the 'Copyright.Header' value in BuildConfig.json to package for the Marketplace.");
                }
                else if (!pluginInfo.Copyright.Header.Contains("%Y"))
                {
                    throw new BuildMisconfigurationException("The configured copyright header must have a %Y placeholder for the current year to package for the Marketplace.");
                }
                else
                {
                    copyrightHeader = pluginInfo.Copyright.Header.Replace("%Y", DateTime.UtcNow.Year.ToString());
                    if (pluginInfo.Copyright.ExcludePaths != null)
                    {
                        copyrightExcludes = string.Join(";", pluginInfo.Copyright.ExcludePaths);
                    }
                }
            }

            // Compute global environment variables.
            var globalEnvironmentVariables = new Dictionary<string, string>
            {
                { "BUILDING_FOR_REDISTRIBUTION", "true" },
            };
            if (distribution.EnvironmentVariables != null)
            {
                foreach (var kv in distribution.EnvironmentVariables)
                {
                    globalEnvironmentVariables[kv.Key] = kv.Value;
                }
            }

            // Compute final settings for BuildGraph.
            return new BuildSpecification
            {
                Engine = engineSpec,
                BuildGraphScript = BuildGraphScriptSpecification.ForPlugin(),
                BuildGraphTarget = "End",
                BuildGraphSettings = new Dictionary<string, string>
                {
                    // Environment options
                    { $"UETPath", $"__UET_PATH__" },
                    { "UETGlobalArgs", _globalArgsProvider?.GlobalArgsString ?? string.Empty },
                    { "EnginePath", "__ENGINE_PATH__" },
                    { $"TempPath", $"__REPOSITORY_ROOT__/.uet/tmp" },
                    { $"ProjectRoot", $"__REPOSITORY_ROOT__" },
                    { $"PluginDirectory", isPluginRooted ? $"__REPOSITORY_ROOT__" : $"__REPOSITORY_ROOT__/{pluginInfo.PluginName}" },
                    { $"PluginName", pluginInfo.PluginName },
                    { $"Distribution", distribution.Name },
                    { $"ArtifactExportPath", "__ARTIFACT_EXPORT_PATH__" },

                    // Dynamic graph
                    { "ScriptNodeIncludes", scriptNodeIncludes },
                    { "ScriptMacroIncludes", scriptMacroIncludes },

                    // General options
                    { "IsUnrealEngine5", "true" },

                    // Clean options
                    { $"CleanDirectories", string.Join(";", cleanDirectories) },

                    // Build options
                    { $"ExecuteBuild", executeBuild ? "true" : "false" },
                    { $"EditorTargetPlatforms", string.Join(";", editorTargetPlatforms) },
                    { $"GameTargetPlatforms", gameConfig.TargetPlatforms },
                    { $"GameConfigurations", gameConfig.Configurations },
                    { $"MacPlatforms", $"IOS;Mac" },
                    { $"StrictIncludes", strictIncludes || strictIncludesAtPluginLevel ? "true" : "false" },
                    { $"Allow2019", "false" },
                    { $"EnginePrefix", "Unreal" },

                    // Package options
                    { $"ExecutePackage", executePackage ? "true" : "false" },
                    { "VersionNumber", versionInfo.versionNumber },
                    { "VersionName", versionInfo.versionName },
                    { "PackageFolder", distribution.Package?.OutputFolderName ?? "Packaged" },
                    { "PackageInclude", GetFilterInclude(repositoryRoot, distribution) },
                    { "PackageExclude", GetFilterExclude(repositoryRoot, distribution) },
                    { "IsForMarketplaceSubmission", isForMarketplaceSubmission ? "true" : "false" },
                    { "CopyrightHeader", copyrightHeader },
                    { "CopyrightExcludes", copyrightExcludes },
                },
                BuildGraphEnvironment = buildGraphEnvironment,
                BuildGraphRepositoryRoot = repositoryRoot,
                UETPath = _selfLocation.GetUETLocalLocation(),
                GlobalEnvironmentVariables = globalEnvironmentVariables,
                ProjectFolderName = null,
                ArtifactExportPath = Environment.CurrentDirectory,
            };
        }

        public async Task<BuildSpecification> BuildConfigProjectToBuildSpecAsync(
            BuildEngineSpecification engineSpec,
            BuildGraphEnvironment buildGraphEnvironment,
            BuildConfigProjectDistribution distribution,
            string repositoryRoot,
            bool executeBuild,
            bool executeTests,
            bool executeDeployment,
            bool strictIncludes,
            bool localExecutor)
        {
            // Determine build matrix.
            var editorTarget = distribution.Build.Editor?.Target ?? "UnrealEditor";
            var gameConfig = ComputeTargetConfig("Game", distribution.Build.Game, localExecutor);
            var clientConfig = ComputeTargetConfig("Client", distribution.Build.Client, localExecutor);
            var serverConfig = ComputeTargetConfig("Server", distribution.Build.Server, localExecutor);

            // Write dynamic build includes for tests and deployments.
            var (scriptNodeIncludes, scriptMacroIncludes) = await WriteDynamicBuildGraphIncludeAsync(
                buildGraphEnvironment,
                localExecutor,
                distribution,
                executeTests,
                executeDeployment);

            // Compute final settings for BuildGraph.
            return new BuildSpecification
            {
                Engine = engineSpec,
                BuildGraphScript = BuildGraphScriptSpecification.ForProject(),
                BuildGraphTarget = "End",
                BuildGraphSettings = new Dictionary<string, string>
                {
                    // Environment options
                    { $"UETPath", $"__UET_PATH__" },
                    { "EnginePath", "__ENGINE_PATH__" },
                    { $"TempPath", $"__REPOSITORY_ROOT__/.uet/tmp" },
                    { $"ProjectRoot", $"__REPOSITORY_ROOT__/{distribution.FolderName}" },
                    { $"RepositoryRoot", $"__REPOSITORY_ROOT__" },
                    { $"ArtifactExportPath", "__ARTIFACT_EXPORT_PATH__" },

                    // Dynamic graph
                    { "ScriptNodeIncludes", scriptNodeIncludes },
                    { "ScriptMacroIncludes", scriptMacroIncludes },

                    // General options
                    { $"UProjectPath", $"__REPOSITORY_ROOT__/{distribution.FolderName}/{distribution.ProjectName}.uproject" },
                    { $"Distribution", distribution.Name },
                    { "IsUnrealEngine5", "true" },

                    // Build options
                    { $"ExecuteBuild", executeBuild ? "true" : "false" },
                    { $"EditorTarget", editorTarget },
                    { $"GameTargets", gameConfig.Targets },
                    { $"ClientTargets", clientConfig.Targets },
                    { $"ServerTargets", serverConfig.Targets },
                    { $"GameTargetPlatforms", gameConfig.TargetPlatforms },
                    { $"ClientTargetPlatforms", clientConfig.TargetPlatforms },
                    { $"ServerTargetPlatforms", serverConfig.TargetPlatforms },
                    { $"GameConfigurations", gameConfig.Configurations },
                    { $"ClientConfigurations", clientConfig.Configurations },
                    { $"ServerConfigurations", serverConfig.Configurations },
                    { $"MacPlatforms", $"IOS;Mac" },
                    { $"StrictIncludes", strictIncludes ? "true" : "false" },

                    // Stage options
                    { $"StageDirectory", $"__REPOSITORY_ROOT__/{distribution.FolderName}/Saved/StagedBuilds" },
                },
                BuildGraphEnvironment = buildGraphEnvironment,
                BuildGraphRepositoryRoot = repositoryRoot,
                UETPath = _selfLocation.GetUETLocalLocation(),
                ProjectFolderName = distribution.FolderName,
                ArtifactExportPath = Environment.CurrentDirectory,
            };
        }

        public async Task<BuildSpecification> PluginPathSpecToBuildSpecAsync(
            BuildEngineSpecification engineSpec,
            BuildGraphEnvironment buildGraphEnvironment,
            PathSpec pathSpec,
            bool shipping,
            bool strictIncludes,
            string[] extraPlatforms,
            bool package,
            bool marketplace,
            string? commandlinePluginVersionName,
            long? commandlinePluginVersionNumber)
        {
            var targetPlatform = OperatingSystem.IsWindows() ? "Win64" : "Mac";
            var gameConfigurations = shipping ? "Shipping" : "Development";

            var versionInfo = await _versioning.ComputeVersionNameAndNumberAsync(engineSpec, true, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(commandlinePluginVersionName))
            {
                versionInfo.versionName = commandlinePluginVersionName;
            }
            if (commandlinePluginVersionNumber.HasValue)
            {
                versionInfo.versionNumber = commandlinePluginVersionNumber.Value.ToString();
            }

            // If building for the Marketplace, compute the copyright header
            // automatically from the .uplugin CreatedBy field.
            var copyrightHeader = string.Empty;
            if (marketplace)
            {
                var pluginFile = JsonSerializer.Deserialize(
                    await File.ReadAllTextAsync(pathSpec.UPluginPath!),
                    ProjectPluginFileJsonSerializerContext.Default.UPluginFile);
                if (string.IsNullOrWhiteSpace(pluginFile?.CreatedBy))
                {
                    _logger.LogWarning(".uplugin file is missing 'CreatedBy' field. Copyright headers set for Marketplace submission may not the Marketplace guildlines. Please set the 'CreatedBy' field or use a 'BuildConfig.json' to build this plugin.");
                    copyrightHeader = $"Copyright %Y. All Rights Reserved.";
                }
                else
                {
                    copyrightHeader = $"Copyright {pluginFile?.CreatedBy} %Y. All Rights Reserved.";
                }
            }

            // Compute final settings for BuildGraph.
            return new BuildSpecification
            {
                Engine = engineSpec,
                BuildGraphScript = BuildGraphScriptSpecification.ForPlugin(),
                BuildGraphTarget = "End",
                BuildGraphSettings = new Dictionary<string, string>
                {
                    // Environment options
                    { $"UETPath", $"__UET_PATH__" },
                    { "UETGlobalArgs", _globalArgsProvider?.GlobalArgsString ?? string.Empty },
                    { "EnginePath", "__ENGINE_PATH__" },
                    { $"TempPath", $"__REPOSITORY_ROOT__/.uet/tmp" },
                    { $"ProjectRoot", $"__REPOSITORY_ROOT__" },
                    { $"PluginDirectory", $"__REPOSITORY_ROOT__" },
                    { $"PluginName", Path.GetFileNameWithoutExtension(pathSpec.UPluginPath)! },
                    // @note: This is only used for naming the package ZIPs now.
                    { $"Distribution", marketplace ? "Marketplace" : "Redistributable" },
                    { $"ArtifactExportPath", "__ARTIFACT_EXPORT_PATH__" },

                    // Dynamic graph
                    { "ScriptIncludes", string.Empty },

                    // General options
                    { "IsUnrealEngine5", "true" },

                    // Clean options
                    { $"CleanDirectories", string.Empty },

                    // Build options
                    { $"ExecuteBuild", "true" },
                    { $"EditorTargetPlatforms", targetPlatform },
                    { $"GameTargetPlatforms", string.Join(";", new[] { targetPlatform }.Concat(extraPlatforms)) },
                    { $"GameConfigurations", gameConfigurations },
                    { $"MacPlatforms", $"IOS;Mac" },
                    { $"StrictIncludes", strictIncludes ? "true" : "false" },
                    { $"Allow2019", "false" },
                    { $"EnginePrefix", "Unreal" },

                    // Package options
                    { $"ExecutePackage", package ? "true" : "false" },
                    { "VersionNumber", versionInfo.versionNumber },
                    { "VersionName", versionInfo.versionName },
                    { "PackageFolder", marketplace ? "Marketplace" : "Redistributable" },
                    { "PackageInclude", string.Empty },
                    { "PackageExclude", string.Empty },
                    { "IsForMarketplaceSubmission", marketplace ? "true" : "false" },
                    { "CopyrightHeader", copyrightHeader },
                    { "CopyrightExcludes", string.Empty },
                },
                BuildGraphEnvironment = buildGraphEnvironment,
                BuildGraphRepositoryRoot = pathSpec.DirectoryPath,
                UETPath = _selfLocation.GetUETLocalLocation(),
                GlobalEnvironmentVariables = new Dictionary<string, string>
                {
                    { "BUILDING_FOR_REDISTRIBUTION", "true" },
                },
                ProjectFolderName = null,
                ArtifactExportPath = Environment.CurrentDirectory,
            };
        }

        public BuildSpecification ProjectPathSpecToBuildSpec(
            BuildEngineSpecification engineSpec,
            BuildGraphEnvironment buildGraphEnvironment,
            PathSpec pathSpec,
            bool shipping,
            bool strictIncludes,
            string[] extraPlatforms)
        {
            // Use heuristics to guess the targets for this build.
            string editorTarget;
            string gameTarget;
            if (Directory.Exists(Path.Combine(pathSpec.DirectoryPath, "Source")))
            {
                var files = Directory.GetFiles(Path.Combine(pathSpec.DirectoryPath, "Source"), "*.Target.cs");
                editorTarget = files.Where(x => x.EndsWith("Editor.Target.cs")).Select(x => Path.GetFileName(x)).First();
                editorTarget = editorTarget.Substring(0, editorTarget.LastIndexOf(".Target.cs"));
                gameTarget = editorTarget.Substring(0, editorTarget.LastIndexOf("Editor"));
            }
            else
            {
                editorTarget = "UnrealEditor";
                gameTarget = "UnrealGame";
            }

            var gameTargetPlatform = OperatingSystem.IsWindows() ? "Win64" : "Mac";
            var gameConfigurations = shipping ? "Shipping" : "Development";

            // Compute final settings for BuildGraph.
            return new BuildSpecification
            {
                Engine = engineSpec,
                BuildGraphScript = BuildGraphScriptSpecification.ForProject(),
                BuildGraphTarget = "End",
                BuildGraphSettings = new Dictionary<string, string>
                {
                    // Environment options
                    { $"UETPath", $"__UET_PATH__" },
                    { "EnginePath", "__ENGINE_PATH__" },
                    { $"TempPath", $"__REPOSITORY_ROOT__/.uet/tmp" },
                    { $"ProjectRoot", $"__REPOSITORY_ROOT__" },
                    { $"RepositoryRoot", $"__REPOSITORY_ROOT__" },
                    { $"ArtifactExportPath", "__ARTIFACT_EXPORT_PATH__" },

                    // Dynamic graph
                    { "ScriptIncludes", string.Empty },

                    // General options
                    { $"UProjectPath", $"__REPOSITORY_ROOT__/{Path.GetFileName(pathSpec.UProjectPath)}" },
                    { $"Distribution", "None" },
                    { "IsUnrealEngine5", "true" },

                    // Build options
                    { $"ExecuteBuild", "true" },
                    { $"EditorTarget", editorTarget },
                    { $"GameTargets", gameTarget },
                    { $"ClientTargets", string.Empty },
                    { $"ServerTargets", string.Empty },
                    { $"GameTargetPlatforms", string.Join(";", new[] { gameTargetPlatform }.Concat(extraPlatforms)) },
                    { $"ClientTargetPlatforms", string.Empty },
                    { $"ServerTargetPlatforms", string.Empty },
                    { $"GameConfigurations", gameConfigurations },
                    { $"ClientConfigurations", string.Empty },
                    { $"ServerConfigurations", string.Empty },
                    { $"MacPlatforms", $"IOS;Mac" },
                    { $"StrictIncludes", strictIncludes ? "true" : "false" },

                    // Stage options
                    { $"StageDirectory", $"__REPOSITORY_ROOT__/Saved/StagedBuilds" },
                },
                BuildGraphEnvironment = buildGraphEnvironment,
                BuildGraphRepositoryRoot = pathSpec.DirectoryPath,
                UETPath = _selfLocation.GetUETLocalLocation(),
                ProjectFolderName = string.Empty,
                ArtifactExportPath = Environment.CurrentDirectory,
            };
        }
    }
}

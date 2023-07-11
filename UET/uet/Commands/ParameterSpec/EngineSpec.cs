﻿namespace UET.Commands.EngineSpec
{
    using Redpoint.Registry;
    using Redpoint.Uet.Configuration.Engine;
    using Redpoint.Uet.Configuration.Project;
    using System;
    using System.CommandLine;
    using System.CommandLine.Parsing;
    using System.Text.Json;
    using System.Text.RegularExpressions;

    internal class EngineSpec
    {
        private EngineSpec()
        {
        }

        private static Regex _versionRegex = new Regex("^[45]\\.[0-9]+(EA)?$");

        public static ParseArgument<EngineSpec> ParseEngineSpec(
            Option<PathSpec> pathSpec,
            Option<DistributionSpec?>? distributionOpt)
        {
            return (result) =>
            {
                // If the engine is specified, use it.
                if (result.Tokens.Count > 0)
                {
                    return ParseEngineSpecWithoutPath(result);
                }

                // Otherwise, take a look at the path spec value to see if we
                // can figure out the target engine from the project file.
                PathSpec? path = null;
                DistributionSpec? distribution = null;
                try
                {
                    path = result.GetValueForOption(pathSpec);
                }
                catch (InvalidOperationException)
                {
                    return null!;
                }
                if (distributionOpt != null)
                {
                    try
                    {
                        distribution = result.GetValueForOption(distributionOpt);
                    }
                    catch (InvalidOperationException)
                    {
                        return null!;
                    }
                }
                if (path == null)
                {
                    result.ErrorMessage = $"Can't automatically detect the appropriate engine because the --{pathSpec.Name} option was invalid.";
                    return null!;
                }
                switch (path.Type)
                {
                    case PathSpecType.UProject:
                        // Read the .uproject file as JSON and get the engine value from it.
                        using (var uprojectFileStream = new FileStream(path.UProjectPath!, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            var projectFile = JsonSerializer.Deserialize<UProjectFile>(
                                uprojectFileStream,
                                SourceGenerationContext.Default.UProjectFile);
                            if (projectFile?.EngineAssociation != null)
                            {
                                var engineSpec = TryParseEngine(projectFile.EngineAssociation, EngineParseFlags.WindowsRegistry | EngineParseFlags.WindowsFolder | EngineParseFlags.MacFolder);
                                if (engineSpec == null)
                                {
                                    result.ErrorMessage = $"The '.uproject' file specifies an engine that is not installed or can't be found ({projectFile.EngineAssociation}).";
                                    return null!;
                                }
                                return engineSpec;
                            }
                            result.ErrorMessage = $"The '.uproject' file does not specify an engine via EngineAssociation; use --{result.Argument.Name} to specify the engine instead.";
                            return null!;
                        }
                    case PathSpecType.UPlugin:
                        // Can't automatically infer the engine version for plugins.
                        result.ErrorMessage = $"The engine version can not be inferred automatically for plugins; use --{result.Argument.Name} to specify the engine instead.";
                        return null!;
                    case PathSpecType.BuildConfig:
                        // If this build configuration is for an engine, then return SelfEngine.
                        var selectedEngineDistribution = distribution?.Distribution as BuildConfigEngineDistribution;
                        if (selectedEngineDistribution != null)
                        {
                            return new EngineSpec
                            {
                                OriginalSpec = string.Empty,
                                Type = EngineSpecType.SelfEngine,
                            };
                        }

                        // If this build configuration is for a project, determine which project file based on
                        // the distribution and then read the engine association from that project file.
                        var selectedProjectDistribution = distribution?.Distribution as BuildConfigProjectDistribution;
                        if (selectedProjectDistribution == null || distributionOpt == null)
                        {
                            result.ErrorMessage = $"The engine version can not be inferred automatically for plugins; use --{result.Argument.Name} to specify the engine instead.";
                            return null!;
                        }

                        var uprojectPath = System.IO.Path.Combine(path.DirectoryPath, selectedProjectDistribution.FolderName, $"{selectedProjectDistribution.ProjectName}.uproject");
                        if (!File.Exists(uprojectPath))
                        {
                            result.ErrorMessage = $"The distribution '{distribution}' specified by --{distributionOpt.Name} refers to the project file '{uprojectPath}', but this project file does not exist on disk, so the engine version can not be inferred.";
                            return null!;
                        }

                        using (var uprojectFileStream = new FileStream(uprojectPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            var projectFile = JsonSerializer.Deserialize<UProjectFile>(
                                uprojectFileStream,
                                SourceGenerationContext.Default.UProjectFile);
                            if (projectFile?.EngineAssociation != null)
                            {
                                var engineSpec = TryParseEngine(projectFile.EngineAssociation, EngineParseFlags.WindowsRegistry | EngineParseFlags.WindowsFolder | EngineParseFlags.MacFolder);
                                if (engineSpec == null)
                                {
                                    result.ErrorMessage = $"The '.uproject' file (referred to by the '{distribution}' distribution) specifies an engine that is not installed or can't be found ({projectFile.EngineAssociation}).";
                                    return null!;
                                }
                                return engineSpec;
                            }
                            result.ErrorMessage = $"The '.uproject' file (referred to by the '{distribution}' distribution) does not specify an engine via EngineAssociation; use --{result.Argument.Name} to specify the engine instead.";
                            return null!;
                        }
                }

                result.ErrorMessage = $"Can't automatically detect the appropriate engine because the --{pathSpec.Name} option was invalid.";
                return null!;
            };
        }

        [Flags]
        private enum EngineParseFlags
        {
            None = 0,

            UEFS = 1 << 0,
            WindowsRegistry = 1 << 1,
            WindowsFolder = 1 << 2,
            MacFolder = 1 << 3,
            AbsolutePath = 1 << 4,
            Git = 1 << 5,

            All = 0xFF,
        }

        public static EngineSpec? TryParseEngineSpecExact(string engine)
        {
            return TryParseEngine(engine);
        }

        private static EngineSpec? TryParseEngine(string engine, EngineParseFlags flags = EngineParseFlags.All)
        {
            if ((flags & EngineParseFlags.UEFS) != 0)
            {
                // Detect UEFS tags.
                if (engine.StartsWith("uefs:"))
                {
                    return new EngineSpec
                    {
                        Type = EngineSpecType.UEFSPackageTag,
                        OriginalSpec = engine,
                        UEFSPackageTag = engine.Substring("uefs:".Length),
                    };
                }
            }

            if ((flags & EngineParseFlags.Git) != 0)
            {
                // Detect commits.
                if (engine.StartsWith("git:"))
                {
                    // <commit>@<url>,f:<folder>,z:<zip>,...
                    var value = engine.Substring("git:".Length);
                    var firstAt = value.IndexOf('@');
                    var commit = value.Substring(0, firstAt);
                    value = value.Substring(firstAt + 1);
                    var firstComma = value.IndexOf(",");
                    var url = firstComma == -1 ? value : value.Substring(0, firstComma);
                    string[] layers;
                    if (firstComma != -1)
                    {
                        layers = value.Substring(firstComma + 1).Split(',');
                    }
                    else
                    {
                        layers = Array.Empty<string>();
                    }
                    // @note: Folders aren't used yet.
                    var folders = layers.Where(x => x.StartsWith("f:")).Select(x => x.Substring(2)).ToArray();
                    var zips = layers.Where(x => x.StartsWith("z:")).Select(x => x.Substring(2)).ToArray();

                    return new EngineSpec
                    {
                        Type = EngineSpecType.GitCommit,
                        OriginalSpec = engine,
                        GitUrl = url,
                        GitCommit = commit,
                        FolderLayers = folders,
                        ZipLayers = zips,
                    };
                }
            }

            if (OperatingSystem.IsWindows())
            {
                if ((flags & EngineParseFlags.WindowsRegistry) != 0)
                {
                    // Try to get the engine from the registry.
                    using (var stack = RegistryStack.OpenPath($@"HKLM:\SOFTWARE\EpicGames\Unreal Engine\{engine}"))
                    {
                        if (stack.Exists)
                        {
                            var registryBasedPath = stack.Key.GetValue("InstalledDirectory") as string;
                            if (registryBasedPath != null && Directory.Exists(registryBasedPath))
                            {
                                return new EngineSpec
                                {
                                    Type = EngineSpecType.Version,
                                    Version = engine,
                                    OriginalSpec = engine,
                                    Path = registryBasedPath,
                                };
                            }
                        }
                    }
                }

                if ((flags & EngineParseFlags.WindowsFolder) != 0)
                {
                    // If the engine matches a version regex [45]\.[0-9]+(EA)?, check the Program Files folder.
                    if (_versionRegex.IsMatch(engine))
                    {
                        var candidatePath = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                            "Epic Games",
                            $"UE_{engine}");
                        if (Directory.Exists(candidatePath))
                        {
                            return new EngineSpec
                            {
                                Type = EngineSpecType.Version,
                                Version = engine,
                                OriginalSpec = engine,
                                Path = candidatePath,
                            };
                        }
                    }
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                if ((flags & EngineParseFlags.MacFolder) != 0)
                {
                    // If the engine matches a version regex [45]\.[0-9]+(EA)?, check the /Users/Shared folder.
                    if (_versionRegex.IsMatch(engine))
                    {
                        var candidatePath = System.IO.Path.Combine(
                            "/Users/Shared",
                            "Epic Games",
                            $"UE_{engine}");
                        if (Directory.Exists(candidatePath))
                        {
                            return new EngineSpec
                            {
                                Type = EngineSpecType.Version,
                                Version = engine,
                                OriginalSpec = engine,
                                Path = candidatePath,
                            };
                        }
                    }
                }
            }

            if ((flags & EngineParseFlags.AbsolutePath) != 0)
            {
                // If the engine path ends in a \, remove it because it creates problems when the
                // path is passed in on the command line (usually escaping a quote " ...)
                engine = engine.TrimEnd('\\');

                // If this is an absolute path to an engine, use that.
                if (System.IO.Path.IsPathRooted(engine) &&
                    Directory.Exists(engine))
                {
                    return new EngineSpec
                    {
                        Type = EngineSpecType.Path,
                        OriginalSpec = engine,
                        Path = engine,
                    };
                }
            }

            // Could not locate engine.
            return null;
        }

        public static EngineSpec ParseEngineSpecWithoutPath(ArgumentResult result)
        {
            var engine = string.Join(" ", result.Tokens);

            var engineResult = TryParseEngine(engine);
            if (engineResult != null)
            {
                return engineResult;
            }

            result.ErrorMessage = "The specified engine could not be found. Engines can be specified as a version number like '5.2', a UEFS tag like 'uefs:...' or an absolute path.";
            return null!;
        }

        public required EngineSpecType Type { get; init; }

        public required string OriginalSpec { get; init; }

        public string? Version { get; private init; }

        public string? Path { get; private init; }

        public string? UEFSPackageTag { get; private init; }

        public string? GitUrl { get; private init; }

        public string? GitCommit { get; private init; }

        public string[]? FolderLayers { get; private init; }

        public string[]? ZipLayers { get; private init; }

        public override string ToString()
        {
            if (Type == EngineSpecType.Version && OriginalSpec != Path)
            {
                return $"{Version} ({Path})";
            }

            return OriginalSpec;
        }
    }
}

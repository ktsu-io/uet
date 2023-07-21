﻿namespace Redpoint.Uet.SdkManagement
{
    using Microsoft.Extensions.Logging;
    using Redpoint.ProcessExecution;
    using Redpoint.Uet.Core;
    using Redpoint.Uet.SdkManagement.WindowsSdk;
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics.CodeAnalysis;
    using System.IO.Compression;
    using System.Linq;
    using System.Net.Http.Json;
    using System.Reflection;
    using System.Runtime.Versioning;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Web;

    [SupportedOSPlatform("windows")]
    public class WindowsAutoSdkSetup : IAutoSdkSetup
    {
        private readonly ILogger<WindowsAutoSdkSetup> _logger;
        private readonly IProcessExecutor _processExecutor;
        private readonly ISimpleDownloadProgress _simpleDownloadProgress;

        public WindowsAutoSdkSetup(
            ILogger<WindowsAutoSdkSetup> logger,
            IProcessExecutor processExecutor,
            ISimpleDownloadProgress simpleDownloadProgress)
        {
            _logger = logger;
            _processExecutor = processExecutor;
            _simpleDownloadProgress = simpleDownloadProgress;
        }

        public string[] PlatformNames => new[] { "Windows", "Win64" };

        private static ConcurrentDictionary<string, Assembly> _cachedCompiles = new ConcurrentDictionary<string, Assembly>();

        private enum CurrentlyIn
        {
            None,
            PreferredWindowsSdk,
            PreferredVisualCpp,
            VsSuggestedComponents,
            Vs2022SuggestedComponents,
        }

        static readonly Regex _versionNumberRegex = new Regex("VersionNumber.Parse\\(\"([0-9\\.]+)\"\\)");
        static readonly Regex _versionNumberRangeRegex = new Regex("VersionNumberRange.Parse\\(\"([0-9\\.]+)\", \"([0-9\\.]+)\"\\)");
        static readonly Regex _elementLine = new Regex("\"([A-Za-z0-9\\.]+)\",");

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Assembly.Load is self-contained")]
        [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "GetType references a type created in memory")]
        internal static Task<(
            string windowsSdkPreferredVersion,
            string visualCppMinimumVersion,
            string[] suggestedComponents)> ParseVersions(
            string microsoftPlatformSdkFileContent)
        {
            string? windowsSdkPreferredVersion = null;
            string? visualCppMinimumVersion = null;
            List<string> suggestedComponents = new List<string>();

            var currentlyIn = CurrentlyIn.None;
            foreach (var line in microsoftPlatformSdkFileContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("//"))
                {
                    continue;
                }
                if (line.EndsWith(";"))
                {
                    currentlyIn = CurrentlyIn.None;
                }

                if (line.Contains(" PreferredWindowsSdkVersions "))
                {
                    currentlyIn = CurrentlyIn.PreferredWindowsSdk;
                    continue;
                }
                else if (line.Contains(" PreferredVisualCppVersions "))
                {
                    currentlyIn = CurrentlyIn.PreferredVisualCpp;
                    continue;
                }
                else if (line.Contains(" VisualStudioSuggestedComponents "))
                {
                    currentlyIn = CurrentlyIn.VsSuggestedComponents;
                    continue;
                }
                else if (line.Contains(" VisualStudio2022SuggestedComponents "))
                {
                    currentlyIn = CurrentlyIn.Vs2022SuggestedComponents;
                    continue;
                }

                switch (currentlyIn)
                {
                    case CurrentlyIn.PreferredWindowsSdk:
                        {
                            var match = _versionNumberRegex.Match(line);
                            if (match.Success)
                            {
                                windowsSdkPreferredVersion = match.Groups[1].Value;
                                currentlyIn = CurrentlyIn.None;
                            }
                            break;
                        }
                    case CurrentlyIn.PreferredVisualCpp:
                        {
                            var match = _versionNumberRangeRegex.Match(line);
                            if (match.Success && line.Contains("VS2022"))
                            {
                                visualCppMinimumVersion = match.Groups[1].Value;
                                currentlyIn = CurrentlyIn.None;
                            }
                            break;
                        }
                    case CurrentlyIn.VsSuggestedComponents:
                        {
                            var match = _elementLine.Match(line);
                            if (match.Success)
                            {
                                suggestedComponents.Add(match.Groups[1].Value);
                            }
                            break;
                        }
                    case CurrentlyIn.Vs2022SuggestedComponents:
                        {
                            var match = _elementLine.Match(line);
                            if (match.Success)
                            {
                                suggestedComponents.Add(match.Groups[1].Value);
                            }
                            break;
                        }
                }
            }

            if (windowsSdkPreferredVersion == null ||
                visualCppMinimumVersion == null)
            {
                throw new InvalidOperationException("Unable to parse versions from MicrosoftPlatformSDK.Versions.cs");
            }

            return Task.FromResult<(string windowsSdkPreferredVersion, string visualCppMinimumVersion, string[] suggestedComponents)>((
                windowsSdkPreferredVersion,
                visualCppMinimumVersion,
                suggestedComponents.ToArray()));
        }

        private async Task<(
            VersionNumber windowsSdkPreferredVersion,
            VersionNumber visualCppMinimumVersion,
            string[] suggestedComponents)> GetVersions(string unrealEnginePath)
        {
            var microsoftPlatformSdkFileContent = await File.ReadAllTextAsync(Path.Combine(
                unrealEnginePath,
                "Engine",
                "Source",
                "Programs",
                "UnrealBuildTool",
                "Platform",
                "Windows",
                "MicrosoftPlatformSDK.Versions.cs"));
            var rawVersions = await ParseVersions(microsoftPlatformSdkFileContent);
            return (
                VersionNumber.Parse(rawVersions.windowsSdkPreferredVersion),
                VersionNumber.Parse(rawVersions.visualCppMinimumVersion),
                rawVersions.suggestedComponents);
        }

        public async Task<string> ComputeSdkPackageId(string unrealEnginePath, CancellationToken cancellationToken)
        {
            var versions = await GetVersions(unrealEnginePath);
            return $"{versions.windowsSdkPreferredVersion}-{versions.visualCppMinimumVersion}";
        }

        public async Task GenerateSdkPackage(string unrealEnginePath, string sdkPackagePath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Retrieving desired versions from Unreal Engine source code...");
            var versions = await GetVersions(unrealEnginePath);

            const string rootManifestUrl = "https://aka.ms/vs/17/release/channel";
            var serializerOptions = new JsonSerializerOptions
            {
                Converters =
                {
                    new VisualStudioManifestPackageDependencyJsonConverter()
                }
            };

            // Load the root manifest.
            _logger.LogInformation("Downloading the root manifest...");
            VisualStudioManifest rootManifest;
            using (var client = new HttpClient())
            {
                rootManifest = (await client.GetFromJsonAsync(rootManifestUrl, new VisualStudioJsonSerializerContext(new JsonSerializerOptions(serializerOptions)).VisualStudioManifest))!;
            }

            // In the root manifest, locate the manifest with all the packages.
            _logger.LogInformation("Downloading the package manifest...");
            VisualStudioManifest packagesManifest;
            using (var client = new HttpClient())
            {
                var packagesManifestUrl = rootManifest.ChannelItems!.First(x => x.Type == "Manifest");
                packagesManifest = (await client.GetFromJsonAsync(packagesManifestUrl.Payloads!.First().Url, new VisualStudioJsonSerializerContext(new JsonSerializerOptions(serializerOptions)).VisualStudioManifest))!;
            }

            // Generate a dictionary of components based on their ID.
            _logger.LogInformation("Computing package map...");
            var componentLookup = new Dictionary<string, VisualStudioManifestChannelItem>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var package in packagesManifest.Packages!)
            {
                if (package.MachineArch != null && package.MachineArch != "x64")
                {
                    continue;
                }

                if (package.Language != null && package.Language != "en-US")
                {
                    continue;
                }

                if (!componentLookup.ContainsKey(package.Id!))
                {
                    componentLookup.Add(package.Id!, package);
                }
            }

            // Generate the list of components to install.
            var componentsToInstall = new HashSet<string>
            {
                // We need the setup configuration component, which provides the COM component
                // that Unreal Engine uses to locate Visual Studio installations.
                "Microsoft.VisualStudio.Setup.Configuration"
            };

            // Locate the VC components directly so we can add
            // them to the desired components.
            _logger.LogInformation("Determining MSVC compiler components to install...");
            var vcComponentSuffixes = new[]
            {
                ".tools.hostx64.targetx64.base",
                ".tools.hostx64.targetx64.res.base",
                ".crt.headers.base",
                ".crt.x64.desktop.base",
                ".crt.x64.store.base",
                ".crt.source.base",
                ".asan.headers.base",
                ".asan.x64.base"
            };
            var numericVersionSignifier = new Regex("^[0-9\\.]+$");
            var vcComponentsByVersionSignifier = new Dictionary<string, HashSet<string>>();
            foreach (var vcComponent in packagesManifest.Packages!.Where(x => x.Id!.StartsWith("microsoft.vc.", StringComparison.InvariantCultureIgnoreCase)))
            {
                var id = vcComponent.Id!.ToLowerInvariant();
                foreach (var suffix in vcComponentSuffixes)
                {
                    if (id.EndsWith(suffix))
                    {
                        var versionSignifier = id.Substring("microsoft.vc.".Length);
                        versionSignifier = versionSignifier.Substring(0, versionSignifier.Length - suffix.Length);
                        if (numericVersionSignifier.IsMatch(versionSignifier))
                        {
                            if (VersionNumber.Parse(vcComponent.Version!) >= versions.visualCppMinimumVersion)
                            {
                                if (!vcComponentsByVersionSignifier.ContainsKey(versionSignifier))
                                {
                                    vcComponentsByVersionSignifier[versionSignifier] = new HashSet<string>();
                                }

                                vcComponentsByVersionSignifier[versionSignifier].Add(vcComponent.Id!);
                            }
                        }
                    }
                }
            }

            // Pick the lowest version available (in case a new version of MSVC breaks things and UE hasn't been
            // updated to know about the breakage).
            var lowestVersion = vcComponentsByVersionSignifier.Keys.OrderBy(x => x).First();
            foreach (var component in vcComponentsByVersionSignifier[lowestVersion])
            {
                _logger.LogInformation($"Adding the following component to the install manifest: {component} (from MSVC)");
                componentsToInstall.Add(component);
            }

            // Try to find the Windows SDK.
            var winSdkVersion = $"{versions.windowsSdkPreferredVersion.Major}.{versions.windowsSdkPreferredVersion.Minor}.{versions.windowsSdkPreferredVersion.Patch}";
            foreach (var component in componentLookup.Keys)
            {
                if (component == $"Win10SDK_{winSdkVersion}" ||
                    component == $"Win11SDK_{winSdkVersion}")
                {
                    _logger.LogInformation($"Adding the following component to the install manifest: {component} (from Windows SDK)");
                    componentsToInstall.Add(component);
                }
            }

            // Add all the components recursively that are desired.
            void RecursivelyAddComponent(string componentId, string fromComponentId)
            {
                if (!componentLookup!.ContainsKey(componentId))
                {
                    return;
                }

                if (!componentsToInstall!.Contains(componentId))
                {
                    var component = componentLookup![componentId];

                    if (component.Type != "Workload")
                    {
                        _logger.LogInformation($"Adding the following component to the install manifest: {componentId} (dependency of {fromComponentId})");
                    }
                    componentsToInstall.Add(componentId);

                    if (component.Dependencies != null)
                    {
                        foreach (var dep in component.Dependencies)
                        {
                            // Ignore recommended and optional components.
                            if (dep.Value.Type == "Recommended" ||
                                dep.Value.Type == "Optional")
                            {
                                continue;
                            }

                            // Ignore a component if it requires something other than the Build Tools.
                            if (dep.Value.When != null)
                            {
                                if (!dep.Value.When.Contains("Microsoft.VisualStudio.Product.BuildTools"))
                                {
                                    continue;
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(dep.Value.Id))
                            {
                                RecursivelyAddComponent(dep.Value.Id, componentId);
                            }
                            else
                            {
                                RecursivelyAddComponent(dep.Key, componentId);
                            }
                        }
                    }
                }
            };
            foreach (var component in versions.suggestedComponents)
            {
                RecursivelyAddComponent(component, "Unreal Engine");
            }

            // Install all of the components.
            var componentsToInstallArray = componentsToInstall.ToArray();
            for (int i = 0; i < componentsToInstallArray.Length; i++)
            {
                var componentId = componentsToInstallArray[i];
                var component = componentLookup![componentId];

                _logger.LogInformation($"({i + 1}/{componentsToInstallArray.Length}) Processing component for installation: {component.Id}");

                switch (component.Type)
                {
                    case "Vsix":
                        {
                            await ExtractVsixComponent(component, sdkPackagePath, cancellationToken);
                        }
                        break;
                    case "Exe":
                        {
                            await ExtractExecutableComponent(component, sdkPackagePath, cancellationToken);
                        }
                        break;
                    case "Msi":
                        {
                            if (componentId == "Microsoft.VisualStudio.Setup.Configuration")
                            {
                                await ExtractMsiComponent(component, sdkPackagePath, cancellationToken);
                            }
                        }
                        break;
                }
            }

            // Write out the environment file.
            var msvcVersion = Path.GetFileName(Directory.GetDirectories(Path.Combine(sdkPackagePath, "VS2022", "VC", "Tools", "MSVC"))[0]);
            var msvcRedistVersion = Path.GetFileName(Directory.GetDirectories(Path.Combine(sdkPackagePath, "VS2022", "VC", "Redist", "MSVC"))[0]);
            var winsdkVersion = Path.GetFileName(Directory.GetDirectories(Path.Combine(sdkPackagePath, "Windows Kits", "10", "bin"))[0]);
            var envs = new Dictionary<string, string>
            {
                { "UES_VS_INSTANCE_ID", $"{versions.windowsSdkPreferredVersion}-{versions.visualCppMinimumVersion}" },
                { "VCToolsInstallDir", $"{Path.Combine("<root>", "VS2022", "VC", "Tools", "MSVC", msvcVersion)}\\" },
                { "VCToolsVersion", msvcVersion },
                { "VisualStudioVersion", "17.0" },
                { "VCINSTALLDIR", $"{Path.Combine("<root>", "VS2022", "VC")}\\" },
                { "DevEnvDir", $"{Path.Combine("<root>", "VS2022", "Common7", "IDE")}\\" },
                { "VCIDEInstallDir", $"{Path.Combine("<root>", "VS2022", "Common7", "IDE", "VC")}\\" },
                { "VCToolsRedistDir", $"{Path.Combine("<root>", "VS2022", "VC", "Redist", "MSVC", msvcRedistVersion)}\\" },
                { "VS170COMNTOOLS", $"{Path.Combine("<root>", "VS2022", "Common7", "Tools")}\\" },
                { "VSINSTALLDIR", $"{Path.Combine("<root>")}\\" },
                { "WindowsSdkBinPath", $"{Path.Combine("<root>", "Windows Kits", "10", "bin")}\\" },
                { "WindowsSdkDir", $"{Path.Combine("<root>", "Windows Kits", "10")}\\" },
                { "WindowsSDKLibVersion", $"{winsdkVersion}\\" },
                { "WindowsSdkVerBinPath", $"{Path.Combine("<root>", "Windows Kits", "10", "bin", winsdkVersion)}\\" },
                { "WindowsSDKVersion", $"{winsdkVersion}\\" },
                {
                    "+PATH",
                    string.Join(
                        ";",
                        new[]
                        {
                            $"{Path.Combine("<root>", "VS2022", "VC", "Tools", "MSVC", msvcVersion, "bin", "Hostx64", "x64")}",
                            $"{Path.Combine("<root>", "Windows Kits", "10", "bin", winsdkVersion, "x64")}",
                            $"{Path.Combine("<root>", "Windows Kits", "10", "bin", winsdkVersion, "x64", "ucrt")}"
                        }
                    )
                },
                {
                    "INCLUDE",
                    string.Join(
                        ";",
                        new[]
                        {
                            $"{Path.Combine("<root>", "VS2022", "VC", "Tools", "MSVC", msvcVersion, "include")}",
                            $"{Path.Combine("<root>", "Windows Kits", "10", "Include", winsdkVersion, "ucrt")}",
                            $"{Path.Combine("<root>", "Windows Kits", "10", "Include", winsdkVersion, "shared")}",
                            $"{Path.Combine("<root>", "Windows Kits", "10", "Include", winsdkVersion, "um")}",
                            $"{Path.Combine("<root>", "Windows Kits", "10", "Include", winsdkVersion, "winrt")}",
                            $"{Path.Combine("<root>", "Windows Kits", "10", "Include", winsdkVersion, "cppwinrt")}"
                        }
                    )
                },
                {
                    "LIB",
                    string.Join(
                        ";",
                        new[]
                        {
                            $"{Path.Combine("<root>", "VS2022", "VC", "Tools", "MSVC", msvcVersion, "lib", "x64")}",
                            $"{Path.Combine("<root>", "Windows Kits", "10", "Lib", winsdkVersion, "ucrt", "x64")}",
                            $"{Path.Combine("<root>", "Windows Kits", "10", "Lib", winsdkVersion, "um", "x64")}"
                        }
                    )
                },
            };
            File.WriteAllText(Path.Combine(sdkPackagePath, "envs.json"), JsonSerializer.Serialize(envs, new VisualStudioJsonSerializerContext(new JsonSerializerOptions { WriteIndented = true }).DictionaryStringString));
            var batchLines = new List<string>();
            foreach (var kv in envs)
            {
                var key = kv.Key;
                var append = false;
                if (kv.Key.StartsWith("+"))
                {
                    key = kv.Key.Substring(1);
                    append = true;
                }
                if (append)
                {
                    batchLines.Add($"@set {key}={kv.Value.Replace("<root>", "%~dp0")};%{key}%");
                }
                else
                {
                    batchLines.Add($"@set {key}={kv.Value.Replace("<root>", "%~dp0")}");
                }
            }
            batchLines.Add("cmd");
            File.WriteAllText(Path.Combine(sdkPackagePath, "StartShell.bat"), string.Join("\r\n", batchLines));

            // For all the directories underneath VS2022/VC/Tools/MSVC, link them to the root, which
            // makes this work with AutoSDK.
            foreach (var directory in new DirectoryInfo(Path.Combine(sdkPackagePath, "VS2022", "VC", "Tools", "MSVC")).GetDirectories())
            {
                Directory.CreateSymbolicLink(Path.Combine(sdkPackagePath, "VS2022", directory.Name), directory.FullName);
            }
        }

        private async Task ExtractVsixComponent(VisualStudioManifestChannelItem component, string sdkPackagePath, CancellationToken cancellationToken)
        {
            var vs2022 = Path.Combine(sdkPackagePath, "VS2022");
            Directory.CreateDirectory(vs2022);
            foreach (var payload in component.Payloads!)
            {
                var filename = Path.GetFileName(payload.FileName!);
                _logger.LogInformation($"Downloading and extracting VSIX: {filename} ({payload.Size / 1024 / 1024} MB)");
                using (var client = new HttpClient())
                {
                    await _simpleDownloadProgress.DownloadAndCopyToStreamAsync(
                        client,
                        payload.Url!,
                        stream =>
                        {
                            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                            {
                                foreach (var entry in archive.Entries)
                                {
                                    if (entry.FullName.EndsWith('\\') || entry.FullName.EndsWith('/'))
                                    {
                                        continue;
                                    }

                                    var relativeName = HttpUtility.UrlDecode(entry.FullName.Replace("+", "%2B"));
                                    if (relativeName.StartsWith("Contents"))
                                    {
                                        relativeName = relativeName.Substring("Contents".Length + 1);
                                    }
                                    else
                                    {
                                        continue;
                                    }

                                    if (relativeName.Contains('\\') || relativeName.Contains('/'))
                                    {
                                        var directoryName = Path.GetDirectoryName(relativeName);
                                        Directory.CreateDirectory(Path.Combine(vs2022, directoryName!));
                                    }

                                    entry.ExtractToFile(Path.Combine(vs2022, relativeName), true);
                                }
                            }
                            return Task.CompletedTask;
                        },
                        cancellationToken);

                }
            }
        }

        private async Task ExtractExecutableComponent(VisualStudioManifestChannelItem component, string sdkPackagePath, CancellationToken cancellationToken)
        {
            // Inspect the MSI packages in the Windows SDK component.
            _logger.LogInformation("Downloading MSI files...");
            var msiPackagesToAllow = new HashSet<string>
            {
                "Installers\\Windows SDK for Windows Store Apps Tools-x86_en-us.msi",
                "Installers\\Windows SDK for Windows Store Apps Headers-x86_en-us.msi",
                "Installers\\Windows SDK Desktop Headers x86-x86_en-us.msi",
                "Installers\\Windows SDK for Windows Store Apps Libs-x86_en-us.msi",
                "Installers\\Windows SDK Desktop Libs x64-x86_en-us.msi",
                "Installers\\Universal CRT Headers Libraries and Sources-x86_en-us.msi"
            };
            string[] GetCabFilenamesFromMsi(Stream msiData)
            {
                var cabNames = new List<string>();
                while (msiData.Position < msiData.Length)
                {
                    if (msiData.ReadByte() == '.')
                    {
                        if (msiData.ReadByte() == 'c')
                        {
                            if (msiData.ReadByte() == 'a')
                            {
                                if (msiData.ReadByte() == 'b')
                                {
                                    var cabNameLength = 32 + 4;
                                    msiData.Seek(-cabNameLength, SeekOrigin.Current);
                                    var cabNameBytes = new byte[cabNameLength];
                                    msiData.ReadExactly(cabNameBytes);
                                    cabNames.Add(Encoding.ASCII.GetString(cabNameBytes));
                                }
                            }
                        }
                    }
                }
                return cabNames.ToArray();
            }
            var allCabNames = new List<string>();
            var msiNames = new List<string>();
            foreach (var payload in component.Payloads!)
            {
                if (msiPackagesToAllow.Contains(payload.FileName!))
                {
                    var filename = Path.GetFileName(payload.FileName!);
                    msiNames.Add(filename);
                    _logger.LogInformation($"Downloading and parsing MSI: {filename} ({payload.Size / 1024 / 1024} MB)");
                    using (var client = new HttpClient())
                    {
                        var targetPath = Path.Combine(sdkPackagePath, "__Installers", filename);
                        Directory.CreateDirectory(Path.Combine(sdkPackagePath, "__Installers"));
                        using (var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                        {
                            await _simpleDownloadProgress.DownloadAndCopyToStreamAsync(
                                client,
                                payload.Url!,
                                async stream => await stream.CopyToAsync(file, cancellationToken),
                                cancellationToken);
                        }
                        using (var file = new FileStream(targetPath, FileMode.Open, FileAccess.Read))
                        {
                            allCabNames.AddRange(GetCabFilenamesFromMsi(file));
                        }
                    }
                }
            }
            if (allCabNames.Count == 0)
            {
                return;
            }
            _logger.LogInformation($"{allCabNames.Count} CAB files to download and extract...");
            var cabFileToUrl = component.Payloads
                .Where(x => x.FileName!.EndsWith(".cab"))
                .ToDictionary(k => k.FileName!, v => (url: v.Url!, size: v.Size));
            foreach (var cabName in allCabNames)
            {
                var uri = cabFileToUrl[$"Installers\\{cabName}"];
                using (var client = new HttpClient())
                {
                    var targetPath = Path.Combine(sdkPackagePath, "__Installers", cabName);
                    Directory.CreateDirectory(Path.Combine(sdkPackagePath, "__Installers"));
                    using (var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                    {
                        _logger.LogInformation($"Downloading CAB: {cabName} ({uri.size / 1024 / 1024} MB)");
                        await _simpleDownloadProgress.DownloadAndCopyToStreamAsync(
                            client,
                            uri.url,
                            async stream => await stream.CopyToAsync(file, cancellationToken),
                            cancellationToken);
                    }
                }
            }
            _logger.LogInformation($"Executing MSI files to extract Windows SDK...");
            var windowsKitsPath = Path.Combine(sdkPackagePath);
            foreach (var msiFile in msiNames)
            {
                _logger.LogInformation($"Extracting MSI: {msiFile}");
                await _processExecutor.ExecuteAsync(
                    new ProcessSpecification
                    {
                        FilePath = @"C:\WINDOWS\system32\msiexec.exe",
                        Arguments = new[]
                        {
                            "/a",
                            msiFile,
                            "/quiet",
                            "/qn",
                            $"TARGETDIR={windowsKitsPath}",
                        },
                        WorkingDirectory = Path.Combine(sdkPackagePath, "__Installers")
                    }, CaptureSpecification.Passthrough, cancellationToken);
                if (!File.Exists(Path.Combine(windowsKitsPath, msiFile)))
                {
                    throw new SdkSetupPackageGenerationFailedException($"MSI extraction failed for: {msiFile}");
                }
                File.Delete(Path.Combine(windowsKitsPath, msiFile));
            }
            // Clean up the installers folder that we no longer need.
            await DirectoryAsync.DeleteAsync(Path.Combine(sdkPackagePath, "__Installers"), true);
        }

        private async Task ExtractMsiComponent(VisualStudioManifestChannelItem component, string sdkPackagePath, CancellationToken cancellationToken)
        {
            // Inspect the MSI packages in the Windows SDK component.
            _logger.LogInformation("Downloading MSI files...");
            var msiNames = new List<string>();
            foreach (var payload in component.Payloads!)
            {
                var filename = Path.GetFileName(payload.FileName!);
                msiNames.Add(filename);
                _logger.LogInformation($"Downloading MSI: {filename} ({payload.Size / 1024 / 1024} MB)");
                using (var client = new HttpClient())
                {
                    var targetPath = Path.Combine(sdkPackagePath, "__Installers", filename);
                    Directory.CreateDirectory(Path.Combine(sdkPackagePath, "__Installers"));
                    using (var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                    {
                        await _simpleDownloadProgress.DownloadAndCopyToStreamAsync(
                            client,
                            payload.Url!,
                            async stream => await stream.CopyToAsync(file, cancellationToken),
                            cancellationToken);
                    }
                }
            }
            _logger.LogInformation($"Executing MSI files to extract them...");
            var setupPath = Path.Combine(sdkPackagePath, "Setup");
            Directory.CreateDirectory(setupPath);
            foreach (var msiFile in msiNames)
            {
                _logger.LogInformation($"Extracting MSI: {msiFile}");
                await _processExecutor.ExecuteAsync(
                    new ProcessSpecification
                    {
                        FilePath = @"C:\WINDOWS\system32\msiexec.exe",
                        Arguments = new[]
                        {
                            "/a",
                            msiFile,
                            "/quiet",
                            "/qn",
                            $"TARGETDIR={setupPath}",
                        },
                        WorkingDirectory = Path.Combine(sdkPackagePath, "__Installers")
                    }, CaptureSpecification.Passthrough, cancellationToken);
                if (!File.Exists(Path.Combine(setupPath, msiFile)))
                {
                    throw new SdkSetupPackageGenerationFailedException($"MSI extraction failed for: {msiFile}");
                }
                File.Delete(Path.Combine(setupPath, msiFile));
            }
            // Clean up the installers folder that we no longer need.
            await DirectoryAsync.DeleteAsync(Path.Combine(sdkPackagePath, "__Installers"), true);
        }

        public Task<AutoSdkMapping[]> GetAutoSdkMappingsForSdkPackage(string sdkPackagePath, CancellationToken cancellationToken)
        {
            return Task.FromResult(new[]
            {
                new AutoSdkMapping
                {
                    RelativePathInsideAutoSdkPath = "Win64",
                    RelativePathInsideSdkPackagePath = ".",
                }
            });
        }

        /*
        public Task<EnvironmentForSdkUsage> EnsureSdkPackage(string sdkPackagePath, CancellationToken cancellationToken)
        {
            var rawEnvs = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(sdkPackagePath, "envs.json")), VisualStudioJsonSerializerContext.Default.DictionaryStringString)!;
            var newEnvs = new Dictionary<string, string>();
            foreach (var kv in rawEnvs)
            {
                if (kv.Key == "UES_VS_INSTANCE_ID")
                {
                    continue;
                }

                newEnvs.Add(kv.Key, kv.Value.Replace("<root>", sdkPackagePath));
            }

            // Create a directory in a temporary folder with a symbolic link to the SDK package path, and set
            // that as the AutoSDK root.
            var temporaryDirectory = Path.Combine(Path.GetTempPath(), Path.GetFileName(sdkPackagePath), "HostWin64");
            Directory.CreateDirectory(temporaryDirectory);
            var symbolicLink = Path.Combine(temporaryDirectory, "Win64");
            if (File.Exists(symbolicLink))
            {
                File.Delete(symbolicLink);
            }
            if (Directory.Exists(symbolicLink))
            {
                Directory.Delete(symbolicLink);
            }
            Directory.CreateSymbolicLink(symbolicLink, sdkPackagePath);
            newEnvs.Add("UE_SDKS_ROOT", Path.Combine(Path.GetTempPath(), Path.GetFileName(sdkPackagePath)));

            return Task.FromResult(new EnvironmentForSdkUsage
            {
                EnvironmentVariables = newEnvs,
            });
        }
        */
    }
}

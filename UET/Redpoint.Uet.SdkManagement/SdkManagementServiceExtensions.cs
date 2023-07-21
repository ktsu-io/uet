namespace Redpoint.Uet.SdkManagement
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Redpoint.ProcessExecution;
    using Redpoint.Uet.Core;
    using System.Text.Json;
    using System;
    using System.Text.Json.Serialization;

    public static class SdkManagementServiceExtensions
    {
        public static void AddSdkManagement(this IServiceCollection services)
        {
            services.AddSingleton<ISimpleDownloadProgress, SimpleDownloadProgress>();
            services.AddSingleton<ILocalSdkManager, DefaultLocalSdkManager>();

            if (OperatingSystem.IsWindows())
            {
                services.AddSingleton<ISdkSetup, WindowsAutoSdkSetup>();
                services.AddSingleton<ISdkSetup, AndroidManualSdkSetup>();
                services.AddSingleton<ISdkSetup, LinuxManualSdkSetup>();

                // Register confidential implementations.
                foreach (var environmentVariableName in Environment.GetEnvironmentVariables()
                    .Keys
                    .OfType<string>()
                    .Where(x => x.StartsWith("UET_PLATFORM_SDK_CONFIG_PATH_")))
                {
                    var platform = environmentVariableName.Substring("UET_PLATFORM_SDK_CONFIG_PATH_".Length);
                    var configPath = Environment.GetEnvironmentVariable(platform)!;
                    var config = JsonSerializer.Deserialize(
                        File.ReadAllText(configPath),
                        new ConfidentialPlatformJsonSerializerContext(new JsonSerializerOptions
                        {
                            Converters =
                            {
                                new JsonStringEnumConverter(),
                            }
                        }).ConfidentialPlatformConfig)!;
                    switch (config.SdkType)
                    {
                        case ConfidentialPlatformConfigSdkType.ManualSdk:
                            services.AddSingleton<ISdkSetup>(sp => 
                            {
                                if (OperatingSystem.IsWindows())
                                {
                                    return new ConfidentialManualSdkSetup(
                                        platform,
                                        config!,
                                        sp.GetRequiredService<IProcessExecutor>(),
                                        sp.GetRequiredService<ILogger<ConfidentialManualSdkSetup>>(),
                                        sp.GetRequiredService<IStringUtilities>());
                                }
                                throw new PlatformNotSupportedException();
                            });
                            break;
                        case ConfidentialPlatformConfigSdkType.AutoSdk:
                            services.AddSingleton<ISdkSetup>(sp =>
                            {
                                if (OperatingSystem.IsWindows())
                                {
                                    return new ConfidentialAutoSdkSetup(
                                        platform,
                                        config!,
                                        sp.GetRequiredService<IProcessExecutor>(),
                                        sp.GetRequiredService<ILogger<ConfidentialAutoSdkSetup>>(),
                                        sp.GetRequiredService<IStringUtilities>());
                                }
                                throw new PlatformNotSupportedException();
                            });
                            break;
                        default:
                            throw new NotSupportedException($"The confidential platform located at '{configPath}' referenced by the environment variable '{environmentVariableName}' has an invalid SdkType.");
                    }
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                services.AddSingleton<ISdkSetup, MacManualSdkSetup>();
            }
        }
    }
}

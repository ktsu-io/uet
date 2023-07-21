namespace Redpoint.Uet.SdkManagement
{
    using System.Text.Json.Serialization;

    public class ConfidentialPlatformConfig
    {
        [JsonPropertyName("Version"), JsonRequired]
        public string? Version { get; set; }

        [JsonPropertyName("SdkType")]
        public ConfidentialPlatformConfigSdkType SdkType { get; set; }

        [JsonPropertyName("CommonPlatformName")]
        public string? CommonPlatformName { get; set; }

        [JsonPropertyName("Installers"), JsonRequired]
        public ConfidentialPlatformConfigInstaller[]? Installers { get; set; }

        [JsonPropertyName("EnvironmentVariables")]
        public Dictionary<string, string>? EnvironmentVariables { get; set; }

        [JsonPropertyName("AutoSdkRelativePathMappings")]
        public Dictionary<string, string>? AutoSdkRelativePathMappings { get; set; }
    }
}

﻿namespace Redpoint.Uet.SdkManagement.AutoSdk.WindowsSdk
{
    using System.Text.Json.Serialization;

    class VisualStudioManifest
    {
        [JsonPropertyName("channelItems")]
        public VisualStudioManifestChannelItem[]? ChannelItems { get; set; }

        [JsonPropertyName("packages")]
        public VisualStudioManifestChannelItem[]? Packages { get; set; }
    }
}

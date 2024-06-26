﻿namespace UET.Commands.Internal.CreateGitHubRelease
{
    using System.Text.Json.Serialization;

    internal sealed class ReleaseResponse
    {
        [JsonPropertyName("id")]
        public long? Id { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }
    }
}

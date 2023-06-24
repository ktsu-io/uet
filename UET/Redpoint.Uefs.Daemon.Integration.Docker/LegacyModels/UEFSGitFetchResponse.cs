﻿using System.Text.Json.Serialization;

namespace Redpoint.Uefs.Daemon.Integration.Docker.LegacyModels
{
    public class UEFSGitFetchResponse
    {
        [JsonPropertyName("PollingId")]
        public string? PollingId = null;

        [JsonPropertyName("Err")]
        public string? Err = null;
    }
}

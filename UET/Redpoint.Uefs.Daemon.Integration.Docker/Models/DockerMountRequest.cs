﻿namespace Redpoint.Uefs.Daemon.Integration.Docker.Models
{
    using System.Text.Json.Serialization;

    public class DockerMountRequest
    {
        [JsonPropertyName("Name")]
        public string Name = string.Empty;

        [JsonPropertyName("ID")]
        public string ID = string.Empty;
    }
}

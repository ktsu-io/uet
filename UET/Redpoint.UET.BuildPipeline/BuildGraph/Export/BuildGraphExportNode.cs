﻿namespace Redpoint.UET.BuildPipeline.BuildGraph.Export
{
    using System.Text.Json.Serialization;

    internal class BuildGraphExportNode
    {
        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("DependsOn")]
        public string DependsOn { get; set; } = string.Empty;

        [JsonPropertyName("RunEarly")]
        public bool RunEarly { get; set; } = false;

        [JsonPropertyName("Notify")]
        public BuildGraphExportNotify Notify { get; set; } = new BuildGraphExportNotify();
    }
}
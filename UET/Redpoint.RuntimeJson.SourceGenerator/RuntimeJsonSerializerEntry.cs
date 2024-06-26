﻿namespace Redpoint.RuntimeJson.SourceGenerator
{
    internal sealed class RuntimeJsonSerializerEntry
    {
        public string? Namespace { get; set; }
        public string? Class { get; set; }
        public string? JsonSerializerContextType { get; set; }
        public HashSet<string> SerializableClassNames { get; } = new HashSet<string>();
    }
}
﻿namespace Redpoint.OpenGE.JobXml
{
    public record class JobTool
    {
        public required string Name { get; init; }

        // public required bool AllowRemote { get; init; }

        // public required string GroupPrefix { get; init; }

        // public required string OutputPrefix { get; init; }

        public required string Params { get; init; }

        public required string Path { get; init; }

        // public required bool SkipIfProjectFailed { get; init; }

        // public required string AutoReserveMemory { get; init; }

        // public required string OutputFileMasks { get; init; }

        public required IReadOnlyCollection<string>? AutoRecover { get; init; }
    }
}

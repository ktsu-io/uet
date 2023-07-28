﻿namespace Redpoint.OpenGE.Executor.CompilerDb.PreprocessorScanner
{
    internal record class PreprocessorScanResult
    {
        public required long FileLastWriteTicks { get; set; }
        public required string[] Includes { get; set; }
        public required string[] SystemIncludes { get; set; }
        public required string[] CompiledPlatformHeaderIncludes { get; set; }
    }
}

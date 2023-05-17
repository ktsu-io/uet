﻿namespace Redpoint.UET.BuildPipeline.Environment
{
    using System.Collections.Generic;

    /// <summary>
    /// WindowsEnvironment contains the Windows-specific environment
    /// settings for jobs that run on Windows.
    /// </summary>
    public record BuildGraphWindowsEnvironment
    {
        /// <summary>
        /// All of the -set: parameters to pass to BuildGraph on Windows.
        /// </summary>
        public required Dictionary<string, string> BuildGraphSettings { get; init; }

        /// <summary>
        /// The absolute path to shared storage on Windows. Must start with a drive letter (like X:\). Must have a trailing slash.
        /// </summary>
        public required string? SharedStorageAbsolutePath { get; init; }
    }
}
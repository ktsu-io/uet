﻿namespace Redpoint.UET.Automation.SystemResources
{
    using System.Threading.Tasks;

    internal interface ISystemResources
    {
        bool CanQuerySystemResources { get; }

        ValueTask<(ulong availableMemoryBytes, ulong totalMemoryBytes)> GetMemoryInfo();
    }
}

﻿namespace Redpoint.OpenGE.Component.Dispatcher.WorkerPool
{
    using System;
    using System.Threading.Tasks;

    internal interface IWorkerCoreRequest<TWorkerCore> : IAsyncDisposable where TWorkerCore : IAsyncDisposable
    {
        bool RequireLocal { get; }

        Task FulfillRequestAsync(TWorkerCore core);

        Task<TWorkerCore> WaitForCoreAsync(CancellationToken cancellationToken);
    }
}

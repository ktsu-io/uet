﻿namespace Redpoint.UET.Automation.Worker.Local
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Redpoint.UET.Automation.TestLogging;
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    internal class LocalWorkerPoolFactory : IWorkerPoolFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public LocalWorkerPoolFactory(
            IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public Task<IWorkerPool> CreateAndStartAsync(
            ITestLogger testLogger,
            IEnumerable<DesiredWorkerDescriptor> workerDescriptors,
            OnWorkerStarted onWorkerStarted,
            OnWorkerExited onWorkedExited,
            OnWorkerPoolFailure onWorkerPoolFailure,
            CancellationToken cancellationToken)
        {
            var workerPool = new LocalWorkerPool(
                _serviceProvider,
                _serviceProvider.GetRequiredService<ILogger<LocalWorkerPool>>(),
                testLogger,
                workerDescriptors,
                onWorkerStarted,
                onWorkedExited,
                onWorkerPoolFailure,
                cancellationToken);
            return Task.FromResult<IWorkerPool>(workerPool);
        }
    }
}

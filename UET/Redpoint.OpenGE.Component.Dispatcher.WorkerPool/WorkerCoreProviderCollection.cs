﻿namespace Redpoint.OpenGE.Component.Dispatcher.WorkerPool
{
    using Redpoint.Concurrency;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    [SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "This class represents a collection of objects.")]
    public class WorkerCoreProviderCollection<TWorkerCore> : IWorkerPoolTracerAssignable where TWorkerCore : IAsyncDisposable
    {
        private readonly Dictionary<string, IWorkerCoreProvider<TWorkerCore>> _providers;
        private readonly Mutex _providerLock;
        private readonly AsyncEvent<WorkerCoreProviderCollectionChanged<TWorkerCore>> _onProvidersChanged;
        private WorkerPoolTracer? _tracer;

        public WorkerCoreProviderCollection()
        {
            _providers = new Dictionary<string, IWorkerCoreProvider<TWorkerCore>>();
            _providerLock = new Mutex();
            _onProvidersChanged = new AsyncEvent<WorkerCoreProviderCollectionChanged<TWorkerCore>>();
        }

        public void SetTracer(WorkerPoolTracer tracer)
        {
            _tracer = tracer;
        }

        public IAsyncEvent<WorkerCoreProviderCollectionChanged<TWorkerCore>> OnProvidersChanged => _onProvidersChanged;

        public async Task<IReadOnlyList<IWorkerCoreProvider<TWorkerCore>>> GetProvidersAsync()
        {
            using var _ = await _providerLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _tracer?.AddTracingMessage($"Returning registered {_providers.Values.Count} providers.");
            return new List<IWorkerCoreProvider<TWorkerCore>>(_providers.Values);
        }

        public async Task<bool> HasAsync(string providerId)
        {
            using var _ = await _providerLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            return _providers.ContainsKey(providerId);
        }

        public async Task AddAsync(IWorkerCoreProvider<TWorkerCore> provider)
        {
            ArgumentNullException.ThrowIfNull(provider);

            using var _ = await _providerLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            if (_providers.ContainsKey(provider.Id))
            {
                return;
            }
            _tracer?.AddTracingMessage($"Adding new provider to provider collection.");
            _providers.Add(provider.Id, provider);
            try
            {
                _tracer?.AddTracingMessage($"Broadcasting that the list of providers has changed.");
                await _onProvidersChanged.BroadcastAsync(new WorkerCoreProviderCollectionChanged<TWorkerCore>
                {
                    CurrentProviders = new List<IWorkerCoreProvider<TWorkerCore>>(_providers.Values),
                    AddedProvider = provider,
                    RemovedProvider = null,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        public async Task RemoveAsync(IWorkerCoreProvider<TWorkerCore> provider)
        {
            ArgumentNullException.ThrowIfNull(provider);

            using var _ = await _providerLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            if (_providers.ContainsKey(provider.Id))
            {
                _tracer?.AddTracingMessage($"Removing provider from provider collection.");
                _providers.Remove(provider.Id);
                try
                {
                    _tracer?.AddTracingMessage($"Broadcasting that the list of providers has changed.");
                    await _onProvidersChanged.BroadcastAsync(new WorkerCoreProviderCollectionChanged<TWorkerCore>
                    {
                        CurrentProviders = new List<IWorkerCoreProvider<TWorkerCore>>(_providers.Values),
                        AddedProvider = null,
                        RemovedProvider = provider,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }
}

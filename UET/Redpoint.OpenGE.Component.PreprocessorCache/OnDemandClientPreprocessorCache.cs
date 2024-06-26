﻿namespace Redpoint.OpenGE.Component.PreprocessorCache
{
    using Grpc.Core;
    using Microsoft.Extensions.Logging;
    using Redpoint.OpenGE.Protocol;
    using Redpoint.GrpcPipes;
    using Redpoint.ProcessExecution;
    using System.Threading;
    using System.Threading.Tasks;

    internal class OnDemandClientPreprocessorCache : IPreprocessorCache, IDisposable
    {
        private readonly ILogger<OnDemandClientPreprocessorCache> _logger;
        private readonly IGrpcPipeFactory _grpcPipeFactory;
        private readonly IProcessExecutor _processExecutor;
        private readonly ProcessSpecification _daemonLaunchSpecification;
        private readonly Concurrency.Semaphore _clientCreatingSemaphore;
        private readonly CancellationTokenSource _daemonCancellationTokenSource;
        private PreprocessorCacheApi.PreprocessorCacheApiClient? _currentClient;
        private Task<int>? _daemonProcess;

        public OnDemandClientPreprocessorCache(
            ILogger<OnDemandClientPreprocessorCache> logger,
            IGrpcPipeFactory grpcPipeFactory,
            IProcessExecutor processExecutor,
            ProcessSpecification daemonLaunchSpecification)
        {
            _logger = logger;
            _grpcPipeFactory = grpcPipeFactory;
            _processExecutor = processExecutor;
            _daemonLaunchSpecification = daemonLaunchSpecification;
            _clientCreatingSemaphore = new Concurrency.Semaphore(1);
            _daemonCancellationTokenSource = new CancellationTokenSource();
            _currentClient = null;
            _daemonProcess = null;
        }

        private async Task<PreprocessorCacheApi.PreprocessorCacheApiClient> GetClientAsync(bool spawn = false)
        {
            await _clientCreatingSemaphore.WaitAsync(_daemonCancellationTokenSource.Token).ConfigureAwait(false);
            try
            {
                if (spawn && (_daemonProcess == null || _daemonProcess.IsCompleted))
                {
                    _daemonProcess = Task.Run(async () => await _processExecutor.ExecuteAsync(
                        _daemonLaunchSpecification,
                        CaptureSpecification.Passthrough,
                        _daemonCancellationTokenSource.Token).ConfigureAwait(false));
                }
                // @note: Do not re-use current client if we were just told to spawn daemon.
                else if (!spawn && _currentClient != null)
                {
                    return _currentClient;
                }

                if (spawn)
                {
                    // @note: Pace the rate at which we re-create the client if we're trying to spawn the daemon.
                    await Task.Delay(10).ConfigureAwait(false);
                }

                _currentClient = _grpcPipeFactory.CreateClient(
                    "OpenGEPreprocessorCache",
                    GrpcPipeNamespace.Computer,
                    channel => new PreprocessorCacheApi.PreprocessorCacheApiClient(channel));
                return _currentClient;
            }
            finally
            {
                _clientCreatingSemaphore.Release();
            }
        }

        public async Task EnsureAsync()
        {
            var client = await GetClientAsync().ConfigureAwait(false);
            do
            {
                try
                {
                    await client.PingAsync(new PingRequest());
                    return;
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
                {
                    client = await GetClientAsync(true).ConfigureAwait(false);
                    continue;
                }
            } while (true);
        }

        public async Task<PreprocessorScanResultWithCacheMetadata> GetUnresolvedDependenciesAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            var client = await GetClientAsync().ConfigureAwait(false);
            do
            {
                try
                {
                    return (await client.GetUnresolvedDependenciesAsync(
                        new GetUnresolvedDependenciesRequest
                        {
                            Path = filePath,
                        },
                        cancellationToken: cancellationToken)).Result;
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
                {
                    client = await GetClientAsync(true).ConfigureAwait(false);
                    continue;
                }
            } while (true);
        }

        public async Task<PreprocessorResolutionResultWithTimingMetadata> GetResolvedDependenciesAsync(
            string filePath,
            string[] forceIncludes,
            string[] includeDirectories,
            Dictionary<string, string> globalDefinitions,
            long buildStartTicks,
            CompilerArchitype architype,
            CancellationToken cancellationToken)
        {
            var client = await GetClientAsync().ConfigureAwait(false);
            do
            {
                try
                {
                    var request = new GetResolvedDependenciesRequest
                    {
                        Path = filePath,
                        BuildStartTicks = buildStartTicks,
                        Architype = architype,
                    };
                    request.IncludeDirectories.AddRange(includeDirectories);
                    request.GlobalDefinitions.Add(globalDefinitions);
                    request.ForceIncludePaths.AddRange(forceIncludes);

                    return (await client.GetResolvedDependenciesAsync(request, cancellationToken: cancellationToken)).Result;
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
                {
                    client = await GetClientAsync(true).ConfigureAwait(false);
                    continue;
                }
            } while (true);
        }

        public async void Dispose()
        {
            _daemonCancellationTokenSource.Cancel();
            if (_daemonProcess != null)
            {
                try
                {
                    await _daemonProcess.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            _daemonCancellationTokenSource.Dispose();
        }
    }
}

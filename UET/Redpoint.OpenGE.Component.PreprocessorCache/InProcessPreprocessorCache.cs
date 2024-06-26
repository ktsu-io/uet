﻿namespace Redpoint.OpenGE.Component.PreprocessorCache
{
    using Grpc.Core;
    using Redpoint.OpenGE.Core;
    using Redpoint.OpenGE.Component.PreprocessorCache.DependencyResolution;
    using Redpoint.OpenGE.Component.PreprocessorCache.DirectiveScanner;
    using Redpoint.OpenGE.Component.PreprocessorCache.Filesystem;
    using Redpoint.OpenGE.Protocol;
    using Redpoint.Reservation;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    internal class InProcessPreprocessorCache : AbstractInProcessPreprocessorCache
    {
        private readonly ICachingPreprocessorScannerFactory _cachingPreprocessorScannerFactory;
        private readonly IPreprocessorResolver _preprocessorResolver;
        private readonly IReservationManagerForOpenGE _openGEReservationManagerProvider;
        private readonly Concurrency.Semaphore _initSemaphore = new Concurrency.Semaphore(1);
        private ICachingPreprocessorScanner? _cachingScanner;
        private bool _inited = false;
        private bool _disposed = false;
        private IReservation? _reservation;

        public InProcessPreprocessorCache(
            ICachingPreprocessorScannerFactory cachingPreprocessorScannerFactory,
            IPreprocessorResolver preprocessorResolver,
            IReservationManagerForOpenGE openGEReservationManagerProvider)
        {
            _cachingPreprocessorScannerFactory = cachingPreprocessorScannerFactory;
            _preprocessorResolver = preprocessorResolver;
            _openGEReservationManagerProvider = openGEReservationManagerProvider;
        }

        public override DateTimeOffset LastGrpcRequestUtc { get; protected set; } = DateTimeOffset.UtcNow;

        public async override ValueTask DisposeAsync()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _initSemaphore.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_inited)
                {
                    _disposed = true;
                    return;
                }
                if (_cachingScanner != null)
                {
                    _cachingScanner.Dispose();
                    _cachingScanner = null;
                }
                if (_reservation != null)
                {
                    await _reservation.DisposeAsync().ConfigureAwait(false);
                    _reservation = null;
                }
                _disposed = true;
                _inited = false;
            }
            finally
            {
                _initSemaphore.Release();
            }
        }

        public async override Task EnsureAsync()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_inited)
            {
                return;
            }
            await _initSemaphore.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_inited)
                {
                    return;
                }

                _reservation = await _openGEReservationManagerProvider.ReservationManager.TryReserveExactAsync("Preprocessor").ConfigureAwait(false);
                if (_reservation == null)
                {
                    throw new PreprocessorCacheAlreadyRunningException();
                }

                _cachingScanner = _cachingPreprocessorScannerFactory.CreateCachingPreprocessorScanner(_reservation.ReservedPath);

                _inited = true;
            }
            finally
            {
                _initSemaphore.Release();
            }
        }

        public async override Task<PreprocessorResolutionResultWithTimingMetadata> GetResolvedDependenciesAsync(
            string filePath,
            string[] forceIncludes,
            string[] includeDirectories,
            Dictionary<string, string> globalDefinitions,
            long buildStartTicks,
            CompilerArchitype architype,
            CancellationToken cancellationToken)
        {
            await EnsureAsync().ConfigureAwait(false);
            return await _preprocessorResolver.ResolveAsync(
                _cachingScanner!,
                filePath,
                forceIncludes,
                includeDirectories,
                globalDefinitions,
                buildStartTicks,
                architype,
                cancellationToken).ConfigureAwait(false);
        }

        public async override Task<PreprocessorScanResultWithCacheMetadata> GetUnresolvedDependenciesAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            await EnsureAsync().ConfigureAwait(false);
            return _cachingScanner!.ParseIncludes(filePath);
        }

        public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
        {
            return Task.FromResult(new PingResponse());
        }

        public override Task<GetUnresolvedDependenciesResponse> GetUnresolvedDependencies(
            GetUnresolvedDependenciesRequest request,
            ServerCallContext context)
        {
            LastGrpcRequestUtc = DateTimeOffset.UtcNow;
            var result = _cachingScanner!.ParseIncludes(request.Path);
            var response = new GetUnresolvedDependenciesResponse
            {
                Result = result,
            };
            LastGrpcRequestUtc = DateTimeOffset.UtcNow;
            return Task.FromResult(response);
        }

        public override async Task<GetResolvedDependenciesResponse> GetResolvedDependencies(
            GetResolvedDependenciesRequest request,
            ServerCallContext context)
        {
            try
            {
                LastGrpcRequestUtc = DateTimeOffset.UtcNow;
                var result = await _preprocessorResolver.ResolveAsync(
                    _cachingScanner!,
                    request.Path,
                    request.ForceIncludePaths.ToArray(),
                    request.IncludeDirectories.ToArray(),
                    request.GlobalDefinitions.ToDictionary(k => k.Key, v => v.Value),
                    request.BuildStartTicks,
                    request.Architype,
                    context.CancellationToken).ConfigureAwait(false);
                LastGrpcRequestUtc = DateTimeOffset.UtcNow;
                return new GetResolvedDependenciesResponse
                {
                    Result = result
                };
            }
            catch (OperationCanceledException)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, "Call was cancelled by client."));
            }
            catch (PreprocessorIncludeNotFoundException ex)
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    $"The preprocessor cache could not resolve the include '{ex.SearchValue}'."));
            }
            catch (PreprocessorIdentifierNotDefinedException ex)
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    $"A preprocessor identifier was not defined when evaluating the preprocessor directives: {ex.Message}"));
            }
            catch (PreprocessorResolutionException ex)
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    $"A generic preprocessor resolution exception occurred: {ex}"));
            }
        }
    }
}

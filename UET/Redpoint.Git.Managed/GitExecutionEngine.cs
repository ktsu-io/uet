namespace Redpoint.Git.Managed
{
    using Microsoft.Extensions.Logging;
    using System.Collections.Concurrent;

    internal class GitExecutionEngine : IDisposable
    {
        private readonly SemaphoreSlim _operationReadySemaphore;
        private readonly ConcurrentQueue<GitOperation> _pendingOperations;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly ILogger<GitExecutionEngine> _logger;
        private readonly Task[] _operationTasks;

        public GitExecutionEngine(
            ILogger<GitExecutionEngine> logger)
        {
            _operationReadySemaphore = new SemaphoreSlim(0);
            _pendingOperations = new ConcurrentQueue<GitOperation>();
            _cancellationTokenSource = new CancellationTokenSource();
            _logger = logger;
            _operationTasks = Enumerable.Range(0, Environment.ProcessorCount).Select(x => Task.Run(RunAsync)).ToArray();
        }

        private Task RunOperationAsync(GitOperation operation, CancellationToken cancellationToken)
        {
            switch (operation)
            {
                case CheckoutCommitGitOperation checkout:

                    break;
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
        }

        private void EnqueueOperation(GitOperation operation)
        {
            _pendingOperations.Enqueue(operation);
            _operationReadySemaphore.Release();
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    await _operationReadySemaphore.WaitAsync(_cancellationTokenSource.Token);
                    if (!_pendingOperations.TryDequeue(out var nextOperation))
                    {
                        throw new InvalidOperationException();
                    }

                    try
                    {
                        await RunOperationAsync(nextOperation, _cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to run Git operaiton: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                // Expected.
            }
        }
    }
}
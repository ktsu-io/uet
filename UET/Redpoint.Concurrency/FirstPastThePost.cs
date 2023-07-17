namespace Redpoint.Concurrency
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Invokes a callback with the result when the first operation returns a
    /// result. Unlike <see cref="Task.WhenAny{TResult}(Task{TResult}[])"/>, 
    /// </summary>
    /// <typeparam name="TResult"></typeparam>
    public class FirstPastThePost<TResult> where TResult : class
    {
        private readonly CancellationTokenSource _cancellationTokenSource;
        private long _scheduledOperations;
        private bool _hasResult;
        private readonly SemaphoreSlim _resultSemaphore;
        private readonly Func<TResult?, Task> _onResult;

        public FirstPastThePost(
            CancellationTokenSource cancellationTokenSource,
            long scheduledOperations,
            Func<TResult?, Task> onResult)
        {
            _cancellationTokenSource = cancellationTokenSource;
            _scheduledOperations = scheduledOperations;
            _hasResult = false;
            _resultSemaphore = new SemaphoreSlim(1);
            _onResult = onResult;
        }

        public bool HasReceivedResult => _hasResult;

        public async Task ReceiveResultAsync(TResult result)
        {
            var broadcastResult = false;
            await _resultSemaphore.WaitAsync();
            try
            {
                _scheduledOperations--;
                if (_scheduledOperations < 0)
                {
                    throw new InvalidOperationException("Got more ReceiveResultAsync/ReceiveNoResultAsync than expected.");
                }

                if (_hasResult)
                {
                    return;
                }

                _hasResult = true;
                _cancellationTokenSource.Cancel();
                broadcastResult = true;
            }
            finally
            {
                _resultSemaphore.Release();
            }

            if (broadcastResult)
            {
                await _onResult(result);
            }
        }

        public async Task ReceiveNoResultAsync()
        {
            var broadcastResult = false;
            await _resultSemaphore.WaitAsync();
            try
            {
                _scheduledOperations--;
                if (_scheduledOperations < 0)
                {
                    throw new InvalidOperationException("Got more ReceiveResultAsync/ReceiveNoResultAsync than expected.");
                }

                if (_scheduledOperations == 0)
                {
                    // We're broadcasting nothing, because no task returned
                    // a result.
                    _hasResult = true;
                    _cancellationTokenSource.Cancel();
                    broadcastResult = true;
                }
            }
            finally
            {
                _resultSemaphore.Release();
            }

            if (broadcastResult)
            {
                await _onResult(null);
            }
        }

    }

    public class AllPastThePost<TResult> where TResult : class
    {
    }
}

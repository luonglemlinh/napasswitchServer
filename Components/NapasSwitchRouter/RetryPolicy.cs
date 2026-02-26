using System;
using core.Helpers;
using System.Threading;
using System.Threading.Tasks;

namespace router
{

    /// Retry policy with exponential backoff for network operations

    public class RetryPolicy
    {
        private readonly int _maxRetries;
        private readonly int _baseDelayMs;
        private readonly int _maxDelayMs;
        private readonly double _backoffMultiplier;

        public RetryPolicy(int maxRetries = 3, int baseDelayMs = 100, int maxDelayMs = 5000, double backoffMultiplier = 2.0)
        {
            _maxRetries = maxRetries;
            _baseDelayMs = baseDelayMs;
            _maxDelayMs = maxDelayMs;
            _backoffMultiplier = backoffMultiplier;
        }


        /// Execute an async action with retry and exponential backoff

        public async Task<T> ExecuteAsync<T>(Func<Task<T>> action, Func<Exception, bool> shouldRetry, string operationName, CancellationToken cancellationToken = default)
        {
            int attempt = 0;
            Exception? lastException = null;

            while (attempt <= _maxRetries)
            {
                try
                {
                    return await action();
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    attempt++;

                    if (attempt > _maxRetries || !shouldRetry(ex))
                    {
                        SwitchLogger.Info($"[RETRY] {operationName} failed after {attempt} attempts: {ex.Message}");
                        throw;
                    }

                    int delay = CalculateDelay(attempt);
                    SwitchLogger.Info($"[RETRY] {operationName} attempt {attempt} failed: {ex.Message}. Retrying in {delay}ms...");
                    await Task.Delay(delay, cancellationToken);
                }
            }

            throw lastException ?? new InvalidOperationException("Retry failed");
        }

        private int CalculateDelay(int attempt)
        {
            // Exponential backoff with jitter
            double delay = _baseDelayMs * Math.Pow(_backoffMultiplier, attempt - 1);
            
            // Add random jitter (�25%) using thread-safe Random.Shared
            double jitter = delay * 0.25 * (Random.Shared.NextDouble() * 2 - 1);
            delay += jitter;

            return Math.Min((int)delay, _maxDelayMs);
        }

        
        /// Predicate for retryable network exceptions
        
        public static bool IsRetryableException(Exception ex)
        {
            return ex is System.Net.Sockets.SocketException ||
                   ex is TimeoutException ||
                   ex is System.IO.IOException;
        }
    }
}

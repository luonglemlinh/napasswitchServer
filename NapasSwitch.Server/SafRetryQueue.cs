using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using core.Helpers;
using core.Models;

namespace server
{
    /// <summary>
    /// Store and Forward (SAF) queue for advice messages that failed ISS delivery.
    /// Persists failed 0420 advice messages in memory and retries delivery at intervals.
    /// </summary>
    public sealed class SafRetryQueue : IDisposable
    {
        private readonly Channel<SafEntry> _queue;
        private readonly ConcurrentQueue<SafEntry> _retryBacklog = new();
        private readonly Func<SafEntry, Task<bool>> _retryFunc;
        private readonly Task _processorTask;
        private readonly Timer _retryTimer;
        private readonly CancellationTokenSource _cts = new();
        private readonly int _maxRetries;
        private readonly TimeSpan _retryInterval;
        private int _totalEnqueued;
        private int _totalDelivered;
        private int _totalDropped;
        private bool _disposed;

        public int PendingCount => _retryBacklog.Count;
        public int TotalEnqueued => _totalEnqueued;
        public int TotalDelivered => _totalDelivered;
        public int TotalDropped => _totalDropped;

        /// <param name="retryFunc">Async function that attempts delivery. Returns true on success.</param>
        /// <param name="maxRetries">Maximum retry attempts before dropping the message.</param>
        /// <param name="retryInterval">Interval between retry sweeps.</param>
        public SafRetryQueue(Func<SafEntry, Task<bool>> retryFunc, int maxRetries = 5, TimeSpan? retryInterval = null)
        {
            _retryFunc = retryFunc;
            _maxRetries = maxRetries;
            _retryInterval = retryInterval ?? TimeSpan.FromSeconds(30);

            _queue = Channel.CreateBounded<SafEntry>(new BoundedChannelOptions(500)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            _processorTask = Task.Run(ProcessNewEntriesAsync);
            _retryTimer = new Timer(RetryBacklog, null, _retryInterval, _retryInterval);
        }

        /// <summary>
        /// Enqueue a failed advice for later retry.
        /// </summary>
        public void Enqueue(IsoMessage request, string sessionId, bool isVoid)
        {
            var entry = new SafEntry
            {
                Request = request,
                SessionId = sessionId,
                IsVoid = isVoid,
                EnqueuedAt = DateTime.UtcNow,
                Attempts = 0
            };

            if (!_queue.Writer.TryWrite(entry))
            {
                Interlocked.Increment(ref _totalDropped);
                SwitchLogger.ForContext("SAF").Warn(
                    "Queue full, dropping advice for session {SessionId}", sessionId);
            }
            else
            {
                Interlocked.Increment(ref _totalEnqueued);
                SwitchLogger.ForContext("SAF").Info(
                    "Queued {Type} advice for retry. Session={SessionId}",
                    isVoid ? "VOID" : "REVERSAL", sessionId);
            }
        }

        private async Task ProcessNewEntriesAsync()
        {
            try
            {
                await foreach (var entry in _queue.Reader.ReadAllAsync(_cts.Token))
                {
                    _retryBacklog.Enqueue(entry);
                }
            }
            catch (OperationCanceledException) { }
        }

        private async void RetryBacklog(object? state)
        {
            int count = _retryBacklog.Count;
            if (count == 0) return;

            SwitchLogger.ForContext("SAF").Debug("Retry sweep: {Count} pending advice(s)", count);

            for (int i = 0; i < count; i++)
            {
                if (!_retryBacklog.TryDequeue(out var entry)) break;

                entry.Attempts++;
                try
                {
                    bool success = await _retryFunc(entry);
                    if (success)
                    {
                        Interlocked.Increment(ref _totalDelivered);
                        SwitchLogger.ForContext("SAF").Info(
                            "Delivered advice on attempt {Attempt}. Session={SessionId}",
                            entry.Attempts, entry.SessionId);
                    }
                    else
                    {
                        RequeueOrDrop(entry);
                    }
                }
                catch (Exception ex)
                {
                    SwitchLogger.ForContext("SAF").Error(
                        "Retry failed for session {SessionId}: {Error}", entry.SessionId, ex.Message);
                    RequeueOrDrop(entry);
                }
            }
        }

        private void RequeueOrDrop(SafEntry entry)
        {
            if (entry.Attempts >= _maxRetries)
            {
                Interlocked.Increment(ref _totalDropped);
                SwitchLogger.ForContext("SAF").Warn(
                    "Max retries ({MaxRetries}) exceeded for session {SessionId}. Advice dropped.",
                    _maxRetries, entry.SessionId);
                MessageLogger.LogMessage(entry.SessionId, "SAF-DROPPED", entry.Request);
            }
            else
            {
                _retryBacklog.Enqueue(entry);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.Writer.Complete();
            _cts.Cancel();
            _retryTimer.Dispose();
            try { _processorTask.Wait(TimeSpan.FromSeconds(3)); } catch { }
            _cts.Dispose();

            int remaining = _retryBacklog.Count;
            if (remaining > 0)
                SwitchLogger.ForContext("SAF").Warn("{Count} undelivered advice(s) lost on shutdown", remaining);
        }
    }

    public class SafEntry
    {
        public IsoMessage Request { get; set; } = null!;
        public string SessionId { get; set; } = string.Empty;
        public bool IsVoid { get; set; }
        public DateTime EnqueuedAt { get; set; }
        public int Attempts { get; set; }
    }
}

using System.Collections.Concurrent;
using System.Text.Json;

namespace core.Helpers
{
    /// <summary>
    /// Thread-safe metrics counters for observability.
    /// Tracks txn/sec, pool hits/misses, error rates, and uptime.
    /// </summary>
    public static class ServerMetrics
    {
        // ---- Transaction counters ----
        private static long _totalTransactions;
        private static long _successfulTransactions;
        private static long _failedTransactions;
        private static long _timeoutTransactions;

        // ---- Message type counters ----
        private static long _authorizationRequests;   // 0200
        private static long _reversalRequests;         // 0400
        private static long _reversalAdvices;          // 0420
        private static long _echoRequests;             // 0800

        // ---- Connection pool counters ----
        private static long _poolHits;
        private static long _poolMisses;

        // ---- Error counters ----
        private static long _parseErrors;
        private static long _routingErrors;
        private static long _connectionErrors;

        // ---- Throughput tracking ----
        private static long _transactionsInWindow;
        private static DateTime _windowStart = DateTime.UtcNow;
        private static readonly object _windowLock = new();

        // ---- Response code distribution ----
        private static readonly ConcurrentDictionary<string, long> _responseCodeCounts = new();

        // Transaction counters
        public static void IncrementTotal() => Interlocked.Increment(ref _totalTransactions);
        public static void IncrementSuccess() => Interlocked.Increment(ref _successfulTransactions);
        public static void IncrementFailed() => Interlocked.Increment(ref _failedTransactions);
        public static void IncrementTimeout() => Interlocked.Increment(ref _timeoutTransactions);

        // MTI counters
        public static void IncrementAuthorization() => Interlocked.Increment(ref _authorizationRequests);
        public static void IncrementReversal() => Interlocked.Increment(ref _reversalRequests);
        public static void IncrementReversalAdvice() => Interlocked.Increment(ref _reversalAdvices);
        public static void IncrementEcho() => Interlocked.Increment(ref _echoRequests);

        // Pool counters
        public static void IncrementPoolHit() => Interlocked.Increment(ref _poolHits);
        public static void IncrementPoolMiss() => Interlocked.Increment(ref _poolMisses);

        // Error counters
        public static void IncrementParseError() => Interlocked.Increment(ref _parseErrors);
        public static void IncrementRoutingError() => Interlocked.Increment(ref _routingErrors);
        public static void IncrementConnectionError() => Interlocked.Increment(ref _connectionErrors);

        // Response code tracking
        public static void RecordResponseCode(string? rc)
        {
            string code = rc ?? "NULL";
            _responseCodeCounts.AddOrUpdate(code, 1, (_, v) => v + 1);
        }

        /// <summary>
        /// Record a completed transaction for throughput calculation.
        /// </summary>
        public static void RecordTransaction()
        {
            Interlocked.Increment(ref _totalTransactions);
            Interlocked.Increment(ref _transactionsInWindow);
        }

        /// <summary>
        /// Calculate transactions per second over the current window.
        /// </summary>
        public static double GetTransactionsPerSecond()
        {
            lock (_windowLock)
            {
                var elapsed = DateTime.UtcNow - _windowStart;
                if (elapsed.TotalSeconds < 1) return 0;

                double tps = Interlocked.Read(ref _transactionsInWindow) / elapsed.TotalSeconds;

                // Reset window every 60 seconds for a rolling average
                if (elapsed.TotalSeconds >= 60)
                {
                    Interlocked.Exchange(ref _transactionsInWindow, 0);
                    _windowStart = DateTime.UtcNow;
                }

                return Math.Round(tps, 2);
            }
        }

        /// <summary>
        /// Get a snapshot of all metrics as a dictionary for JSON serialization.
        /// </summary>
        public static MetricsSnapshot GetSnapshot()
        {
            return new MetricsSnapshot
            {
                TotalTransactions = Interlocked.Read(ref _totalTransactions),
                SuccessfulTransactions = Interlocked.Read(ref _successfulTransactions),
                FailedTransactions = Interlocked.Read(ref _failedTransactions),
                TimeoutTransactions = Interlocked.Read(ref _timeoutTransactions),
                AuthorizationRequests = Interlocked.Read(ref _authorizationRequests),
                ReversalRequests = Interlocked.Read(ref _reversalRequests),
                ReversalAdvices = Interlocked.Read(ref _reversalAdvices),
                EchoRequests = Interlocked.Read(ref _echoRequests),
                PoolHits = Interlocked.Read(ref _poolHits),
                PoolMisses = Interlocked.Read(ref _poolMisses),
                ParseErrors = Interlocked.Read(ref _parseErrors),
                RoutingErrors = Interlocked.Read(ref _routingErrors),
                ConnectionErrors = Interlocked.Read(ref _connectionErrors),
                TransactionsPerSecond = GetTransactionsPerSecond(),
                ResponseCodeDistribution = new Dictionary<string, long>(_responseCodeCounts)
            };
        }

        /// <summary>
        /// Serialize current metrics to JSON string.
        /// </summary>
        public static string ToJson()
        {
            return JsonSerializer.Serialize(GetSnapshot(), new JsonSerializerOptions { WriteIndented = true });
        }
    }

    public class MetricsSnapshot
    {
        public long TotalTransactions { get; set; }
        public long SuccessfulTransactions { get; set; }
        public long FailedTransactions { get; set; }
        public long TimeoutTransactions { get; set; }
        public long AuthorizationRequests { get; set; }
        public long ReversalRequests { get; set; }
        public long ReversalAdvices { get; set; }
        public long EchoRequests { get; set; }
        public long PoolHits { get; set; }
        public long PoolMisses { get; set; }
        public long ParseErrors { get; set; }
        public long RoutingErrors { get; set; }
        public long ConnectionErrors { get; set; }
        public double TransactionsPerSecond { get; set; }
        public Dictionary<string, long> ResponseCodeDistribution { get; set; } = new();
    }
}

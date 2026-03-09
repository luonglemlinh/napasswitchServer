using System;
using System.Collections.Generic;
using core.Helpers;
using System.Collections.Concurrent;
using System.Threading;
using core.Models;

namespace router
{

    /// Transaction states in the switch processing lifecycle
    /// Simplified for clarity: RECEIVED ? ROUTING ? COMPLETED/FAILED/REVERSING ? REVERSED

    public enum TransactionState
    {
        // Core states - simpler mental model for new developers
        Received,           // Initial message from ACQ
        Routing,            // Validated and being routed (combines Validated + AwaitingResponse)
        Completed,          // Finished successfully
        Failed,             // Processing failed (any error)

        // Reversal states  
        Reversing,          // Reversal in progress (combines ReversalRequired + ReversalPending)
        Reversed            // Reversal completed or failed
    }


    /// Transaction context containing all state information

    public class TransactionContext
    {
        public string TransactionId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public TransactionState State { get; private set; }
        public IsoMessage? Request { get; set; }
        public IsoMessage? Response { get; set; }
        public IsoMessage? ReversalRequest { get; set; }
        public IsoMessage? ReversalResponse { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? SentToIssuerAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public int RetryCount { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? TRN { get; set; }
        public string? IssuerCode { get; set; }
        public byte[]? RequestBytes { get; set; }
        public List<BufferedLogEntry> BufferedLogs { get; } = new();

        private readonly object _lock = new object();

        public TransactionContext()
        {
            State = TransactionState.Received;
            CreatedAt = DateTime.UtcNow;
        }


        /// Transition to a new state with validation

        public bool TryTransitionTo(TransactionState newState, string? errorCode = null, string? errorMessage = null)
        {
            lock (_lock)
            {
                // Once terminal, stay terminal (unless moving to reversal)
                if (IsTerminalState() && newState != TransactionState.Reversing && newState != TransactionState.Reversed)
                {
                    return false;
                }

                State = newState;
                ErrorCode = errorCode;
                ErrorMessage = errorMessage;

                if (newState == TransactionState.Routing) SentToIssuerAt = DateTime.UtcNow;
                if (IsTerminalState()) CompletedAt = DateTime.UtcNow;

                SwitchLogger.Info($"[STATE] {TransactionId}: {newState}");
                return true;
            }
        }

        public TimeSpan? GetProcessingTime()
        {
            if (CompletedAt.HasValue)
                return CompletedAt.Value - CreatedAt;
            return DateTime.UtcNow - CreatedAt;
        }

        public bool IsTerminalState()
        {
            return State == TransactionState.Completed ||
                   State == TransactionState.Failed ||
                   State == TransactionState.Reversed;
        }
    }


    /// Transaction state machine manager
    /// Tracks all active transactions and handles timeouts/reversals
    /// Uses PriorityQueue for O(log n) timeout checking instead of O(n) iteration

    public class TransactionStateMachine : IDisposable
    {
        private readonly ConcurrentDictionary<string, TransactionContext> _transactions;
        private readonly ConcurrentDictionary<string, string> _sessionStanIndex; // "session:stan" -> transactionId
        private readonly Timer _timeoutChecker;
        private readonly TimeSpan _transactionTimeout;
        private readonly TimeSpan _staleTransactionTimeout;
        private bool _disposed;

        // PriorityQueue for efficient timeout checking - orders by expiry time
        private readonly PriorityQueue<string, DateTime> _expiryQueue;
        private readonly object _queueLock = new();

        public event Action<TransactionContext>? OnTransactionTimeout;
        public event Action<TransactionContext>? OnReversalRequired;

        public TransactionStateMachine(int transactionTimeoutSeconds = 30, int staleTimeoutMinutes = 5)
        {
            _transactions = new ConcurrentDictionary<string, TransactionContext>();
            _sessionStanIndex = new ConcurrentDictionary<string, string>();
            _expiryQueue = new PriorityQueue<string, DateTime>();
            _transactionTimeout = TimeSpan.FromSeconds(transactionTimeoutSeconds);
            _staleTransactionTimeout = TimeSpan.FromMinutes(staleTimeoutMinutes);

            // Check for timeouts every second
            _timeoutChecker = new Timer(CheckTimeouts, null, 1000, 1000);
        }


        /// Create a new transaction context

        public TransactionContext CreateTransaction(string sessionId, IsoMessage request)
        {
            var now = DateTime.UtcNow;
            var context = new TransactionContext
            {
                TransactionId = GenerateTransactionId(),
                SessionId = sessionId,
                Request = request,
                ExpiresAt = now.Add(_transactionTimeout)
            };

            _transactions.TryAdd(context.TransactionId, context);

            string? stan = request.GetSTAN();
            if (!string.IsNullOrEmpty(stan))
                _sessionStanIndex[$"{sessionId}:{stan}"] = context.TransactionId;

            // Add to priority queue for timeout tracking
            lock (_queueLock)
            {
                _expiryQueue.Enqueue(context.TransactionId, context.ExpiresAt);
            }

            SwitchLogger.Info($"[STATE-MACHINE] Created transaction {context.TransactionId} for session {sessionId}");

            return context;
        }


        /// Get transaction context by ID

        public TransactionContext? GetTransaction(string transactionId)
        {
            _transactions.TryGetValue(transactionId, out var context);
            return context;
        }


        /// Find transaction by session and STAN

        public TransactionContext? FindTransaction(string sessionId, string? stan)
        {
            if (!string.IsNullOrEmpty(stan) && _sessionStanIndex.TryGetValue($"{sessionId}:{stan}", out string? txnId))
            {
                _transactions.TryGetValue(txnId, out var ctx);
                return ctx;
            }
            return null;
        }


        /// Remove completed transaction from tracking

        public void CompleteTransaction(string transactionId)
        {
            if (_transactions.TryRemove(transactionId, out var context))
            {
                SwitchLogger.Info($"[STATE-MACHINE] Transaction {transactionId} completed and removed from tracking");
            }
        }


        /// Check for timed out transactions using PriorityQueue for O(1) head check

        private void CheckTimeouts(object? state)
        {
            var now = DateTime.UtcNow;
            var toCleanup = new List<string>();

            lock (_queueLock)
            {
                // Process expired transactions from the head of the queue
                while (_expiryQueue.TryPeek(out var txnId, out var expiresAt))
                {
                    if (expiresAt > now)
                    {
                        // No more expired transactions
                        break;
                    }

                    _expiryQueue.Dequeue();

                    if (_transactions.TryGetValue(txnId, out var context))
                    {
                        if (context.State == TransactionState.Routing)
                        {
                            HandleTimeout(context);
                        }
                        else if (context.IsTerminalState() && context.CompletedAt.HasValue)
                        {
                            // Re-enqueue for stale cleanup check
                            var staleExpiry = context.CompletedAt.Value.Add(_staleTransactionTimeout);
                            if (now >= staleExpiry)
                            {
                                toCleanup.Add(txnId);
                            }
                            else
                            {
                                _expiryQueue.Enqueue(txnId, staleExpiry);
                            }
                        }
                    }
                }
            }

            // Clean up stale transactions outside the lock
            foreach (var txnId in toCleanup)
            {
                if (_transactions.TryRemove(txnId, out var context))
                {
                    string? staleStan = context.Request?.GetSTAN();
                    if (!string.IsNullOrEmpty(staleStan))
                        _sessionStanIndex.TryRemove($"{context.SessionId}:{staleStan}", out _);
                }
            }
        }

        private void HandleTimeout(TransactionContext context)
        {
            if (context.TryTransitionTo(TransactionState.Failed))
            {
                SwitchLogger.Info($"[STATE-MACHINE] Transaction {context.TransactionId} TIMED OUT");
                OnTransactionTimeout?.Invoke(context);

                // For authorization requests, we may need to send reversal
                if (context.Request?.MessageType == "0200")
                {
                    context.TryTransitionTo(TransactionState.Reversing);
                    OnReversalRequired?.Invoke(context);
                }
            }
        }

        
        /// Create automatic reversal for timed out transaction
        
        public IsoMessage CreateAutoReversal(TransactionContext context)
        {
            if (context.Request == null)
                throw new InvalidOperationException("Cannot create reversal without original request");

            var reversal = new IsoMessage
            {
                MessageType = "0400" // Reversal request
            };

            // Copy fields from original request
            foreach (var field in new[] { 2, 3, 4, 7, 9, 11, 12, 13, 32, 33, 37, 41, 42, 49, 50, 63 })
            {
                if (context.Request.HasField(field))
                    reversal.SetField(field, context.Request.GetField(field));
            }

            // DE#90: Original Data Elements (42 bytes)
            reversal.SetField(90, IsoMessage.BuildDE90(context.Request));

            // Calculate DE #5 (Settlement Amount) if DE #4 and DE #9 are present
            core.Helpers.SettlementHelper.AddSettlementAmount(reversal);

            // Set reversal reason: timeout
            reversal.SetField(56, "4021"); // Transaction timeout

            context.ReversalRequest = reversal;
            context.TryTransitionTo(TransactionState.Reversing);

            return reversal;
        }

        private string GenerateTransactionId()
        {
            return $"TXN-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        }

        public TransactionStateMachineStats GetStats()
        {
            int routing = 0, completed = 0, failed = 0, reversals = 0;

            foreach (var kvp in _transactions)
            {
                switch (kvp.Value.State)
                {
                    case TransactionState.Routing:
                        routing++;
                        break;
                    case TransactionState.Completed:
                        completed++;
                        break;
                    case TransactionState.Failed:
                        failed++;
                        break;
                    case TransactionState.Reversing:
                        reversals++;
                        break;
                }
            }

            return new TransactionStateMachineStats
            {
                ActiveTransactions = _transactions.Count,
                RoutingCount = routing,
                CompletedCount = completed,
                FailedCount = failed,
                ReversingCount = reversals
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timeoutChecker?.Dispose();
            _transactions.Clear();
        }
    }

    public class TransactionStateMachineStats
    {
        public int ActiveTransactions { get; set; }
        public int RoutingCount { get; set; }
        public int CompletedCount { get; set; }
        public int FailedCount { get; set; }
        public int ReversingCount { get; set; }
    }
}

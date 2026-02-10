using System;
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
        public int RetryCount { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? TRN { get; set; }

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

                Console.WriteLine($"[STATE] {TransactionId}: {newState}");
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
    
    public class TransactionStateMachine : IDisposable
    {
        private readonly ConcurrentDictionary<string, TransactionContext> _transactions;
        private readonly Timer _timeoutChecker;
        private readonly TimeSpan _transactionTimeout;
        private readonly TimeSpan _staleTransactionTimeout;
        private bool _disposed;

        public event Action<TransactionContext>? OnTransactionTimeout;
        public event Action<TransactionContext>? OnReversalRequired;

        public TransactionStateMachine(int transactionTimeoutSeconds = 30, int staleTimeoutMinutes = 5)
        {
            _transactions = new ConcurrentDictionary<string, TransactionContext>();
            _transactionTimeout = TimeSpan.FromSeconds(transactionTimeoutSeconds);
            _staleTransactionTimeout = TimeSpan.FromMinutes(staleTimeoutMinutes);
            
            // Check for timeouts every second
            _timeoutChecker = new Timer(CheckTimeouts, null, 1000, 1000);
        }

        
        /// Create a new transaction context
        
        public TransactionContext CreateTransaction(string sessionId, IsoMessage request)
        {
            var context = new TransactionContext
            {
                TransactionId = GenerateTransactionId(),
                SessionId = sessionId,
                Request = request
            };

            _transactions.TryAdd(context.TransactionId, context);
            Console.WriteLine($"[STATE-MACHINE] Created transaction {context.TransactionId} for session {sessionId}");
            
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
            foreach (var kvp in _transactions)
            {
                if (kvp.Value.SessionId == sessionId && 
                    kvp.Value.Request?.GetSTAN() == stan)
                {
                    return kvp.Value;
                }
            }
            return null;
        }

        
        /// Remove completed transaction from tracking
        
        public void CompleteTransaction(string transactionId)
        {
            if (_transactions.TryRemove(transactionId, out var context))
            {
                Console.WriteLine($"[STATE-MACHINE] Transaction {transactionId} completed and removed from tracking");
            }
        }

        
        /// Check for timed out transactions
        
        private void CheckTimeouts(object? state)
        {
            var now = DateTime.UtcNow;

            foreach (var kvp in _transactions)
            {
                var context = kvp.Value;

                if (context.State == TransactionState.Routing)
                {
                    if (context.SentToIssuerAt.HasValue && 
                        now - context.SentToIssuerAt.Value > _transactionTimeout)
                    {
                        HandleTimeout(context);
                    }
                }

                // Clean up stale completed/failed transactions
                if (context.IsTerminalState() && 
                    context.CompletedAt.HasValue &&
                    now - context.CompletedAt.Value > _staleTransactionTimeout)
                {
                    _transactions.TryRemove(kvp.Key, out _);
                }
            }
        }

        private void HandleTimeout(TransactionContext context)
        {
            if (context.TryTransitionTo(TransactionState.Failed))
            {
                Console.WriteLine($"[STATE-MACHINE] Transaction {context.TransactionId} TIMED OUT");
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
            return $"TXN-{DateTime.UtcNow:yyyyHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
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

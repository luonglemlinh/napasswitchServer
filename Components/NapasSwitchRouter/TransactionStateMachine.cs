using System;
using System.Collections.Concurrent;
using System.Threading;
using core.Models;

namespace router
{
    
    /// Transaction states in the switch processing lifecycle
    
    public enum TransactionState
    {
        // Initial states
        Received,           // Message received from ACQ
        Validated,          // Message passed validation
        
        // Routing states
        RoutingToIssuer,    // Being forwarded to ISS
        AwaitingResponse,   // Waiting for ISS response
        
        // Response states
        ResponseReceived,   // Got response from ISS
        Completed,          // Successfully completed
        
        // Error states
        ValidationFailed,   // Message validation failed
        RoutingFailed,      // Could not route to ISS
        Timeout,            // ISS did not respond in time
        SystemError,        // Internal error
        
        // Reversal states
        ReversalRequired,   // Original timed out, need to reverse
        ReversalPending,    // Reversal sent to ISS
        ReversalCompleted,  // Reversal confirmed
        ReversalFailed,     // Reversal failed - needs manual intervention
        
        // Partial reversal states
        PartialReversalPending,
        PartialReversalCompleted
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
        public DateTime? ResponseReceivedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public int RetryCount { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }

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
                if (!IsValidTransition(State, newState))
                {
                    Console.WriteLine($"[STATE-MACHINE] Invalid transition: {State} -> {newState} for {TransactionId}");
                    return false;
                }

                var oldState = State;
                State = newState;
                ErrorCode = errorCode;
                ErrorMessage = errorMessage;

                // Update timestamps
                switch (newState)
                {
                    case TransactionState.RoutingToIssuer:
                        SentToIssuerAt = DateTime.UtcNow;
                        break;
                    case TransactionState.ResponseReceived:
                        ResponseReceivedAt = DateTime.UtcNow;
                        break;
                    case TransactionState.Completed:
                    case TransactionState.ReversalCompleted:
                    case TransactionState.ReversalFailed:
                        CompletedAt = DateTime.UtcNow;
                        break;
                }

                Console.WriteLine($"[STATE-MACHINE] {TransactionId}: {oldState} -> {newState}");
                return true;
            }
        }

        
        /// Check if a state transition is valid
        
        private static bool IsValidTransition(TransactionState from, TransactionState to)
        {
            return (from, to) switch
            {
                // Normal flow
                (TransactionState.Received, TransactionState.Validated) => true,
                (TransactionState.Received, TransactionState.ValidationFailed) => true,
                (TransactionState.Validated, TransactionState.RoutingToIssuer) => true,
                (TransactionState.Validated, TransactionState.RoutingFailed) => true,
                (TransactionState.RoutingToIssuer, TransactionState.AwaitingResponse) => true,
                (TransactionState.RoutingToIssuer, TransactionState.RoutingFailed) => true,
                (TransactionState.AwaitingResponse, TransactionState.ResponseReceived) => true,
                (TransactionState.AwaitingResponse, TransactionState.Timeout) => true,
                (TransactionState.ResponseReceived, TransactionState.Completed) => true,
                
                // Error flows
                (TransactionState.RoutingToIssuer, TransactionState.Timeout) => true,
                (TransactionState.RoutingToIssuer, TransactionState.SystemError) => true,
                (TransactionState.AwaitingResponse, TransactionState.SystemError) => true,
                
                // Reversal flows
                (TransactionState.Timeout, TransactionState.ReversalRequired) => true,
                (TransactionState.ReversalRequired, TransactionState.ReversalPending) => true,
                (TransactionState.ReversalPending, TransactionState.ReversalCompleted) => true,
                (TransactionState.ReversalPending, TransactionState.ReversalFailed) => true,
                
                // Partial reversal
                (TransactionState.Completed, TransactionState.PartialReversalPending) => true,
                (TransactionState.PartialReversalPending, TransactionState.PartialReversalCompleted) => true,
                (TransactionState.PartialReversalPending, TransactionState.ReversalFailed) => true,

                _ => false
            };
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
                   State == TransactionState.ValidationFailed ||
                   State == TransactionState.ReversalCompleted ||
                   State == TransactionState.ReversalFailed ||
                   State == TransactionState.PartialReversalCompleted;
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

                // Check for transaction timeout (waiting for ISS response)
                if (context.State == TransactionState.AwaitingResponse ||
                    context.State == TransactionState.RoutingToIssuer)
                {
                    if (context.SentToIssuerAt.HasValue && 
                        now - context.SentToIssuerAt.Value > _transactionTimeout)
                    {
                        HandleTimeout(context);
                    }
                }

                // Clean up stale completed transactions
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
            if (context.TryTransitionTo(TransactionState.Timeout))
            {
                Console.WriteLine($"[STATE-MACHINE] Transaction {context.TransactionId} TIMED OUT");
                OnTransactionTimeout?.Invoke(context);

                // For authorization requests, we may need to send reversal
                if (context.Request?.MessageType == "0200")
                {
                    context.TryTransitionTo(TransactionState.ReversalRequired);
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
            foreach (var field in new[] { 2, 3, 4, 7, 11, 12, 13, 32, 33, 37, 41, 42, 49 })
            {
                if (context.Request.HasField(field))
                    reversal.SetField(field, context.Request.GetField(field));
            }

            // Set reversal reason: timeout
            reversal.SetField(56, "4021"); // Transaction timeout

            context.ReversalRequest = reversal;
            context.TryTransitionTo(TransactionState.ReversalPending);

            return reversal;
        }

        private string GenerateTransactionId()
        {
            return $"TXN-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        }

        public TransactionStateMachineStats GetStats()
        {
            int pending = 0, completed = 0, failed = 0, reversals = 0;

            foreach (var kvp in _transactions)
            {
                switch (kvp.Value.State)
                {
                    case TransactionState.RoutingToIssuer:
                    case TransactionState.AwaitingResponse:
                        pending++;
                        break;
                    case TransactionState.Completed:
                        completed++;
                        break;
                    case TransactionState.Timeout:
                    case TransactionState.SystemError:
                    case TransactionState.RoutingFailed:
                        failed++;
                        break;
                    case TransactionState.ReversalPending:
                    case TransactionState.ReversalRequired:
                        reversals++;
                        break;
                }
            }

            return new TransactionStateMachineStats
            {
                ActiveTransactions = _transactions.Count,
                PendingCount = pending,
                CompletedCount = completed,
                FailedCount = failed,
                PendingReversalsCount = reversals
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
        public int PendingCount { get; set; }
        public int CompletedCount { get; set; }
        public int FailedCount { get; set; }
        public int PendingReversalsCount { get; set; }
    }
}

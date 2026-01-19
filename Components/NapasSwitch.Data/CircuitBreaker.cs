using System;
using System.Threading;

namespace data
{
    
    /// Circuit breaker states
    
    public enum CircuitBreakerState
    {
        Closed,     // Normal operation - requests flow through
        Open,       // Circuit tripped - requests fail fast
        HalfOpen    // Testing if service recovered
    }

    
    /// Circuit breaker pattern implementation for fault tolerance
    /// Prevents cascading failures by failing fast when a service is down
    
    public class CircuitBreaker
    {
        private readonly int _failureThreshold;
        private readonly int _successThreshold;
        private readonly TimeSpan _openDuration;
        
        private CircuitBreakerState _state = CircuitBreakerState.Closed;
        private int _failureCount;
        private int _successCount;
        private DateTime _lastStateChange;
        private readonly object _lock = new object();

        public CircuitBreakerState State => _state;
        public int FailureCount => _failureCount;

        
        /// Create a circuit breaker
        
        /// <param name="failureThreshold">Number of failures before opening circuit</param>
        /// <param name="successThreshold">Number of successes in half-open before closing</param>
        /// <param name="openDurationSeconds">How long to wait before trying again</param>
        public CircuitBreaker(int failureThreshold = 5, int successThreshold = 2, int openDurationSeconds = 30)
        {
            _failureThreshold = failureThreshold;
            _successThreshold = successThreshold;
            _openDuration = TimeSpan.FromSeconds(openDurationSeconds);
            _lastStateChange = DateTime.UtcNow;
        }

        
        /// Check if request should be allowed
        
        public bool AllowRequest()
        {
            lock (_lock)
            {
                switch (_state)
                {
                    case CircuitBreakerState.Closed:
                        return true;

                    case CircuitBreakerState.Open:
                        // Check if we should transition to half-open
                        if (DateTime.UtcNow - _lastStateChange >= _openDuration)
                        {
                            TransitionTo(CircuitBreakerState.HalfOpen);
                            return true;
                        }
                        return false;

                    case CircuitBreakerState.HalfOpen:
                        // Allow limited requests in half-open
                        return true;

                    default:
                        return false;
                }
            }
        }

        
        /// Record a successful operation
        
        public void RecordSuccess()
        {
            lock (_lock)
            {
                switch (_state)
                {
                    case CircuitBreakerState.Closed:
                        _failureCount = 0;
                        break;

                    case CircuitBreakerState.HalfOpen:
                        _successCount++;
                        if (_successCount >= _successThreshold)
                        {
                            TransitionTo(CircuitBreakerState.Closed);
                        }
                        break;
                }
            }
        }

        
        /// Record a failed operation
        
        public void RecordFailure()
        {
            lock (_lock)
            {
                switch (_state)
                {
                    case CircuitBreakerState.Closed:
                        _failureCount++;
                        if (_failureCount >= _failureThreshold)
                        {
                            TransitionTo(CircuitBreakerState.Open);
                        }
                        break;

                    case CircuitBreakerState.HalfOpen:
                        // Single failure in half-open trips the breaker again
                        TransitionTo(CircuitBreakerState.Open);
                        break;
                }
            }
        }

        private void TransitionTo(CircuitBreakerState newState)
        {
            if (_state != newState)
            {
                Console.WriteLine($"[CIRCUIT-BREAKER] State transition: {_state} -> {newState}");
                _state = newState;
                _lastStateChange = DateTime.UtcNow;
                _failureCount = 0;
                _successCount = 0;
            }
        }

        
        /// Execute an action with circuit breaker protection
        
        public void Execute(Action action, Action? fallback = null)
        {
            if (!AllowRequest())
            {
                Console.WriteLine("[CIRCUIT-BREAKER] Circuit is OPEN, executing fallback");
                fallback?.Invoke();
                return;
            }

            try
            {
                action();
                RecordSuccess();
            }
            catch (Exception)
            {
                RecordFailure();
                fallback?.Invoke();
            }
        }

        
        /// Execute an async action with circuit breaker protection
        
        public async System.Threading.Tasks.Task ExecuteAsync(Func<System.Threading.Tasks.Task> action, Func<System.Threading.Tasks.Task>? fallback = null)
        {
            if (!AllowRequest())
            {
                Console.WriteLine("[CIRCUIT-BREAKER] Circuit is OPEN, executing fallback");
                if (fallback != null)
                    await fallback();
                return;
            }

            try
            {
                await action();
                RecordSuccess();
            }
            catch (Exception)
            {
                RecordFailure();
                if (fallback != null)
                    await fallback();
            }
        }

        
        /// Reset the circuit breaker manually
        
        public void Reset()
        {
            lock (_lock)
            {
                TransitionTo(CircuitBreakerState.Closed);
            }
        }
    }
}

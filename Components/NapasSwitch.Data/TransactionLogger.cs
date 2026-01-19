using System;
using System.Data.SqlClient;
using System.Threading.Tasks;
using core.Models;
using data.Models;

namespace data
{
    
    /// Handles logging of all transactions to database
    /// Uses circuit breaker pattern for fault tolerance
    /// Falls back to file logging when database is unavailable
    
    public class TransactionLogger : IDisposable
    {
        private readonly string _connectionString;
        private readonly bool _enableLogging;
        private readonly CircuitBreaker _circuitBreaker;
        private readonly FallbackTransactionLogger _fallbackLogger;
        private bool _disposed;

        public CircuitBreakerState CircuitState => _circuitBreaker.State;
        public int PendingFallbackCount => _fallbackLogger.PendingCount;

        public TransactionLogger(string connectionString, bool enableLogging = true)
        {
            _connectionString = connectionString;
            _enableLogging = enableLogging;
            _circuitBreaker = new CircuitBreaker(
                failureThreshold: 5,      // Open after 5 consecutive failures
                successThreshold: 2,       // Close after 2 successes in half-open
                openDurationSeconds: 30    // Wait 30 seconds before retry
            );
            _fallbackLogger = new FallbackTransactionLogger();
        }

        
        /// Log a complete transaction (request + response pair)
        
        public async Task LogTransactionAsync(
            IsoMessage request,
            IsoMessage response,
            string sessionId,
            int processingTimeMs,
            string direction = "COMPLETE")
        {
            if (!_enableLogging) return;

            await _circuitBreaker.ExecuteAsync(
                async () => await LogTransactionToDbAsync(request, response, sessionId, processingTimeMs, direction),
                async () =>
                {
                    // Fallback: Log to file
                    _fallbackLogger.LogTransaction(request, response, sessionId, processingTimeMs, direction);
                    await Task.CompletedTask;
                }
            );
        }

        private async Task LogTransactionToDbAsync(
            IsoMessage request,
            IsoMessage response,
            string sessionId,
            int processingTimeMs,
            string direction)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                string query = @"
                                INSERT INTO TransactionLog 
                                (SessionId, MessageType, PAN, ProcessingCode, Amount, STAN, 
                                 AcquirerID, IssuerID, ResponseCode, TerminalID, MerchantID,
                                 TransactionTime, LoggedAt, ProcessingTimeMs, Direction)
                                VALUES 
                                (@SessionId, @MessageType, @PAN, @ProcessingCode, @Amount, @STAN,
                                 @AcquirerID, @IssuerID, @ResponseCode, @TerminalID, @MerchantID,
                                 @TransactionTime, @LoggedAt, @ProcessingTimeMs, @Direction)";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@SessionId", sessionId);
                    command.Parameters.AddWithValue("@MessageType", request.MessageType);
                    command.Parameters.AddWithValue("@PAN", (object?)request.GetPAN() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@ProcessingCode", (object?)request.GetProcessingCode() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@Amount", ParseAmount(request.GetAmount()));
                    command.Parameters.AddWithValue("@STAN", (object?)request.GetSTAN() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@AcquirerID", (object?)request.GetAcquirerID() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@IssuerID", (object?)request.GetIssuerID() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@ResponseCode", (object?)response.GetResponseCode() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@TerminalID", (object?)request.GetTerminalID() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@MerchantID", (object?)request.GetMerchantID() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@TransactionTime", DateTime.Now);
                    command.Parameters.AddWithValue("@LoggedAt", DateTime.Now);
                    command.Parameters.AddWithValue("@ProcessingTimeMs", processingTimeMs);
                    command.Parameters.AddWithValue("@Direction", direction);

                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        
        /// Log just a request (for inbound tracking)
        
        public async Task LogRequestAsync(IsoMessage request, string sessionId, string direction = "INBOUND")
        {
            if (!_enableLogging) return;

            await _circuitBreaker.ExecuteAsync(
                async () => await LogRequestToDbAsync(request, sessionId, direction),
                async () =>
                {
                    // Fallback: Log to file
                    _fallbackLogger.LogTransaction(request, null, sessionId, 0, direction);
                    await Task.CompletedTask;
                }
            );
        }

        private async Task LogRequestToDbAsync(IsoMessage request, string sessionId, string direction)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                string query = @"
                                INSERT INTO TransactionLog 
                                (SessionId, MessageType, PAN, ProcessingCode, Amount, STAN, 
                                 AcquirerID, IssuerID, TerminalID, MerchantID,
                                 TransactionTime, LoggedAt, Direction)
                                VALUES 
                                (@SessionId, @MessageType, @PAN, @ProcessingCode, @Amount, @STAN,
                                 @AcquirerID, @IssuerID, @TerminalID, @MerchantID,
                                 @TransactionTime, @LoggedAt, @Direction)";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@SessionId", sessionId);
                    command.Parameters.AddWithValue("@MessageType", request.MessageType);
                    command.Parameters.AddWithValue("@PAN", (object?)request.GetPAN() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@ProcessingCode", (object?)request.GetProcessingCode() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@Amount", ParseAmount(request.GetAmount()));
                    command.Parameters.AddWithValue("@STAN", (object?)request.GetSTAN() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@AcquirerID", (object?)request.GetAcquirerID() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@IssuerID", (object?)request.GetIssuerID() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@TerminalID", (object?)request.GetTerminalID() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@MerchantID", (object?)request.GetMerchantID() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@TransactionTime", DateTime.Now);
                    command.Parameters.AddWithValue("@LoggedAt", DateTime.Now);
                    command.Parameters.AddWithValue("@Direction", direction);

                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        
        /// Try to recover fallback logs to database
        
        public async Task<int> RecoverFallbackLogsAsync()
        {
            if (_circuitBreaker.State != CircuitBreakerState.Closed)
            {
                Console.WriteLine("[DB] Cannot recover fallback logs - circuit breaker is not closed");
                return 0;
            }

            return await _fallbackLogger.RecoverToDatabase(this);
        }

        private decimal ParseAmount(string? amountStr)
        {
            if (string.IsNullOrEmpty(amountStr)) return 0;

            // ISO amounts are in smallest currency unit (cents)
            if (long.TryParse(amountStr, out long cents))
                return cents / 100m;

            return 0;
        }

        
        /// Get transaction statistics for monitoring
        
        public async Task<TransactionStats> GetStatsAsync(DateTime from, DateTime to)
        {
            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    string query = @"
                                    SELECT 
                                        COUNT(*) as TotalCount,
                                        SUM(CASE WHEN ResponseCode = '00' THEN 1 ELSE 0 END) as ApprovedCount,
                                        SUM(CASE WHEN ResponseCode != '00' THEN 1 ELSE 0 END) as DeclinedCount,
                                        AVG(ProcessingTimeMs) as AvgProcessingTime,
                                        SUM(Amount) as TotalAmount
                                    FROM TransactionLog
                                    WHERE TransactionTime BETWEEN @From AND @To
                                    AND Direction = 'COMPLETE'";

                    using (var command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@From", from);
                        command.Parameters.AddWithValue("@To", to);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                return new TransactionStats
                                {
                                    TotalCount = reader.GetInt32(0),
                                    ApprovedCount = reader.GetInt32(1),
                                    DeclinedCount = reader.GetInt32(2),
                                    AvgProcessingTimeMs = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                                    TotalAmount = reader.IsDBNull(4) ? 0 : reader.GetDecimal(4)
                                };
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to get stats: {ex.Message}");
            }

            return new TransactionStats();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _fallbackLogger?.Dispose();
        }
    }

    
    /// Transaction statistics for monitoring and reporting
    
    public class TransactionStats
    {
        public int TotalCount { get; set; }
        public int ApprovedCount { get; set; }
        public int DeclinedCount { get; set; }
        public int AvgProcessingTimeMs { get; set; }
        public decimal TotalAmount { get; set; }

        public decimal ApprovalRate => TotalCount > 0 ? (ApprovedCount * 100m / TotalCount) : 0;
    }
}

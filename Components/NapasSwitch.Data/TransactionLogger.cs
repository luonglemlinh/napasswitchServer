using System;
using System.Data.SqlClient;
using System.Threading.Tasks;
using core.Models;
using data.Models;

namespace data
{
    /// <summary>
    /// Handles logging of all transactions to database
    /// This is critical for audit trails and compliance
    /// </summary>
    public class TransactionLogger
    {
        private readonly string _connectionString;
        private readonly bool _enableLogging;

        public TransactionLogger(string connectionString, bool enableLogging = true)
        {
            _connectionString = connectionString;
            _enableLogging = enableLogging;
        }

        /// <summary>
        /// Log a complete transaction (request + response pair)
        /// </summary>
        public async Task LogTransactionAsync(
            IsoMessage request,
            IsoMessage response,
            string sessionId,
            int processingTimeMs,
            string direction = "COMPLETE")
        {
            if (!_enableLogging) return;

            try
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
            catch (Exception ex)
            {
                // Don't throw - logging failures shouldn't crash the switch
                Console.WriteLine($"[ERROR] Failed to log transaction: {ex.Message}");
            }
        }

        /// <summary>
        /// Log just a request (for inbound tracking)
        /// </summary>
        public async Task LogRequestAsync(IsoMessage request, string sessionId, string direction = "INBOUND")
        {
            if (!_enableLogging) return;

            try
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
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to log request: {ex.Message}");
            }
        }

        private decimal ParseAmount(string? amountStr)
        {
            if (string.IsNullOrEmpty(amountStr)) return 0;

            // ISO amounts are in smallest currency unit (cents)
            if (long.TryParse(amountStr, out long cents))
                return cents / 100m;

            return 0;
        }

        /// <summary>
        /// Get transaction statistics for monitoring
        /// </summary>
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
    }

    /// <summary>
    /// Transaction statistics for monitoring and reporting
    /// </summary>
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

using System;
using core.Helpers;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using System.IO;
using core.Models;

namespace data
{
    
    /// Handles logging of all transactions to database
    /// Falls back to local file logging when database is unavailable
    
    public class TransactionLogger : IDisposable
    {
        private readonly string _connectionString;
        private readonly bool _enableLogging;

        public TransactionLogger(string connectionString, bool enableLogging = true)
        {
            _connectionString = connectionString;
            _enableLogging = enableLogging;
        }

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
                await LogTransactionToDbAsync(request, response, sessionId, processingTimeMs, direction);
            }
            catch (Exception ex)
            {
                SwitchLogger.Info($"[DB-LOG-ERROR] Connection failed, falling back to message log: {ex.Message}");
                core.Helpers.MessageLogger.LogMessage(sessionId, direction, response ?? request);
            }
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
                    command.Parameters.AddWithValue("@TransactionTime", DateTime.UtcNow);
                    command.Parameters.AddWithValue("@LoggedAt", DateTime.UtcNow);
                    command.Parameters.AddWithValue("@ProcessingTimeMs", processingTimeMs);
                    command.Parameters.AddWithValue("@Direction", direction);

                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        public async Task LogRequestAsync(IsoMessage request, string sessionId, string direction = "INBOUND")
        {
            if (!_enableLogging) return;

            try 
            {
                await LogRequestToDbAsync(request, sessionId, direction);
            }
            catch (Exception ex)
            {
                SwitchLogger.Info($"[DB-LOG-ERROR] Connection failed, falling back to message log: {ex.Message}");
                core.Helpers.MessageLogger.LogMessage(sessionId, direction, request);
            }
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
                                 TransactionTime, LoggedAt, ProcessingTimeMs, Direction)
                                VALUES 
                                (@SessionId, @MessageType, @PAN, @ProcessingCode, @Amount, @STAN,
                                 @AcquirerID, @IssuerID, @TerminalID, @MerchantID,
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
                    command.Parameters.AddWithValue("@TerminalID", (object?)request.GetTerminalID() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@MerchantID", (object?)request.GetMerchantID() ?? DBNull.Value);
                    command.Parameters.AddWithValue("@TransactionTime", DateTime.UtcNow);
                    command.Parameters.AddWithValue("@LoggedAt", DateTime.UtcNow);
                    command.Parameters.AddWithValue("@ProcessingTimeMs", 0); // Inbound requests don't have processing time yet
                    command.Parameters.AddWithValue("@Direction", direction);

                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        private decimal ParseAmount(string? amountStr)
        {
            if (string.IsNullOrEmpty(amountStr)) return 0;
            if (long.TryParse(amountStr, out long cents)) return cents / 100m;
            return 0;
        }

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
                                    TotalCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                                    ApprovedCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                                    DeclinedCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
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
                SwitchLogger.Info($"[ERROR] Failed to get stats: {ex.Message}");
            }

            return new TransactionStats();
        }

        public void Dispose()
        {
        }
    }

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

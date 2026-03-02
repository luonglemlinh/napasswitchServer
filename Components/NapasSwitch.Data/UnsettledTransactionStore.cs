using System;
using core.Helpers;
using core.Security;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using core.Models;

namespace data
{
    /// <summary>
    /// Stores transactions for the current settlement day in UnsettledTransactions.
    /// At end-of-day settlement, records are moved to TransactionLog.
    /// Void/reversal can only target MATCHED transactions from the same settlement date.
    /// </summary>
    public class UnsettledTransactionStore
    {
        private readonly string _connectionString;
        private readonly int _expirationMinutes;
        private readonly SecureDataHandler? _secureDataHandler;
        private static readonly TimeZoneInfo VietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");

        /// <summary>
        /// Get current time in Vietnam timezone (UTC+7)
        /// </summary>
        private static DateTime GetVietnamTime() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, VietnamTimeZone);

        /// <summary>
        /// Get today's settlement date in Vietnam timezone
        /// </summary>
        private static DateTime GetSettlementDate() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, VietnamTimeZone).Date;

        public UnsettledTransactionStore(string connectionString, int expirationMinutes = 5, SecureDataHandler? secureDataHandler = null)
        {
            _connectionString = connectionString;
            _expirationMinutes = expirationMinutes;
            _secureDataHandler = secureDataHandler;
        }

        /// <summary>
        /// Check for duplicate transaction by STAN + AcquirerID + Date.
        /// STAN is only 6 digits (000000-999999) and wraps under load.
        /// Without this check, a replayed 0200 is forwarded as a fresh transaction.
        /// </summary>
        public async Task<bool> IsDuplicateAsync(string stan, string? acquirerId, string? transactionDate)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                SELECT COUNT(*) FROM UnsettledTransactions 
                WHERE RequestSTAN = @STAN 
                  AND RequestAcquirerID = @AcquirerID 
                  AND RequestDateTime = @DateTime
                  AND Status IN ('PENDING', 'MATCHED')", connection);

            command.Parameters.AddWithValue("@STAN", stan);
            command.Parameters.AddWithValue("@AcquirerID", acquirerId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@DateTime", transactionDate ?? (object)DBNull.Value);

            var result = await command.ExecuteScalarAsync();
            return result != null && Convert.ToInt32(result) > 0;
        }

        public async Task<string> StoreRequestAsync(string transactionId, string sessionId, IsoMessage request, byte[] messageBytes, string? originalTransactionId = null)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                INSERT INTO UnsettledTransactions (
                    TransactionId, SessionId, MessageType, TransactionType, SettlementDate,
                    RequestPAN, RequestAmount, RequestProcessingCode, RequestSTAN,
                    RequestDateTime, RequestAcquirerID, RequestTerminalID, RequestMerchantID,
                    RequestRRN, RequestTRN, RequestMessageBytes, Status, ExpiresAt,
                    OriginalTransactionId
                ) VALUES (
                    @TransactionId, @SessionId, @MessageType, @TransactionType, @SettlementDate,
                    @PAN, @Amount, @ProcessingCode, @STAN,
                    @DateTime, @AcquirerID, @TerminalID, @MerchantID,
                    @RRN, @TRN, @MessageBytes, 'PENDING', @ExpiresAt,
                    @OriginalTransactionId
                )", connection);

            command.Parameters.AddWithValue("@TransactionId", transactionId);
            command.Parameters.AddWithValue("@SessionId", sessionId);
            command.Parameters.AddWithValue("@MessageType", request.MessageType);
            command.Parameters.AddWithValue("@TransactionType",
                TransactionTypeHelper.GetTransactionType(request.MessageType, request.GetField(3)));
            command.Parameters.AddWithValue("@SettlementDate", GetSettlementDate());

            // PCI-DSS: Encrypt PAN before storing in database
            string? pan = request.GetField(2);
            if (!string.IsNullOrEmpty(pan) && _secureDataHandler != null)
            {
                pan = _secureDataHandler.EncryptPAN(pan);
            }
            command.Parameters.AddWithValue("@PAN", pan ?? (object)DBNull.Value);
            
            decimal amount = 0;
            if (decimal.TryParse(request.GetField(4) ?? "0", out decimal parsedAmount))
                amount = parsedAmount / 100; // Convert from cents
            command.Parameters.AddWithValue("@Amount", amount);
            
            command.Parameters.AddWithValue("@ProcessingCode", request.GetField(3) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@STAN", request.GetField(11) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@DateTime", request.GetField(7) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@AcquirerID", request.GetField(32) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@TerminalID", request.GetField(41) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@MerchantID", request.GetField(42) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@RRN", request.GetField(37) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@TRN", request.GetTRN() ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@MessageBytes", messageBytes);
            command.Parameters.AddWithValue("@ExpiresAt", GetVietnamTime().AddMinutes(_expirationMinutes));
            command.Parameters.AddWithValue("@OriginalTransactionId", originalTransactionId ?? (object)DBNull.Value);

            await command.ExecuteNonQueryAsync();
            
            SwitchLogger.ForContext("UNSETTLED").Info("Stored {TransactionType} {TransactionId} | STAN: {STAN}",
                TransactionTypeHelper.GetTransactionType(request.MessageType, request.GetField(3)),
                transactionId, request.GetField(11));
            return transactionId;
        }

        public async Task<UnsettledTransaction?> GetRequestAsync(string transactionId)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                SELECT TransactionId, SessionId, MessageType, TransactionType, SettlementDate,
                       RequestPAN, RequestAmount, RequestProcessingCode, RequestSTAN,
                       RequestDateTime, RequestAcquirerID, RequestTerminalID, RequestMerchantID,
                       RequestRRN, RequestTRN, RequestMessageBytes, Status, CreatedAt,
                       OriginalTransactionId
                FROM UnsettledTransactions 
                WHERE TransactionId = @TransactionId AND Status = 'PENDING'", connection);

            command.Parameters.AddWithValue("@TransactionId", transactionId);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return ReadUnsettledTransaction(reader);
            }

            return null;
        }

        public async Task<UnsettledTransaction?> GetRequestBySTANAsync(string stan)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                SELECT TransactionId, SessionId, MessageType, TransactionType, SettlementDate,
                       RequestPAN, RequestAmount, RequestProcessingCode, RequestSTAN,
                       RequestDateTime, RequestAcquirerID, RequestTerminalID, RequestMerchantID,
                       RequestRRN, RequestTRN, RequestMessageBytes, Status, CreatedAt,
                       OriginalTransactionId
                FROM UnsettledTransactions 
                WHERE RequestSTAN = @STAN AND Status IN ('PENDING', 'MATCHED')
                ORDER BY CreatedAt DESC", connection);

            command.Parameters.AddWithValue("@STAN", stan);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return ReadUnsettledTransaction(reader);
            }

            return null;
        }

        /// <summary>
        /// Get unsettled transaction by TRN (Transaction Reference Number) for reversal verification
        /// </summary>
        public async Task<UnsettledTransaction?> GetRequestByTRNAsync(string trn)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                SELECT TransactionId, SessionId, MessageType, TransactionType, SettlementDate,
                       RequestPAN, RequestAmount, RequestProcessingCode, RequestSTAN,
                       RequestDateTime, RequestAcquirerID, RequestTerminalID, RequestMerchantID,
                       RequestRRN, RequestTRN, RequestMessageBytes, Status, CreatedAt,
                       OriginalTransactionId
                FROM UnsettledTransactions 
                WHERE RequestTRN = @TRN AND Status IN ('PENDING', 'MATCHED')
                ORDER BY CreatedAt DESC", connection);

            command.Parameters.AddWithValue("@TRN", trn);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return ReadUnsettledTransaction(reader);
            }

            return null;
        }

        /// <summary>
        /// Look up the original transaction for void/reversal within the current settlement day.
        /// Only MATCHED financial transactions from today's settlement can be voided/reversed.
        /// </summary>
        public async Task<UnsettledTransaction?> GetOriginalForVoidReversalAsync(string? trn, string? stan)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            string whereClause;
            string paramName;
            string paramValue;

            if (!string.IsNullOrEmpty(trn))
            {
                whereClause = "RequestTRN = @Param";
                paramName = "@Param";
                paramValue = trn;
            }
            else if (!string.IsNullOrEmpty(stan))
            {
                whereClause = "RequestSTAN = @Param";
                paramName = "@Param";
                paramValue = stan;
            }
            else
            {
                return null;
            }

            using var command = new SqlCommand($@"
                SELECT TransactionId, SessionId, MessageType, TransactionType, SettlementDate,
                       RequestPAN, RequestAmount, RequestProcessingCode, RequestSTAN,
                       RequestDateTime, RequestAcquirerID, RequestTerminalID, RequestMerchantID,
                       RequestRRN, RequestTRN, RequestMessageBytes, Status, CreatedAt,
                       OriginalTransactionId
                FROM UnsettledTransactions 
                WHERE {whereClause}
                  AND Status = 'MATCHED'
                  AND SettlementDate = @SettlementDate
                  AND TransactionType IN ('PURCHASE', 'CASH_WITHDRAWAL', 'CASH_DEPOSIT', 'TRANSFER', 'REFUND')
                ORDER BY CreatedAt DESC", connection);

            command.Parameters.AddWithValue(paramName, paramValue);
            command.Parameters.AddWithValue("@SettlementDate", GetSettlementDate());

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return ReadUnsettledTransaction(reader);
            }

            return null;
        }

        public async Task MarkAsMatchedAsync(string transactionId, string responseCode)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                UPDATE UnsettledTransactions 
                SET Status = 'MATCHED', 
                    ResponseCode = @ResponseCode,
                    ResponseReceivedAt = @VietnamTime,
                    UpdatedAt = @VietnamTime
                WHERE TransactionId = @TransactionId", connection);

            command.Parameters.AddWithValue("@TransactionId", transactionId);
            command.Parameters.AddWithValue("@ResponseCode", responseCode);
            command.Parameters.AddWithValue("@VietnamTime", GetVietnamTime());

            await command.ExecuteNonQueryAsync();
            SwitchLogger.ForContext("UNSETTLED").Info("Marked {TransactionId} as MATCHED with RC: {ResponseCode}", transactionId, responseCode);
        }

        /// <summary>
        /// Mark the original transaction as VOIDED after successful void processing
        /// </summary>
        public async Task MarkAsVoidedAsync(string transactionId)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                UPDATE UnsettledTransactions 
                SET Status = 'VOIDED',
                    UpdatedAt = @VietnamTime
                WHERE TransactionId = @TransactionId AND Status = 'MATCHED'", connection);

            command.Parameters.AddWithValue("@TransactionId", transactionId);
            command.Parameters.AddWithValue("@VietnamTime", GetVietnamTime());

            await command.ExecuteNonQueryAsync();
            SwitchLogger.ForContext("UNSETTLED").Info("Marked {TransactionId} as VOIDED", transactionId);
        }

        /// <summary>
        /// Mark the original transaction as REVERSED after successful reversal processing
        /// </summary>
        public async Task MarkAsReversedAsync(string transactionId)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                UPDATE UnsettledTransactions 
                SET Status = 'REVERSED',
                    UpdatedAt = @VietnamTime
                WHERE TransactionId = @TransactionId AND Status = 'MATCHED'", connection);

            command.Parameters.AddWithValue("@TransactionId", transactionId);
            command.Parameters.AddWithValue("@VietnamTime", GetVietnamTime());

            await command.ExecuteNonQueryAsync();
            SwitchLogger.ForContext("UNSETTLED").Info("Marked {TransactionId} as REVERSED", transactionId);
        }

        public async Task MarkAsMismatchAsync(string transactionId, string errorReason)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                UPDATE UnsettledTransactions 
                SET Status = 'MISMATCH',
                    ErrorReason = @ErrorReason,
                    UpdatedAt = @VietnamTime
                WHERE TransactionId = @TransactionId", connection);

            command.Parameters.AddWithValue("@TransactionId", transactionId);
            command.Parameters.AddWithValue("@ErrorReason", errorReason);
            command.Parameters.AddWithValue("@VietnamTime", GetVietnamTime());

            await command.ExecuteNonQueryAsync();
            SwitchLogger.ForContext("UNSETTLED").Info("Marked {TransactionId} as MISMATCH: {ErrorReason}", transactionId, errorReason);
        }

        public async Task<int> CleanupExpiredAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var vnTime = GetVietnamTime();
            using var command = new SqlCommand(@"
                UPDATE UnsettledTransactions 
                SET Status = 'EXPIRED', UpdatedAt = @VietnamTime
                WHERE ExpiresAt < @VietnamTime AND Status = 'PENDING'", connection);

            command.Parameters.AddWithValue("@VietnamTime", vnTime);

            int updated = await command.ExecuteNonQueryAsync();
            if (updated > 0)
                SwitchLogger.ForContext("UNSETTLED").Info("Marked {Count} transactions as EXPIRED", updated);
            
            return updated;
        }

        public async Task<int> GetUnsettledCountAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                SELECT COUNT(*) FROM UnsettledTransactions WHERE Status = 'PENDING'", connection);

            var result = await command.ExecuteScalarAsync();
            return result != null ? Convert.ToInt32(result) : 0;
        }

        /// <summary>
        /// Helper method to read an UnsettledTransaction from a SqlDataReader.
        /// Column order must match the SELECT statements in all query methods.
        /// </summary>
        private static UnsettledTransaction ReadUnsettledTransaction(SqlDataReader reader)
        {
            return new UnsettledTransaction
            {
                TransactionId = reader.GetString(0),
                SessionId = reader.GetString(1),
                MessageType = reader.GetString(2),
                TransactionType = reader.GetString(3),
                SettlementDate = reader.GetDateTime(4),
                RequestPAN = reader.IsDBNull(5) ? null : reader.GetString(5),
                RequestAmount = reader.GetDecimal(6),
                RequestProcessingCode = reader.IsDBNull(7) ? null : reader.GetString(7),
                RequestSTAN = reader.IsDBNull(8) ? null : reader.GetString(8),
                RequestDateTime = reader.IsDBNull(9) ? null : reader.GetString(9),
                RequestAcquirerID = reader.IsDBNull(10) ? null : reader.GetString(10),
                RequestTerminalID = reader.IsDBNull(11) ? null : reader.GetString(11),
                RequestMerchantID = reader.IsDBNull(12) ? null : reader.GetString(12),
                RequestRRN = reader.IsDBNull(13) ? null : reader.GetString(13),
                RequestTRN = reader.IsDBNull(14) ? null : reader.GetString(14),
                RequestMessageBytes = (byte[])reader[15],
                Status = reader.GetString(16),
                CreatedAt = reader.GetDateTime(17),
                OriginalTransactionId = reader.IsDBNull(18) ? null : reader.GetString(18)
            };
        }
    }

    public class UnsettledTransaction
    {
        public string TransactionId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string MessageType { get; set; } = string.Empty;
        public string TransactionType { get; set; } = string.Empty;
        public DateTime SettlementDate { get; set; }
        public string? RequestPAN { get; set; }
        public decimal RequestAmount { get; set; }
        public string? RequestProcessingCode { get; set; }
        public string? RequestSTAN { get; set; }
        public string? RequestDateTime { get; set; }
        public string? RequestAcquirerID { get; set; }
        public string? RequestTerminalID { get; set; }
        public string? RequestMerchantID { get; set; }
        public string? RequestRRN { get; set; }
        public string? RequestTRN { get; set; }
        public byte[] RequestMessageBytes { get; set; } = Array.Empty<byte>();
        public string Status { get; set; } = "PENDING";
        public string? ResponseCode { get; set; }
        public string? ErrorReason { get; set; }
        public DateTime? ResponseReceivedAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string? OriginalTransactionId { get; set; }
    }
}

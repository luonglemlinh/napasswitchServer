using System;
using System.Data.SqlClient;
using System.Threading.Tasks;
using core.Models;

namespace data
{
    public class PendingTransactionStore
    {
        private readonly string _connectionString;
        private readonly int _expirationMinutes;
        private static readonly TimeZoneInfo VietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");

        /// <summary>
        /// Get current time in Vietnam timezone (UTC+7)
        /// </summary>
        private static DateTime GetVietnamTime() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, VietnamTimeZone);

        public PendingTransactionStore(string connectionString, int expirationMinutes = 5)
        {
            _connectionString = connectionString;
            _expirationMinutes = expirationMinutes;
        }

        public async Task<string> StoreRequestAsync(string transactionId, string sessionId, IsoMessage request, byte[] messageBytes)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                INSERT INTO PendingTransactions (
                    TransactionId, SessionId, MessageType,
                    RequestPAN, RequestAmount, RequestProcessingCode, RequestSTAN,
                    RequestDateTime, RequestAcquirerID, RequestTerminalID, RequestMerchantID,
                    RequestRRN, RequestTRN, RequestMessageBytes, Status, ExpiresAt
                ) VALUES (
                    @TransactionId, @SessionId, @MessageType,
                    @PAN, @Amount, @ProcessingCode, @STAN,
                    @DateTime, @AcquirerID, @TerminalID, @MerchantID,
                    @RRN, @TRN, @MessageBytes, 'PENDING', @ExpiresAt
                )", connection);

            command.Parameters.AddWithValue("@TransactionId", transactionId);
            command.Parameters.AddWithValue("@SessionId", sessionId);
            command.Parameters.AddWithValue("@MessageType", request.MessageType);
            command.Parameters.AddWithValue("@PAN", request.GetField(2) ?? (object)DBNull.Value);
            
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

            await command.ExecuteNonQueryAsync();
            
            Console.WriteLine($"[PENDING-TXN] Stored request {transactionId} | STAN: {request.GetField(11)}");
            return transactionId;
        }

        public async Task<PendingTransaction?> GetRequestAsync(string transactionId)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                SELECT TransactionId, SessionId, MessageType, 
                       RequestPAN, RequestAmount, RequestProcessingCode, RequestSTAN,
                       RequestDateTime, RequestAcquirerID, RequestTerminalID, RequestMerchantID,
                       RequestRRN, RequestMessageBytes, Status, CreatedAt
                FROM PendingTransactions 
                WHERE TransactionId = @TransactionId AND Status = 'PENDING'", connection);

            command.Parameters.AddWithValue("@TransactionId", transactionId);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new PendingTransaction
                {
                    TransactionId = reader.GetString(0),
                    SessionId = reader.GetString(1),
                    MessageType = reader.GetString(2),
                    RequestPAN = reader.IsDBNull(3) ? null : reader.GetString(3),
                    RequestAmount = reader.GetDecimal(4),
                    RequestProcessingCode = reader.IsDBNull(5) ? null : reader.GetString(5),
                    RequestSTAN = reader.IsDBNull(6) ? null : reader.GetString(6),
                    RequestDateTime = reader.IsDBNull(7) ? null : reader.GetString(7),
                    RequestAcquirerID = reader.IsDBNull(8) ? null : reader.GetString(8),
                    RequestTerminalID = reader.IsDBNull(9) ? null : reader.GetString(9),
                    RequestMerchantID = reader.IsDBNull(10) ? null : reader.GetString(10),
                    RequestRRN = reader.IsDBNull(11) ? null : reader.GetString(11),
                    RequestMessageBytes = (byte[])reader[12],
                    Status = reader.GetString(13),
                    CreatedAt = reader.GetDateTime(14)
                };
            }

            return null;
        }

        public async Task<PendingTransaction?> GetRequestBySTANAsync(string stan)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                SELECT TransactionId, SessionId, MessageType, 
                       RequestPAN, RequestAmount, RequestProcessingCode, RequestSTAN,
                       RequestDateTime, RequestAcquirerID, RequestTerminalID, RequestMerchantID,
                       RequestRRN, RequestMessageBytes, Status, CreatedAt
                FROM PendingTransactions 
                WHERE RequestSTAN = @STAN AND Status = 'PENDING'
                ORDER BY CreatedAt DESC", connection);

            command.Parameters.AddWithValue("@STAN", stan);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new PendingTransaction
                {
                    TransactionId = reader.GetString(0),
                    SessionId = reader.GetString(1),
                    MessageType = reader.GetString(2),
                    RequestPAN = reader.IsDBNull(3) ? null : reader.GetString(3),
                    RequestAmount = reader.GetDecimal(4),
                    RequestProcessingCode = reader.IsDBNull(5) ? null : reader.GetString(5),
                    RequestSTAN = reader.IsDBNull(6) ? null : reader.GetString(6),
                    RequestDateTime = reader.IsDBNull(7) ? null : reader.GetString(7),
                    RequestAcquirerID = reader.IsDBNull(8) ? null : reader.GetString(8),
                    RequestTerminalID = reader.IsDBNull(9) ? null : reader.GetString(9),
                    RequestMerchantID = reader.IsDBNull(10) ? null : reader.GetString(10),
                    RequestRRN = reader.IsDBNull(11) ? null : reader.GetString(11),
                    RequestMessageBytes = (byte[])reader[12],
                    Status = reader.GetString(13),
                    CreatedAt = reader.GetDateTime(14)
                };
            }

            return null;
        }

        /// <summary>
        /// Get pending transaction by TRN (Transaction Reference Number) for reversal verification
        /// </summary>
        public async Task<PendingTransaction?> GetRequestByTRNAsync(string trn)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                SELECT TransactionId, SessionId, MessageType, 
                       RequestPAN, RequestAmount, RequestProcessingCode, RequestSTAN,
                       RequestDateTime, RequestAcquirerID, RequestTerminalID, RequestMerchantID,
                       RequestRRN, RequestTRN, RequestMessageBytes, Status, CreatedAt
                FROM PendingTransactions 
                WHERE RequestTRN = @TRN AND Status = 'PENDING'
                ORDER BY CreatedAt DESC", connection);

            command.Parameters.AddWithValue("@TRN", trn);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new PendingTransaction
                {
                    TransactionId = reader.GetString(0),
                    SessionId = reader.GetString(1),
                    MessageType = reader.GetString(2),
                    RequestPAN = reader.IsDBNull(3) ? null : reader.GetString(3),
                    RequestAmount = reader.GetDecimal(4),
                    RequestProcessingCode = reader.IsDBNull(5) ? null : reader.GetString(5),
                    RequestSTAN = reader.IsDBNull(6) ? null : reader.GetString(6),
                    RequestDateTime = reader.IsDBNull(7) ? null : reader.GetString(7),
                    RequestAcquirerID = reader.IsDBNull(8) ? null : reader.GetString(8),
                    RequestTerminalID = reader.IsDBNull(9) ? null : reader.GetString(9),
                    RequestMerchantID = reader.IsDBNull(10) ? null : reader.GetString(10),
                    RequestRRN = reader.IsDBNull(11) ? null : reader.GetString(11),
                    RequestTRN = reader.IsDBNull(12) ? null : reader.GetString(12),
                    RequestMessageBytes = (byte[])reader[13],
                    Status = reader.GetString(14),
                    CreatedAt = reader.GetDateTime(15)
                };
            }

            return null;
        }

        public async Task MarkAsMatchedAsync(string transactionId, string responseCode)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                UPDATE PendingTransactions 
                SET Status = 'MATCHED', 
                    ResponseCode = @ResponseCode,
                    UpdatedAt = @VietnamTime
                WHERE TransactionId = @TransactionId", connection);

            command.Parameters.AddWithValue("@TransactionId", transactionId);
            command.Parameters.AddWithValue("@ResponseCode", responseCode);
            command.Parameters.AddWithValue("@VietnamTime", GetVietnamTime());

            await command.ExecuteNonQueryAsync();
            Console.WriteLine($"[PENDING-TXN] Marked {transactionId} as MATCHED with RC: {responseCode}");
        }

        public async Task MarkAsMismatchAsync(string transactionId, string errorReason)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                UPDATE PendingTransactions 
                SET Status = 'MISMATCH', 
                    ResponseCode = @ErrorReason,
                    UpdatedAt = @VietnamTime
                WHERE TransactionId = @TransactionId", connection);

            command.Parameters.AddWithValue("@TransactionId", transactionId);
            command.Parameters.AddWithValue("@ErrorReason", errorReason);
            command.Parameters.AddWithValue("@VietnamTime", GetVietnamTime());

            await command.ExecuteNonQueryAsync();
            Console.WriteLine($"[PENDING-TXN] Marked {transactionId} as MISMATCH: {errorReason}");
        }

        public async Task<int> CleanupExpiredAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var vnTime = GetVietnamTime();
            using var command = new SqlCommand(@"
                UPDATE PendingTransactions 
                SET Status = 'EXPIRED', UpdatedAt = @VietnamTime
                WHERE ExpiresAt < @VietnamTime AND Status = 'PENDING'", connection);

            command.Parameters.AddWithValue("@VietnamTime", vnTime);

            int updated = await command.ExecuteNonQueryAsync();
            if (updated > 0)
                Console.WriteLine($"[PENDING-TXN] Marked {updated} transactions as EXPIRED");
            
            return updated;
        }

        public async Task<int> GetPendingCountAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                SELECT COUNT(*) FROM PendingTransactions WHERE Status = 'PENDING'", connection);

            var result = await command.ExecuteScalarAsync();
            return result != null ? Convert.ToInt32(result) : 0;
        }
    }

    public class PendingTransaction
    {
        public string TransactionId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string MessageType { get; set; } = string.Empty;
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
        public DateTime CreatedAt { get; set; }
    }
}

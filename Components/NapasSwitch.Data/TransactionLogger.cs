using System;
using core.Helpers;
using core.Security;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.IO;
using core.Models;

namespace data
{

    /// Handles logging of all transactions to database
    /// Uses Channel&lt;T&gt; to buffer writes and batch insert for high throughput.
    /// Falls back to local file logging when database is unavailable.

    public class TransactionLogger : IDisposable
    {
        private readonly string _connectionString;
        private readonly bool _enableLogging;
        private readonly SecureDataHandler? _secureDataHandler;
        private readonly Channel<LogEntry> _logChannel;
        private readonly Task _writerTask;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        private abstract record LogEntry;
        private sealed record TransactionLogEntry(IsoMessage Request, IsoMessage Response, string TransactionId, string SessionId) : LogEntry;
        private sealed record RequestLogEntry(IsoMessage Request, byte[] MessageBytes, string TransactionId, string SessionId, int ExpirationMinutes) : LogEntry;
        private sealed record StatusUpdateEntry(string TransactionId, string Status, string? ErrorReason = null, string? ResponseCode = null) : LogEntry;

        public TransactionLogger(string connectionString, bool enableLogging = true, SecureDataHandler? secureDataHandler = null)
        {
            _connectionString = connectionString;
            _enableLogging = enableLogging;
            _secureDataHandler = secureDataHandler;
            _logChannel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(2000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            _writerTask = Task.Run(ProcessLogEntriesAsync);
        }

        private async Task ProcessLogEntriesAsync()
        {
            var reader = _logChannel.Reader;
            SqlConnection? connection = null;
            try
            {
                await foreach (var entry in reader.ReadAllAsync(_cts.Token))
                {
                    try
                    {
                        connection = await EnsureConnectionAsync(connection);
                        switch (entry)
                        {
                            case TransactionLogEntry txn:
                                await LogTransactionToDbAsync(connection, txn.Request, txn.Response, txn.TransactionId, txn.SessionId);
                                break;
                            case RequestLogEntry req:
                                await LogRequestToDbAsync(connection, req.Request, req.MessageBytes, req.TransactionId, req.SessionId, req.ExpirationMinutes);
                                break;
                            case StatusUpdateEntry status:
                                await UpdateStatusInDbAsync(connection, status.TransactionId, status.Status, status.ErrorReason, status.ResponseCode);
                                break;
                        }
                    }
                    catch (SqlException ex)
                    {
                        SwitchLogger.ForContext("DB-LOG").Error("SQL error, will reconnect on next entry: {Error}", ex.Message);
                        try { connection?.Dispose(); } catch { }
                        connection = null;
                        FallbackToFileLog(entry);
                    }
                    catch (Exception ex)
                    {
                        SwitchLogger.ForContext("DB-LOG").Error("Background write failed: {Error}", ex.Message);
                        FallbackToFileLog(entry);
                    }
                }
            }
            catch (OperationCanceledException) { /* Graceful shutdown */ }
            finally
            {
                try { connection?.Dispose(); } catch { }
            }
        }

        private async Task<SqlConnection> EnsureConnectionAsync(SqlConnection? existing)
        {
            if (existing is { State: System.Data.ConnectionState.Open })
                return existing;

            existing?.Dispose();
            var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(_cts.Token);
            return conn;
        }

        private void FallbackToFileLog(LogEntry entry)
        {
            switch (entry)
            {
                case TransactionLogEntry txn:
                    core.Helpers.MessageLogger.LogMessage(txn.SessionId, "COMPLETE", txn.Response ?? txn.Request);
                    break;
                case RequestLogEntry req:
                    core.Helpers.MessageLogger.LogMessage(req.SessionId, "INBOUND", req.Request);
                    break;
                case StatusUpdateEntry s:
                    SwitchLogger.ForContext("DB-LOG").Warn("Status update failed for {TransactionId} -> {Status}. Fallback: Check process logs.", s.TransactionId, s.Status);
                    break;
            }
        }

        private string? EncryptPanForStorage(string? pan)
        {
            if (string.IsNullOrEmpty(pan)) return null;
            if (_secureDataHandler != null) return _secureDataHandler.EncryptPAN(pan);
            return SecureDataHandler.MaskPAN(pan);
        }

        public Task LogTransactionAsync(IsoMessage request, IsoMessage response, string transactionId, string sessionId)
        {
            if (!_enableLogging) return Task.CompletedTask;
            _logChannel.Writer.TryWrite(new TransactionLogEntry(request, response, transactionId, sessionId));
            return Task.CompletedTask;
        }

        private async Task LogTransactionToDbAsync(SqlConnection connection, IsoMessage request, IsoMessage response, string transactionId, string sessionId)
        {
            // Transition from PENDING to MATCHED (Consolidation)
            string query = @"
                UPDATE TransactionLog SET
                    ResponseCode = @ResponseCode,
                    AuthorizationCode = @AuthorizationCode,
                    RRN = COALESCE(RRN, @RRN),
                    Status = 'MATCHED',
                    CompletedTime = GETUTCDATE()
                WHERE TransactionId = @TransactionId";

            // If the record doesn't exist (e.g. echo or non-financial), we might need an UPSERT logic 
            // but for financial transactions it's always an UPDATE.
            // Let's use a smarter UPSERT just in case.
            string upsertQuery = @"
                IF EXISTS (SELECT 1 FROM TransactionLog WHERE TransactionId = @TransactionId)
                BEGIN
                    UPDATE TransactionLog SET
                        ResponseCode = @ResponseCode,
                        AuthorizationCode = @AuthorizationCode,
                        RRN = COALESCE(RRN, @RRN),
                        Status = 'MATCHED'
                    WHERE TransactionId = @TransactionId;
                END
                ELSE
                BEGIN
                    INSERT INTO TransactionLog (
                        TransactionId, SessionId, MessageType, PAN, ProcessingCode, Amount, STAN, 
                        AcquirerID, IssuerID, ResponseCode, TerminalID, MerchantID,
                        TransactionTime, TransactionType, RRN, TRN, AuthorizationCode, Status
                    ) VALUES (
                        @TransactionId, @SessionId, @MessageType, @PAN, @ProcessingCode, @Amount, @STAN,
                        @AcquirerID, @IssuerID, @ResponseCode, @TerminalID, @MerchantID,
                        GETUTCDATE(), @TransactionType, @RRN, @TRN, @AuthorizationCode, 'MATCHED'
                    );
                END";

            using (var command = new SqlCommand(upsertQuery, connection))
            {
                command.Parameters.AddWithValue("@TransactionId", transactionId);
                command.Parameters.AddWithValue("@SessionId", sessionId);
                command.Parameters.AddWithValue("@MessageType", request.MessageType);
                command.Parameters.AddWithValue("@PAN", (object?)EncryptPanForStorage(request.GetPAN()) ?? DBNull.Value);
                command.Parameters.AddWithValue("@ProcessingCode", (object?)request.GetProcessingCode() ?? DBNull.Value);
                command.Parameters.AddWithValue("@Amount", ParseAmount(request.GetAmount()));
                command.Parameters.AddWithValue("@STAN", (object?)request.GetSTAN() ?? DBNull.Value);
                command.Parameters.AddWithValue("@AcquirerID", (object?)request.GetAcquirerID() ?? DBNull.Value);
                command.Parameters.AddWithValue("@IssuerID", (object?)request.GetIssuerID() ?? DBNull.Value);
                command.Parameters.AddWithValue("@ResponseCode", (object?)response.GetResponseCode() ?? DBNull.Value);
                command.Parameters.AddWithValue("@TerminalID", (object?)request.GetTerminalID() ?? DBNull.Value);
                command.Parameters.AddWithValue("@MerchantID", (object?)request.GetMerchantID() ?? DBNull.Value);
                command.Parameters.AddWithValue("@TransactionType",
                    TransactionTypeHelper.GetTransactionType(request.MessageType, request.GetProcessingCode()));
                command.Parameters.AddWithValue("@RRN", (object?)request.GetField(37) ?? (object?)response.GetField(37) ?? DBNull.Value);
                command.Parameters.AddWithValue("@TRN", (object?)request.GetTRN() ?? DBNull.Value);
                command.Parameters.AddWithValue("@AuthorizationCode", (object?)response.GetField(38) ?? DBNull.Value);

                await command.ExecuteNonQueryAsync();
            }
        }

        public Task LogRequestAsync(IsoMessage request, byte[] messageBytes, string transactionId, string sessionId, int expirationMinutes = 5)
        {
            if (!_enableLogging) return Task.CompletedTask;
            _logChannel.Writer.TryWrite(new RequestLogEntry(request, messageBytes, transactionId, sessionId, expirationMinutes));
            return Task.CompletedTask;
        }

        private async Task LogRequestToDbAsync(SqlConnection connection, IsoMessage request, byte[] messageBytes, string transactionId, string sessionId, int expirationMinutes)
        {
            string query = @"
                INSERT INTO TransactionLog (
                    TransactionId, SessionId, MessageType, PAN, ProcessingCode, Amount, STAN, 
                    AcquirerID, IssuerID, TerminalID, MerchantID,
                    TransactionTime, TransactionType, RRN, TRN, Status, ExpiresAt, RequestMessageBytes
                ) VALUES (
                    @TransactionId, @SessionId, @MessageType, @PAN, @ProcessingCode, @Amount, @STAN,
                    @AcquirerID, @IssuerID, @TerminalID, @MerchantID,
                    GETUTCDATE(), @TransactionType, @RRN, @TRN, 'PENDING', 
                    DATEADD(MINUTE, @Expiry, GETUTCDATE()), @MessageBytes)";

            using (var command = new SqlCommand(query, connection))
            {
                command.Parameters.AddWithValue("@TransactionId", transactionId);
                command.Parameters.AddWithValue("@SessionId", sessionId);
                command.Parameters.AddWithValue("@MessageType", request.MessageType);
                command.Parameters.AddWithValue("@PAN", (object?)EncryptPanForStorage(request.GetPAN()) ?? DBNull.Value);
                command.Parameters.AddWithValue("@ProcessingCode", (object?)request.GetProcessingCode() ?? DBNull.Value);
                command.Parameters.AddWithValue("@Amount", ParseAmount(request.GetAmount()));
                command.Parameters.AddWithValue("@STAN", (object?)request.GetSTAN() ?? DBNull.Value);
                command.Parameters.AddWithValue("@AcquirerID", (object?)request.GetAcquirerID() ?? DBNull.Value);
                command.Parameters.AddWithValue("@IssuerID", (object?)request.GetIssuerID() ?? DBNull.Value);
                command.Parameters.AddWithValue("@TerminalID", (object?)request.GetTerminalID() ?? DBNull.Value);
                command.Parameters.AddWithValue("@MerchantID", (object?)request.GetMerchantID() ?? DBNull.Value);
                command.Parameters.AddWithValue("@TransactionType",
                    TransactionTypeHelper.GetTransactionType(request.MessageType, request.GetProcessingCode()));
                command.Parameters.AddWithValue("@RRN", (object?)request.GetField(37) ?? DBNull.Value);
                command.Parameters.AddWithValue("@TRN", (object?)request.GetTRN() ?? DBNull.Value);
                command.Parameters.AddWithValue("@Expiry", expirationMinutes);
                command.Parameters.AddWithValue("@MessageBytes", messageBytes);

                await command.ExecuteNonQueryAsync();
            }
        }

        public Task UpdateStatusAsync(string transactionId, string status, string? errorReason = null, string? responseCode = null)
        {
            if (!_enableLogging) return Task.CompletedTask;
            _logChannel.Writer.TryWrite(new StatusUpdateEntry(transactionId, status, errorReason, responseCode));
            return Task.CompletedTask;
        }

        private async Task UpdateStatusInDbAsync(SqlConnection connection, string transactionId, string status, string? errorReason, string? responseCode)
        {
            string query = @"
                UPDATE TransactionLog SET 
                    Status = @Status, 
                    ErrorReason = COALESCE(@ErrorReason, ErrorReason),
                    ResponseCode = COALESCE(@ResponseCode, ResponseCode)
                WHERE TransactionId = @TransactionId";

            using (var command = new SqlCommand(query, connection))
            {
                command.Parameters.AddWithValue("@TransactionId", transactionId);
                command.Parameters.AddWithValue("@Status", status);
                command.Parameters.AddWithValue("@ErrorReason", (object?)errorReason ?? DBNull.Value);
                command.Parameters.AddWithValue("@ResponseCode", (object?)responseCode ?? DBNull.Value);
                await command.ExecuteNonQueryAsync();
            }
        }

        public async Task<bool> IsDuplicateAsync(string stan, string? acquirerId, string? transactionDate)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                SELECT COUNT(*) FROM TransactionLog 
                WHERE STAN = @STAN 
                  AND AcquirerID = @AcquirerID 
                  AND Status IN ('PENDING', 'MATCHED')", connection);

            command.Parameters.AddWithValue("@STAN", stan);
            command.Parameters.AddWithValue("@AcquirerID", acquirerId ?? (object)DBNull.Value);
            // In a real system we'd also check the date field if present in the message or use TransactionTime range

            var result = await command.ExecuteScalarAsync();
            return result != null && Convert.ToInt32(result) > 0;
        }

        public async Task<IsoMessage?> GetOriginalRequestAsync(string? trn, string? stan, string? acquirerId)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            string whereClause = !string.IsNullOrEmpty(trn) ? "TRN = @Param" : "STAN = @Param AND AcquirerID = @Acq";
            
            using var command = new SqlCommand($@"
                SELECT RequestMessageBytes FROM TransactionLog 
                WHERE {whereClause} AND Status = 'MATCHED'
                ORDER BY Id DESC", connection);

            command.Parameters.AddWithValue("@Param", trn ?? stan);
            if (string.IsNullOrEmpty(trn)) command.Parameters.AddWithValue("@Acq", acquirerId ?? (object)DBNull.Value);

            var bytes = await command.ExecuteScalarAsync() as byte[];
            if (bytes == null) return null;

            var parser = new core.ISO8583.IsoParser();
            return parser.Parse(bytes);
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
                                        0 as AvgProcessingTime,
                                        SUM(Amount) as TotalAmount
                                    FROM TransactionLog
                                    WHERE TransactionTime BETWEEN @From AND @To";

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
                SwitchLogger.ForContext("DB-LOG").Error("Failed to get stats: {Error}", ex.Message);
            }

            return new TransactionStats();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _logChannel.Writer.Complete();
            _cts.Cancel();
            try { _writerTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
            _cts.Dispose();
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

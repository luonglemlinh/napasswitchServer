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

    public record OriginalTransactionInfo(IsoMessage Message, string TransactionId);

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
        private sealed record TransactionLogEntry(IsoMessage Request, IsoMessage Response, string TransactionId, string SessionId, string? CurrencyCode = null, string? PosEntryMode = null, string? SettlementDate = null, string? OriginalTransactionId = null) : LogEntry;
        private sealed record RequestLogEntry(IsoMessage Request, byte[] MessageBytes, string TransactionId, string SessionId, int ExpirationMinutes, string? CurrencyCode = null, string? PosEntryMode = null, string? SettlementDate = null, string? OriginalTransactionId = null) : LogEntry;
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
                                await LogTransactionToDbAsync(connection, txn.Request, txn.Response, txn.TransactionId, txn.SessionId, txn.CurrencyCode, txn.PosEntryMode, txn.SettlementDate, txn.OriginalTransactionId);
                                break;
                            case RequestLogEntry req:
                                await LogRequestToDbAsync(connection, req.Request, req.MessageBytes, req.TransactionId, req.SessionId, req.ExpirationMinutes, req.CurrencyCode, req.PosEntryMode, req.SettlementDate, req.OriginalTransactionId);
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

        public Task LogTransactionAsync(IsoMessage request, IsoMessage response, string transactionId, string sessionId, string? currencyCode = null, string? posEntryMode = null, string? settlementDate = null, string? originalTransactionId = null)
        {
            if (!_enableLogging) return Task.CompletedTask;
            _logChannel.Writer.TryWrite(new TransactionLogEntry(request, response, transactionId, sessionId, currencyCode, posEntryMode, settlementDate, originalTransactionId));
            return Task.CompletedTask;
        }

        private async Task LogTransactionToDbAsync(SqlConnection connection, IsoMessage request, IsoMessage response, string transactionId, string sessionId, string? currencyCode, string? posEntryMode, string? settlementDate, string? originalTransactionId)
        {
            string upsertQuery = @"
                IF EXISTS (SELECT 1 FROM TransactionLog WHERE TRANSACTIONID = @TRANSACTIONID)
                BEGIN
                    UPDATE TransactionLog SET
                        RESPONSECODE = @RESPONSECODE,
                        AUTHORIZATIONCODE = @AUTHORIZATIONCODE,
                        RRN = COALESCE(RRN, @RRN),
                        CURRENCYCODE = COALESCE(CURRENCYCODE, @CURRENCYCODE),
                        POSENTRYMODE = COALESCE(POSENTRYMODE, @POSENTRYMODE),
                        STATUS = 'MATCHED'
                    WHERE TRANSACTIONID = @TRANSACTIONID;
                END
                ELSE
                BEGIN
                    INSERT INTO TransactionLog (
                        TRANSACTIONID, SESSIONID, MESSAGETYPE, PAN, PROCESSINGCODE, AMOUNT, STAN, 
                        ACQUIRERID, ISSUERID, RESPONSECODE, TERMINALID, MERCHANTID,
                        TRANSACTIONTIME, TRANSACTIONTYPE, RRN, TRN, AUTHORIZATIONCODE, STATUS,
                        CURRENCYCODE, POSENTRYMODE, SETTLEMENTDATE, ORIGINALTRANSACTIONID
                    ) VALUES (
                        @TRANSACTIONID, @SESSIONID, @MESSAGETYPE, @PAN, @PROCESSINGCODE, @AMOUNT, @STAN,
                        @ACQUIRERID, @ISSUERID, @RESPONSECODE, @TERMINALID, @MERCHANTID,
                        GETUTCDATE(), @TRANSACTIONTYPE, @RRN, @TRN, @AUTHORIZATIONCODE, 'MATCHED',
                        @CURRENCYCODE, @POSENTRYMODE, @SETTLEMENTDATE, @ORIGINALTRANSACTIONID
                    );
                END";

            using (var command = new SqlCommand(upsertQuery, connection))
            {
                command.Parameters.AddWithValue("@TRANSACTIONID", transactionId);
                command.Parameters.AddWithValue("@SESSIONID", sessionId);
                command.Parameters.AddWithValue("@MESSAGETYPE", request.MessageType);
                command.Parameters.AddWithValue("@PAN", (object?)EncryptPanForStorage(request.GetPAN()) ?? DBNull.Value);
                command.Parameters.AddWithValue("@PROCESSINGCODE", (object?)request.GetProcessingCode() ?? DBNull.Value);
                command.Parameters.AddWithValue("@AMOUNT", ParseAmount(request.GetAmount()));
                command.Parameters.AddWithValue("@STAN", (object?)request.GetSTAN() ?? DBNull.Value);
                command.Parameters.AddWithValue("@ACQUIRERID", (object?)request.GetAcquirerID() ?? DBNull.Value);
                command.Parameters.AddWithValue("@ISSUERID", (object?)GetBestIssuerID(request, response) ?? DBNull.Value);
                command.Parameters.AddWithValue("@RESPONSECODE", (object?)response.GetResponseCode() ?? DBNull.Value);
                command.Parameters.AddWithValue("@TERMINALID", (object?)request.GetTerminalID() ?? DBNull.Value);
                command.Parameters.AddWithValue("@MERCHANTID", (object?)request.GetMerchantID() ?? DBNull.Value);
                command.Parameters.AddWithValue("@TRANSACTIONTYPE",
                    TransactionTypeHelper.GetTransactionType(request.MessageType, request.GetProcessingCode()));
                command.Parameters.AddWithValue("@RRN", (object?)request.GetField(37) ?? (object?)response.GetField(37) ?? DBNull.Value);
                command.Parameters.AddWithValue("@TRN", (object?)request.GetTRN() ?? DBNull.Value);
                command.Parameters.AddWithValue("@AUTHORIZATIONCODE", (object?)response.GetField(38) ?? DBNull.Value);
                
                command.Parameters.AddWithValue("@CURRENCYCODE", (object?)currencyCode ?? DBNull.Value);
                command.Parameters.AddWithValue("@POSENTRYMODE", (object?)posEntryMode ?? DBNull.Value);
                command.Parameters.AddWithValue("@SETTLEMENTDATE", string.IsNullOrEmpty(settlementDate) ? (object)DBNull.Value : settlementDate);
                command.Parameters.AddWithValue("@ORIGINALTRANSACTIONID", (object?)originalTransactionId ?? DBNull.Value);

                await command.ExecuteNonQueryAsync();
            }
        }

        public Task LogRequestAsync(IsoMessage request, byte[] messageBytes, string transactionId, string sessionId, int expirationMinutes = 5, string? currencyCode = null, string? posEntryMode = null, string? settlementDate = null, string? originalTransactionId = null)
        {
            if (!_enableLogging) return Task.CompletedTask;
            _logChannel.Writer.TryWrite(new RequestLogEntry(request, messageBytes, transactionId, sessionId, expirationMinutes, currencyCode, posEntryMode, settlementDate, originalTransactionId));
            return Task.CompletedTask;
        }

        private async Task LogRequestToDbAsync(SqlConnection connection, IsoMessage request, byte[] messageBytes, string transactionId, string sessionId, int expirationMinutes, string? currencyCode, string? posEntryMode, string? settlementDate, string? originalTransactionId)
        {
            string query = @"
                INSERT INTO TransactionLog (
                    TRANSACTIONID, SESSIONID, MESSAGETYPE, PAN, PROCESSINGCODE, AMOUNT, STAN, 
                    ACQUIRERID, ISSUERID, TERMINALID, MERCHANTID,
                    TRANSACTIONTIME, TRANSACTIONTYPE, RRN, TRN, STATUS, EXPIRESAT, REQUESTMESSAGEBYTES,
                    CURRENCYCODE, POSENTRYMODE, SETTLEMENTDATE, ORIGINALTRANSACTIONID
                ) VALUES (
                    @TRANSACTIONID, @SESSIONID, @MESSAGETYPE, @PAN, @PROCESSINGCODE, @AMOUNT, @STAN,
                    @ACQUIRERID, @ISSUERID, @TERMINALID, @MERCHANTID,
                    GETUTCDATE(), @TRANSACTIONTYPE, @RRN, @TRN, 'PENDING', 
                    DATEADD(MINUTE, @EXPIRY, GETUTCDATE()), @MESSAGEBYTES,
                    @CURRENCYCODE, @POSENTRYMODE, @SETTLEMENTDATE, @ORIGINALTRANSACTIONID)";

            using (var command = new SqlCommand(query, connection))
            {
                command.Parameters.AddWithValue("@TRANSACTIONID", transactionId);
                command.Parameters.AddWithValue("@SESSIONID", sessionId);
                command.Parameters.AddWithValue("@MESSAGETYPE", request.MessageType);
                command.Parameters.AddWithValue("@PAN", (object?)EncryptPanForStorage(request.GetPAN()) ?? DBNull.Value);
                command.Parameters.AddWithValue("@PROCESSINGCODE", (object?)request.GetProcessingCode() ?? DBNull.Value);
                command.Parameters.AddWithValue("@AMOUNT", ParseAmount(request.GetAmount()));
                command.Parameters.AddWithValue("@STAN", (object?)request.GetSTAN() ?? DBNull.Value);
                command.Parameters.AddWithValue("@ACQUIRERID", (object?)request.GetAcquirerID() ?? DBNull.Value);
                command.Parameters.AddWithValue("@ISSUERID", (object?)GetBestIssuerID(request, null) ?? DBNull.Value);
                command.Parameters.AddWithValue("@TERMINALID", (object?)request.GetTerminalID() ?? DBNull.Value);
                command.Parameters.AddWithValue("@MERCHANTID", (object?)request.GetMerchantID() ?? DBNull.Value);
                command.Parameters.AddWithValue("@TRANSACTIONTYPE",
                    TransactionTypeHelper.GetTransactionType(request.MessageType, request.GetProcessingCode()));
                command.Parameters.AddWithValue("@RRN", (object?)request.GetField(37) ?? DBNull.Value);
                command.Parameters.AddWithValue("@TRN", (object?)request.GetTRN() ?? DBNull.Value);
                command.Parameters.AddWithValue("@EXPIRY", expirationMinutes);
                command.Parameters.AddWithValue("@MESSAGEBYTES", messageBytes);
                
                command.Parameters.AddWithValue("@CURRENCYCODE", (object?)currencyCode ?? DBNull.Value);
                command.Parameters.AddWithValue("@POSENTRYMODE", (object?)posEntryMode ?? DBNull.Value);
                command.Parameters.AddWithValue("@SETTLEMENTDATE", string.IsNullOrEmpty(settlementDate) ? (object)DBNull.Value : settlementDate);
                command.Parameters.AddWithValue("@ORIGINALTRANSACTIONID", (object?)originalTransactionId ?? DBNull.Value);

                await command.ExecuteNonQueryAsync();
            }
        }

        private string? GetBestIssuerID(IsoMessage request, IsoMessage? response)
        {
            // Priority: DE#33 (Forwarding ID) > DE#100 (Receiving ID) > DE#32 (Acquirer ID - ONLY for responses where request has no ISS)
            string? iss = request.GetIssuerID();
            if (!string.IsNullOrEmpty(iss)) return iss;

            iss = request.GetField(100);
            if (!string.IsNullOrEmpty(iss)) return iss;

            if (response != null)
            {
                iss = response.GetIssuerID();
                if (!string.IsNullOrEmpty(iss)) return iss;
                
                iss = response.GetAcquirerID(); // In many responses, ACQ field sometimes holds switch-specific issuer IDs
                if (!string.IsNullOrEmpty(iss)) return iss;
            }

            return null;
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
                    STATUS = @STATUS, 
                    ERRORREASON = COALESCE(@ERRORREASON, ERRORREASON),
                    RESPONSECODE = COALESCE(@RESPONSECODE, RESPONSECODE)
                WHERE TRANSACTIONID = @TRANSACTIONID";

            using (var command = new SqlCommand(query, connection))
            {
                command.Parameters.AddWithValue("@TRANSACTIONID", transactionId);
                command.Parameters.AddWithValue("@STATUS", status);
                command.Parameters.AddWithValue("@ERRORREASON", (object?)errorReason ?? DBNull.Value);
                command.Parameters.AddWithValue("@RESPONSECODE", (object?)responseCode ?? DBNull.Value);
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
                  AND ACQUIRERID = @ACQUIRERID 
                  AND STATUS IN ('PENDING', 'MATCHED')", connection);

            command.Parameters.AddWithValue("@STAN", stan);
            command.Parameters.AddWithValue("@ACQUIRERID", acquirerId ?? (object)DBNull.Value);
            // In a real system we'd also check the date field if present in the message or use TransactionTime range

            var result = await command.ExecuteScalarAsync();
            return result != null && Convert.ToInt32(result) > 0;
        }

        public async Task<OriginalTransactionInfo?> GetOriginalRequestAsync(string? trn, string? stan, string? acquirerId)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            string whereClause = !string.IsNullOrEmpty(trn) ? "TRN = @PARAM" : "STAN = @PARAM AND ACQUIRERID = @ACQ";
            
            using var command = new SqlCommand($@"
                SELECT REQUESTMESSAGEBYTES, TRANSACTIONID FROM TransactionLog 
                WHERE {whereClause} AND STATUS = 'MATCHED'
                ORDER BY ID DESC", connection);

            command.Parameters.AddWithValue("@PARAM", trn ?? stan);
            if (string.IsNullOrEmpty(trn)) command.Parameters.AddWithValue("@ACQ", acquirerId ?? (object)DBNull.Value);

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            var bytes = reader["REQUESTMESSAGEBYTES"] as byte[];
            var originalTxnId = reader["TRANSACTIONID"]?.ToString();

            if (bytes == null || string.IsNullOrEmpty(originalTxnId)) return null;

            var parser = new core.ISO8583.IsoParser();
            var message = parser.Parse(bytes);
            return new OriginalTransactionInfo(message, originalTxnId);
        }

        private decimal ParseAmount(string? amountStr)
        {
            if (string.IsNullOrEmpty(amountStr)) return 0;
            if (long.TryParse(amountStr, out long cents)) return cents / 100m;
            return 0;
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
}

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
        private sealed record TransactionLogEntry(IsoMessage Request, IsoMessage Response, string SessionId, int ProcessingTimeMs, string Direction) : LogEntry;
        private sealed record RequestLogEntry(IsoMessage Request, string SessionId, string Direction) : LogEntry;

        public TransactionLogger(string connectionString, bool enableLogging = true, SecureDataHandler? secureDataHandler = null)
        {
            _connectionString = connectionString;
            _enableLogging = enableLogging;
            _secureDataHandler = secureDataHandler;
            _logChannel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(1000)
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
                                await LogTransactionToDbAsync(connection, txn.Request, txn.Response, txn.SessionId, txn.ProcessingTimeMs, txn.Direction);
                                break;
                            case RequestLogEntry req:
                                await LogRequestToDbAsync(connection, req.Request, req.SessionId, req.Direction);
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
                    core.Helpers.MessageLogger.LogMessage(txn.SessionId, txn.Direction, txn.Response ?? txn.Request);
                    break;
                case RequestLogEntry req:
                    core.Helpers.MessageLogger.LogMessage(req.SessionId, req.Direction, req.Request);
                    break;
            }
        }

        /// <summary>
        /// Encrypt PAN using the injected SecureDataHandler, or mask if unavailable.
        /// </summary>
        private string? EncryptPanForStorage(string? pan)
        {
            if (string.IsNullOrEmpty(pan)) return null;
            if (_secureDataHandler != null) return _secureDataHandler.EncryptPAN(pan);
            return SecureDataHandler.MaskPAN(pan);
        }

        public Task LogTransactionAsync(
            IsoMessage request,
            IsoMessage response,
            string sessionId,
            int processingTimeMs,
            string direction = "COMPLETE")
        {
            if (!_enableLogging) return Task.CompletedTask;
            if (!_logChannel.Writer.TryWrite(new TransactionLogEntry(request, response, sessionId, processingTimeMs, direction)))
            {
                ServerMetrics.IncrementDroppedLogEntries();
                SwitchLogger.ForContext("DB-LOG").Error("Channel full — falling back to file log. SessionId={SessionId}, Direction={Direction}", sessionId, direction);
                core.Helpers.MessageLogger.LogMessage(sessionId, $"DB-OVERFLOW-{direction}", response ?? request);
            }
            return Task.CompletedTask;
        }

        private async Task LogTransactionToDbAsync(
            SqlConnection connection,
            IsoMessage request,
            IsoMessage response,
            string sessionId,
            int processingTimeMs,
            string direction)
        {
            string query = @"
                            INSERT INTO TransactionLog 
                            (SessionId, MessageType, PAN, ProcessingCode, Amount, STAN, 
                             AcquirerID, IssuerID, ResponseCode, TerminalID, MerchantID,
                             TransactionTime, LoggedAt, ProcessingTimeMs, Direction, TransactionType)
                            VALUES 
                            (@SessionId, @MessageType, @PAN, @ProcessingCode, @Amount, @STAN,
                             @AcquirerID, @IssuerID, @ResponseCode, @TerminalID, @MerchantID,
                             @TransactionTime, @LoggedAt, @ProcessingTimeMs, @Direction, @TransactionType)";

            using (var command = new SqlCommand(query, connection))
            {
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
                command.Parameters.AddWithValue("@TransactionTime", DateTime.UtcNow);
                command.Parameters.AddWithValue("@LoggedAt", DateTime.UtcNow);
                command.Parameters.AddWithValue("@ProcessingTimeMs", processingTimeMs);
                command.Parameters.AddWithValue("@Direction", direction);
                command.Parameters.AddWithValue("@TransactionType",
                    TransactionTypeHelper.GetTransactionType(request.MessageType, request.GetProcessingCode()));

                await command.ExecuteNonQueryAsync();
            }
        }

        public Task LogRequestAsync(IsoMessage request, string sessionId, string direction = "INBOUND")
        {
            if (!_enableLogging) return Task.CompletedTask;
            if (!_logChannel.Writer.TryWrite(new RequestLogEntry(request, sessionId, direction)))
            {
                ServerMetrics.IncrementDroppedLogEntries();
                SwitchLogger.ForContext("DB-LOG").Error("Channel full — falling back to file log. SessionId={SessionId}, Direction={Direction}", sessionId, direction);
                core.Helpers.MessageLogger.LogMessage(sessionId, $"DB-OVERFLOW-{direction}", request);
            }
            return Task.CompletedTask;
        }

        private async Task LogRequestToDbAsync(SqlConnection connection, IsoMessage request, string sessionId, string direction)
        {
            string query = @"
                            INSERT INTO TransactionLog 
                            (SessionId, MessageType, PAN, ProcessingCode, Amount, STAN, 
                             AcquirerID, IssuerID, TerminalID, MerchantID,
                             TransactionTime, LoggedAt, ProcessingTimeMs, Direction, TransactionType)
                            VALUES 
                            (@SessionId, @MessageType, @PAN, @ProcessingCode, @Amount, @STAN,
                             @AcquirerID, @IssuerID, @TerminalID, @MerchantID,
                             @TransactionTime, @LoggedAt, @ProcessingTimeMs, @Direction, @TransactionType)";

            using (var command = new SqlCommand(query, connection))
            {
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
                command.Parameters.AddWithValue("@TransactionTime", DateTime.UtcNow);
                command.Parameters.AddWithValue("@LoggedAt", DateTime.UtcNow);
                command.Parameters.AddWithValue("@ProcessingTimeMs", 0);
                command.Parameters.AddWithValue("@Direction", direction);
                command.Parameters.AddWithValue("@TransactionType",
                    TransactionTypeHelper.GetTransactionType(request.MessageType, request.GetProcessingCode()));

                await command.ExecuteNonQueryAsync();
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

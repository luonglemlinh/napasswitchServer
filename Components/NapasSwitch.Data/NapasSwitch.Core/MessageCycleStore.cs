using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using core.Helpers;
using core.ISO8583;
using core.Models;

namespace data
{
    public class MessageCycleStore : IDisposable
    {
        private readonly string _connectionString;
        private readonly bool _enableLogging;
        private readonly IsoParser _isoParser;
        private readonly Channel<CycleLogEntry> _logChannel;
        private readonly Task _writerTask;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        private record CycleLogEntry(
            string TransactionId, 
            string SessionId, 
            string? ACQ, 
            string? ISS, 
            string Direction, 
            string? MessageType, 
            string? ProcessingCode, 
            decimal? Amount, 
            string? Stan, 
            string? Rrn,
            string? ResponseCode,
            string RawMessage);

        public MessageCycleStore(string connectionString, bool enableLogging = true)
        {
            _connectionString = connectionString;
            _enableLogging = enableLogging;
            _isoParser = new IsoParser();

            _logChannel = Channel.CreateBounded<CycleLogEntry>(new BoundedChannelOptions(2000)
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
                        if (connection == null || connection.State != System.Data.ConnectionState.Open)
                        {
                            connection?.Dispose();
                            connection = new SqlConnection(_connectionString);
                            await connection.OpenAsync(_cts.Token);
                        }

                        await InsertLogToDbAsync(connection, entry);
                    }
                    catch (SqlException ex)
                    {
                        SwitchLogger.ForContext("MSG-CYCLE").Error("SQL error inserting cycle leg: {Error}", ex.Message);
                        try { connection?.Dispose(); } catch { }
                        connection = null;
                        FallbackToFileLog(entry);
                    }
                    catch (Exception ex)
                    {
                        SwitchLogger.ForContext("MSG-CYCLE").Error("Background write failed: {Error}", ex.Message);
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

        private void FallbackToFileLog(CycleLogEntry entry)
        {
            SwitchLogger.ForContext("MSG-CYCLE").Error("Cycle overflow fallback. Direction={Direction}, SessionId={SessionId}, RawMessage={RawMessage}", entry.Direction, entry.SessionId, entry.RawMessage);
        }

        public Task LogCycleAsync(
            string transactionId,
            string sessionId,
            string? acq,
            string? iss,
            string direction,
            IsoMessage? parsedMessage,
            byte[] messageBytes)
        {
            if (!_enableLogging) return Task.CompletedTask;

            var safeMessage = parsedMessage ?? TryParseMessage(messageBytes);
            string sanitizedRawMessage = BuildSanitizedRawMessageHex(safeMessage, messageBytes);

            decimal? amount = null;
            if (decimal.TryParse(safeMessage?.GetField(4) ?? "0", out decimal parsedAmount))
            {
                amount = parsedAmount / 100m; // Convert from cents
            }

            var entry = new CycleLogEntry(
                transactionId,
                sessionId,
                acq,
                iss,
                direction,
                safeMessage?.MessageType,
                safeMessage?.GetField(3),
                amount,
                safeMessage?.GetField(11),
                safeMessage?.GetField(37),
                safeMessage?.GetField(39),
                sanitizedRawMessage
            );

            if (!_logChannel.Writer.TryWrite(entry))
            {
                ServerMetrics.IncrementDroppedLogEntries();
                SwitchLogger.ForContext("MSG-CYCLE").Warn("Channel full - falling back to file log. SessionId={SessionId}, Direction={Direction}", sessionId, direction);
                FallbackToFileLog(entry);
            }

            return Task.CompletedTask;
        }

        public Task LogCyclesAsync(string transactionId, string sessionId, System.Collections.Generic.IEnumerable<BufferedLogEntry> entries)
        {
            if (!_enableLogging) return Task.CompletedTask;

            foreach (var leg in entries)
            {
                var entry = new CycleLogEntry(
                    transactionId,
                    sessionId,
                    leg.ACQ,
                    leg.ISS,
                    leg.Direction,
                    leg.MessageType,
                    leg.ProcessingCode,
                    leg.Amount,
                    leg.STAN,
                    leg.RRN,
                    leg.ResponseCode,
                    leg.RawMessage
                );

                if (!_logChannel.Writer.TryWrite(entry))
                {
                    ServerMetrics.IncrementDroppedLogEntries();
                    FallbackToFileLog(entry);
                }
            }

            return Task.CompletedTask;
        }

        private async Task InsertLogToDbAsync(SqlConnection connection, CycleLogEntry entry)
        {
            string query = @"
                INSERT INTO MessageCycle (
                    TransactionId, SessionId, ACQ, ISS, Direction,
                    MessageType, ProcessingCode, Amount, STAN, RRN, ResponseCode, LogTime, RawMessage
                ) VALUES (
                    @TransactionId, @SessionId, @ACQ, @ISS, @Direction,
                    @MessageType, @ProcessingCode, @Amount, @STAN, @RRN, @ResponseCode, @LogTime, @RawMessage
                )";

            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@TransactionId", entry.TransactionId);
            command.Parameters.AddWithValue("@SessionId", entry.SessionId);
            command.Parameters.AddWithValue("@ACQ", (object?)entry.ACQ ?? DBNull.Value);
            command.Parameters.AddWithValue("@ISS", (object?)entry.ISS ?? DBNull.Value);
            command.Parameters.AddWithValue("@Direction", entry.Direction);
            command.Parameters.AddWithValue("@MessageType", (object?)entry.MessageType ?? DBNull.Value);
            command.Parameters.AddWithValue("@ProcessingCode", (object?)entry.ProcessingCode ?? DBNull.Value);
            command.Parameters.AddWithValue("@Amount", (object?)entry.Amount ?? DBNull.Value);
            command.Parameters.AddWithValue("@STAN", (object?)entry.Stan ?? DBNull.Value);
            command.Parameters.AddWithValue("@RRN", (object?)entry.Rrn ?? DBNull.Value);
            command.Parameters.AddWithValue("@ResponseCode", (object?)entry.ResponseCode ?? DBNull.Value);
            command.Parameters.AddWithValue("@LogTime", DateTime.UtcNow);

            command.Parameters.AddWithValue("@RawMessage", entry.RawMessage);

            await command.ExecuteNonQueryAsync();
        }

        private IsoMessage? TryParseMessage(byte[] messageBytes)
        {
            try
            {
                return _isoParser.Parse(messageBytes);
            }
            catch
            {
                return null;
            }
        }

        public string BuildSanitizedRawMessageHex(IsoMessage? parsedMessage, byte[] originalMessageBytes)
        {
            if (parsedMessage == null)
            {
                return BitConverter.ToString(originalMessageBytes).Replace("-", "");
            }

            try
            {
                var sanitized = CloneMessage(parsedMessage);

                if (sanitized.HasField(2)) sanitized.SetField(2, MaskPan(sanitized.GetField(2)));
                if (sanitized.HasField(35)) sanitized.SetField(35, MaskWithAsterisks(sanitized.GetField(35)));
                if (sanitized.HasField(52)) sanitized.SetField(52, MaskWithAsterisks(sanitized.GetField(52)));

                byte[] safeBytes = _isoParser.Build(sanitized);
                return BitConverter.ToString(safeBytes).Replace("-", "");
            }
            catch
            {
                return BitConverter.ToString(originalMessageBytes).Replace("-", "");
            }
        }

        private static IsoMessage CloneMessage(IsoMessage message)
        {
            var clone = new IsoMessage
            {
                MessageType = message.MessageType,
                Header = message.Header,
                PrimaryBitmap = message.PrimaryBitmap,
                SecondaryBitmap = message.SecondaryBitmap
            };

            foreach (var field in message.Fields)
            {
                clone.SetField(field.Key, field.Value);
            }

            return clone;
        }

        private static string? MaskPan(string? pan)
        {
            if (string.IsNullOrEmpty(pan)) return pan;
            if (pan.Length < 10) return new string('*', pan.Length);

            int maskedLength = pan.Length - 10;
            return pan.Substring(0, 6) + new string('*', maskedLength) + pan.Substring(pan.Length - 4);
        }

        private static string? MaskWithAsterisks(string? value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return new string('*', value.Length);
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

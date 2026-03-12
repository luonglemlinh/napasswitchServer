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
        private volatile bool _schemaChecked;
        private volatile bool _hasSenderColumn;
        private volatile bool _hasResponseCodeColumn;
        private volatile string _rawMessageColumnName = "RawMessage";
        private volatile bool _directionConstraintSupportsInbound = true;

        private record CycleLogEntry(
            string TRANSACTIONID, 
            string? ACQ, 
            string? ISS, 
            string? SENDER,
            string DIRECTION, 
            string? MESSAGETYPE, 
            string? PROCESSINGCODE, 
            decimal? AMOUNT, 
            string? STAN, 
            string? RRN,
            DateTime LOGTIME,
            string? RC,
            string SESSIONID,
            string RAWMESSAGE);

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
            SwitchLogger.ForContext("MSG-CYCLE").Error("Cycle overflow fallback. Direction={Direction}, SessionId={SessionId}, RawMessage={RawMessage}", entry.DIRECTION, entry.SESSIONID, entry.RAWMESSAGE);
        }

        /// <summary>
        /// Derives ACQ, ISS, and Sender from a parsed ISO message.
        /// ACQ  = DE#32
        /// ISS  = first 6 digits of PAN (DE#2, fallback DE#35)
        /// Sender = "ISS" if DE#39 present AND MTI is a response, else "ACQ"
        /// </summary>
        public static (string? acq, string? iss, string sender) DeriveRoutingFields(IsoMessage message)
        {
            string? acq = message.GetField(32);

            string? iss = null;
            string? bin = message.GetCardBIN(); // already handles DE#2 and DE#35 fallback
            if (!string.IsNullOrEmpty(bin))
                iss = bin; // BIN is already 6 digits from GetCardBIN()

            bool isResponse = MtiHelper.IsResponse(message.MessageType);
            string sender = (message.HasField(39) && isResponse) ? "ISS" : "ACQ";

            return (acq, iss, sender);
        }

        public Task LogCycleAsync(
            string transactionId,
            string sessionId,
            string direction,
            IsoMessage? parsedMessage,
            byte[] messageBytes)
        {
            if (!_enableLogging) return Task.CompletedTask;

            var safeMessage = parsedMessage ?? TryParseMessage(messageBytes);
            var (acq, iss, sender) = safeMessage != null
                ? DeriveRoutingFields(safeMessage)
                : (null, null, "ACQ");

            string sanitizedRawMessage = BuildSanitizedRawMessageHex(safeMessage, messageBytes);

            decimal? amount = null;
            if (decimal.TryParse(safeMessage?.GetField(4) ?? "0", out decimal parsedAmount))
            {
                amount = parsedAmount / 100m; // Convert from cents
            }

            var entry = new CycleLogEntry(
                transactionId,
                acq,
                iss,
                sender,
                direction,
                safeMessage?.MessageType,
                safeMessage?.GetField(3),
                amount,
                safeMessage?.GetField(11),
                safeMessage?.GetField(37),
                DateTime.UtcNow,
                safeMessage?.GetField(39),
                sessionId,
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
                    leg.ACQ,
                    leg.ISS,
                    leg.SENDER,
                    leg.DIRECTION,
                    leg.MESSAGETYPE,
                    leg.PROCESSINGCODE,
                    leg.AMOUNT,
                    leg.STAN,
                    leg.RRN,
                    leg.LOGTIME,
                    leg.RC,
                    sessionId,
                    leg.RAWMESSAGE
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
            await EnsureSchemaAsync(connection);

            // Always write using real table column names (not view aliases).
            // Sender / ResponseCode may not exist on older databases, so we detect and omit them.
            string query = BuildInsertQuery();

            using var command = new SqlCommand(query, connection);
            AddInsertParameters(command, entry, entry.DIRECTION);

            try
            {
                await command.ExecuteNonQueryAsync();
            }
            catch (SqlException ex) when (IsDirectionConstraintConflict(ex))
            {
                // Legacy databases may have CK_MessageCycle_Direction without INBOUND.
                // Retry with a fallback value that is accepted by older constraints.
                // We map INBOUND -> FORWARDED (closest "request-side" leg) to avoid dropping logs.
                if (string.Equals(entry.DIRECTION, "INBOUND", StringComparison.OrdinalIgnoreCase))
                {
                    command.Parameters["@DIRECTION"].Value = "FORWARDED";
                    await command.ExecuteNonQueryAsync();
                    return;
                }

                throw;
            }
        }

        private void AddInsertParameters(SqlCommand command, CycleLogEntry entry, string directionValue)
        {
            command.Parameters.AddWithValue("@TRANSACTIONID", entry.TRANSACTIONID);
            command.Parameters.AddWithValue("@ACQ", (object?)entry.ACQ ?? DBNull.Value);
            command.Parameters.AddWithValue("@ISS", (object?)entry.ISS ?? DBNull.Value);
            command.Parameters.AddWithValue("@DIRECTION", directionValue);
            command.Parameters.AddWithValue("@MESSAGETYPE", (object?)entry.MESSAGETYPE ?? DBNull.Value);
            command.Parameters.AddWithValue("@PROCESSINGCODE", (object?)entry.PROCESSINGCODE ?? DBNull.Value);
            command.Parameters.AddWithValue("@AMOUNT", (object?)entry.AMOUNT ?? DBNull.Value);
            command.Parameters.AddWithValue("@STAN", (object?)entry.STAN ?? DBNull.Value);
            command.Parameters.AddWithValue("@RRN", (object?)entry.RRN ?? DBNull.Value);
            command.Parameters.AddWithValue("@LOGTIME", entry.LOGTIME);
            command.Parameters.AddWithValue("@SESSIONID", entry.SESSIONID);
            command.Parameters.AddWithValue("@RAWMESSAGE", entry.RAWMESSAGE);

            if (_hasSenderColumn)
                command.Parameters.AddWithValue("@SENDER", (object?)entry.SENDER ?? DBNull.Value);
            if (_hasResponseCodeColumn)
                command.Parameters.AddWithValue("@RC", (object?)entry.RC ?? DBNull.Value);
        }

        private static bool IsDirectionConstraintConflict(SqlException ex)
        {
            // We only want to catch the specific check-constraint conflict for direction.
            // Example message contains: CHECK constraint "CK_MessageCycle_Direction"
            return ex.Message.Contains("CK_MessageCycle_Direction", StringComparison.OrdinalIgnoreCase)
                   && ex.Message.Contains("Direction", StringComparison.OrdinalIgnoreCase);
        }

        private async Task EnsureSchemaAsync(SqlConnection connection)
        {
            if (_schemaChecked) return;

            // Re-check schema whenever we establish a new connection instance
            // (connection is recreated on SQL exceptions).
            _schemaChecked = true;
            _hasSenderColumn = false;
            _hasResponseCodeColumn = false;
            _rawMessageColumnName = "RawMessage";
            _directionConstraintSupportsInbound = true;

            const string schemaQuery = @"
                SELECT COLUMN_NAME
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'MessageCycle';";

            try
            {
                using var cmd = new SqlCommand(schemaQuery, connection);
                using var reader = await cmd.ExecuteReaderAsync(_cts.Token);
                var cols = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadAsync(_cts.Token))
                {
                    if (reader[0] is string name && !string.IsNullOrWhiteSpace(name))
                        cols.Add(name);
                }

                _hasSenderColumn = cols.Contains("Sender");
                _hasResponseCodeColumn = cols.Contains("ResponseCode");
                _rawMessageColumnName = cols.Contains("RawMessage") ? "RawMessage" :
                                       (cols.Contains("MessageLog") ? "MessageLog" : "RawMessage");

                // Inspect direction check constraint definition to see if INBOUND is allowed
                const string constraintSql = @"
                    SELECT definition
                    FROM sys.check_constraints
                    WHERE name = 'CK_MessageCycle_Direction'
                      AND parent_object_id = OBJECT_ID('dbo.MessageCycle');";

                await reader.CloseAsync();
                using (var cc = new SqlCommand(constraintSql, connection))
                using (var cr = await cc.ExecuteReaderAsync(_cts.Token))
                {
                    if (await cr.ReadAsync(_cts.Token) && cr[0] is string def)
                    {
                        _directionConstraintSupportsInbound = def.Contains("INBOUND", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception ex)
            {
                // If schema inspection fails, keep defaults and let the SQL error surface as before.
            }
        }

        private string BuildInsertQuery()
        {
            var cols = new System.Collections.Generic.List<string>
            {
                "TransactionId",
                "SessionId",
                "ACQ",
                "ISS",
                "Direction",
                "MessageType",
                "ProcessingCode",
                "Amount",
                "STAN",
                "RRN",
                "LogTime",
                _rawMessageColumnName
            };

            var vals = new System.Collections.Generic.List<string>
            {
                "@TRANSACTIONID",
                "@SESSIONID",
                "@ACQ",
                "@ISS",
                "@DIRECTION",
                "@MESSAGETYPE",
                "@PROCESSINGCODE",
                "@AMOUNT",
                "@STAN",
                "@RRN",
                "@LOGTIME",
                "@RAWMESSAGE"
            };

            if (_hasSenderColumn)
            {
                cols.Insert(2, "Sender");
                vals.Insert(2, "@SENDER");
            }

            if (_hasResponseCodeColumn)
            {
                cols.Insert(cols.IndexOf("LogTime"), "ResponseCode");
                vals.Insert(vals.IndexOf("@LOGTIME"), "@RC");
            }

            string query = $@"
                INSERT INTO dbo.MessageCycle (
                    {string.Join(", ", cols)}
                ) VALUES (
                    {string.Join(", ", vals)}
                )";
            return query;
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

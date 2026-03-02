    using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using core.Models;
using core.Security;

namespace core.Helpers
{
    /// <summary>
    /// Utility class for logging detailed ISO-8583 message contents to a file.
    /// Uses Channel&lt;T&gt; for non-blocking, high-throughput logging.
    /// This helps keep the console output clean while preserving full message details for debugging.
    /// </summary>
    public static class MessageLogger
    {
        private static readonly string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        private static readonly string LogFilePath = Path.Combine(LogDir, "message_log.txt");
        private static readonly string NetworkLogFilePath = Path.Combine(LogDir, "network_message.txt");
        private static readonly string ConnectionLogFilePath = Path.Combine(LogDir, "h2h_connections.txt");

        // Channel for non-blocking log writes - unbounded to prevent blocking callers
        private static readonly Channel<LogEntry> _logChannel = Channel.CreateUnbounded<LogEntry>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        private static readonly Task _writerTask;
        private static readonly CancellationTokenSource _cts = new();

        private static DateTime _lastRotationCheck = DateTime.MinValue;
        private static bool _logDirExists = false;
        private static readonly TimeSpan RotationCheckInterval = TimeSpan.FromSeconds(5);

        // Log entry types
        private abstract record LogEntry;
        private sealed record RawLogEntry(string SessionId, string Hex, string Ascii) : LogEntry;
        private sealed record MessageLogEntry(string SessionId, string Direction, IsoMessage Message) : LogEntry;
        private sealed record ConnectionLogEntry(string Source, string Message) : LogEntry;

        static MessageLogger()
        {
            // Start the background writer task
            _writerTask = Task.Run(ProcessLogEntriesAsync);
        }

        private static async Task ProcessLogEntriesAsync()
        {
            var reader = _logChannel.Reader;

            try
            {
                await foreach (var entry in reader.ReadAllAsync(_cts.Token))
                {
                    try
                    {
                        EnsureInitialized();

                        switch (entry)
                        {
                            case RawLogEntry raw:
                                WriteRawLog(raw);
                                break;
                            case MessageLogEntry msg:
                                WriteMessageLog(msg);
                                break;
                            case ConnectionLogEntry conn:
                                WriteConnectionLog(conn);
                                break;
                        }
                    }
                    catch
                    {
                        // Ignore individual log write failures to prevent crash
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }

        private static void EnsureInitialized()
        {
            try
            {
                if (!_logDirExists)
                {
                    if (!Directory.Exists(LogDir))
                    {
                        Directory.CreateDirectory(LogDir);
                    }
                    _logDirExists = true;
                }

                // Throttle rotation checks to avoid hitting disk on every log write
                if (DateTime.UtcNow - _lastRotationCheck > RotationCheckInterval)
                {
                    RotateFile(LogFilePath);
                    RotateFile(NetworkLogFilePath);
                    RotateFile(ConnectionLogFilePath);
                    _lastRotationCheck = DateTime.UtcNow;
                }
            }
            catch { /* Ignore logging errors to prevent crash */ }
        }

        public static void LogConnectionEvent(string source, string message)
        {
            // Non-blocking enqueue
            _logChannel.Writer.TryWrite(new ConnectionLogEntry(source, message));
        }

        private static void WriteConnectionLog(ConnectionLogEntry entry)
        {
            using var writer = new StreamWriter(ConnectionLogFilePath, append: true);
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            writer.WriteLine($"{timestamp} [{entry.Source}] {entry.Message}");
        }

        private static void RotateFile(string path)
        {
            try 
            {
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if (info.Length > 10 * 1024 * 1024)
                    {
                        string fileName = Path.GetFileNameWithoutExtension(path);
                        string ext = Path.GetExtension(path);
                        string newPath = Path.Combine(LogDir, $"{fileName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}{ext}");
                        File.Move(path, newPath);
                    }
                }
            }
            catch { /* Best effort rotation */ }
        }

        public static void LogRaw(string sessionId, string hex, string ascii)
        {
            // Non-blocking enqueue
            _logChannel.Writer.TryWrite(new RawLogEntry(sessionId, hex, ascii));
        }

        private static void WriteRawLog(RawLogEntry entry)
        {
            using var writer = new StreamWriter(LogFilePath, append: true);
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            writer.WriteLine($"{timestamp} [{entry.SessionId}] Raw HEX: {entry.Hex}");
            writer.WriteLine($"{timestamp} [{entry.SessionId}] Raw ASCII: {entry.Ascii}");
        }

        public static void LogMessage(string sessionId, string direction, IsoMessage message)
        {
            // Non-blocking enqueue
            _logChannel.Writer.TryWrite(new MessageLogEntry(sessionId, direction, message));
        }

        private static void WriteMessageLog(MessageLogEntry entry)
        {
            var message = entry.Message;
            string targetPath = message.MessageType.StartsWith("08") ? NetworkLogFilePath : LogFilePath;

            using var writer = new StreamWriter(targetPath, append: true);
            string trn = message.GetTRN() ?? "N/A";
            string mti = message.MessageType;
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");

            writer.WriteLine($"{timestamp} <{trn}> [------------] {entry.Direction}:");
            writer.WriteLine($"\tType: {mti}");

            if (!string.IsNullOrEmpty(message.Header))
            {
                writer.WriteLine($"\t000:{message.Header}");
            }

            if (!string.IsNullOrEmpty(message.PrimaryBitmap))
            {
                string bitmap = message.PrimaryBitmap;
                if (!string.IsNullOrEmpty(message.SecondaryBitmap)) bitmap += message.SecondaryBitmap;
                writer.WriteLine($"\t001:{bitmap}");
            }

            foreach (var field in message.Fields.OrderBy(f => f.Key))
            {
                if (field.Key == 0 || field.Key == 1) continue; // Already handled

                int fNum = field.Key;
                string value = field.Value;

                // Mask sensitive fields for PCI-DSS compliance
                string maskedValue = MaskSensitiveField(fNum, value);

                writer.WriteLine($"\t{fNum:D3}:{maskedValue}");

                // Subfield logging (use masked value for sensitive fields)
                LogSubfields(writer, fNum, maskedValue);
            }
        }

        private static void LogSubfields(StreamWriter writer, int fNum, string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            switch (fNum)
            {
                case 2: // PAN
                    if (value.Length >= 2)
                        writer.WriteLine($"\t 2.01:{value.Substring(0, 2)}");
                    break;

                case 3: // Processing Code (6 digits: 00 00 00)
                    if (value.Length >= 2) writer.WriteLine($"\t 3.01:{value.Substring(0, 2)}");
                    if (value.Length >= 4) writer.WriteLine($"\t 3.02:{value.Substring(2, 2)}");
                    if (value.Length >= 6) writer.WriteLine($"\t 3.03:{value.Substring(4, 2)}");
                    break;

                case 22: // POS Entry Mode (3 digits: 07 0)
                    if (value.Length >= 2) writer.WriteLine($"\t 22.01:{value.Substring(0, 2)}");
                    if (value.Length >= 3) writer.WriteLine($"\t 22.02:{value.Substring(2, 1)}");
                    break;

                case 43: // Merchant Name/Loc (40 chars: 22 / 15 / 3)
                    if (value.Length >= 22) writer.WriteLine($"\t 43.01:{value.Substring(0, 22)}");
                    if (value.Length >= 37) writer.WriteLine($"\t 43.02:{value.Substring(22, 15)}");
                    if (value.Length >= 40) writer.WriteLine($"\t 43.03:{value.Substring(37, 3)}");
                    break;
            }
        }

        private static string FormatAmount(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return "0.00";
            if (decimal.TryParse(raw, out decimal val))
            {
                return (val / 100).ToString("N2");
            }
            return raw;
        }

        /// <summary>
        /// Masks sensitive field values for PCI-DSS compliance.
        /// DE#2 (PAN), DE#35 (Track 2), DE#52 (PIN Block) are masked.
        /// </summary>
        private static string MaskSensitiveField(int fieldNumber, string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            switch (fieldNumber)
            {
                case 2: // PAN - show first 6 and last 4 digits
                    if (value.Length >= 13)
                    {
                        int maskedLength = value.Length - 10;
                        return value.Substring(0, 6) + new string('*', maskedLength) + value.Substring(value.Length - 4);
                    }
                    return new string('*', value.Length);

                case 35: // Track 2 Data - mask everything except separator
                    int separatorIndex = value.IndexOf('=');
                    if (separatorIndex > 6 && value.Length > separatorIndex + 5)
                    {
                        // Show first 6, separator, last 4
                        return value.Substring(0, 6) + new string('*', separatorIndex - 6) + 
                               "=" + new string('*', value.Length - separatorIndex - 5) + 
                               value.Substring(value.Length - 4);
                    }
                    return new string('*', value.Length);

                case 52: // PIN Block - fully masked
                    return new string('*', value.Length);

                default:
                    return value;
            }
        }

        /// <summary>
        /// Flush pending log entries and shutdown the background writer.
        /// Call this during application shutdown.
        /// </summary>
        public static async Task ShutdownAsync()
        {
            _logChannel.Writer.Complete();
            await _writerTask;
        }
    }
}

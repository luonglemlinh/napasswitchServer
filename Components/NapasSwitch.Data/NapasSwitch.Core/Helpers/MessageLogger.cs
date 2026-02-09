    using System;
using System.IO;
using System.Linq;
using core.Models;
using core.Security;

namespace core.Helpers
{
    /// <summary>
    /// Utility class for logging detailed ISO-8583 message contents to a file.
    /// This helps keep the console output clean while preserving full message details for debugging.
    /// </summary>
    public static class MessageLogger
    {
        private static readonly string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        private static readonly string LogFilePath = Path.Combine(LogDir, "message_log.txt");
        private static readonly string NetworkLogFilePath = Path.Combine(LogDir, "network_message.txt");
        private static readonly string ConnectionLogFilePath = Path.Combine(LogDir, "h2h_connections.txt");
        private static readonly object _lock = new object();

        private static void EnsureInitialized()
        {
            try
            {
                if (!Directory.Exists(LogDir))
                {
                    Directory.CreateDirectory(LogDir);
                }

                RotateFile(LogFilePath);
                RotateFile(NetworkLogFilePath);
                RotateFile(ConnectionLogFilePath);
            }
            catch { /* Ignore logging errors to prevent crash */ }
        }

        public static void LogConnectionEvent(string source, string message)
        {
            try
            {
                lock (_lock)
                {
                    EnsureInitialized();
                    using (var writer = new StreamWriter(ConnectionLogFilePath, append: true))
                    {
                        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                        writer.WriteLine($"{timestamp} [{source}] {message}");
                    }
                }
            }
            catch { /* Ignore */ }
        }

        private static void RotateFile(string path)
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.Length > 10 * 1024 * 1024)
                {
                    string fileName = Path.GetFileNameWithoutExtension(path);
                    string ext = Path.GetExtension(path);
                    string newPath = Path.Combine(LogDir, $"{fileName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                    File.Move(path, newPath);
                }
            }
        }

        public static void LogRaw(string sessionId, string hex, string ascii)
        {
            try
            {
                lock (_lock)
                {
                    EnsureInitialized();
                    using (var writer = new StreamWriter(LogFilePath, append: true))
                    {
                        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                        writer.WriteLine($"{timestamp} [{sessionId}] Raw HEX: {hex}");
                        writer.WriteLine($"{timestamp} [{sessionId}] Raw ASCII: {ascii}");
                    }
                }
            }
            catch { }
        }

        public static void LogMessage(string sessionId, string direction, IsoMessage message)
        {
            try
            {
                lock (_lock)
                {
                    EnsureInitialized();
                    
                    string targetPath = message.MessageType.StartsWith("08") ? NetworkLogFilePath : LogFilePath;

                    using (var writer = new StreamWriter(targetPath, append: true))
                    {
                        string trn = message.GetTRN() ?? "N/A";
                        string mti = message.MessageType;
                        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

                        writer.WriteLine($"{timestamp} <{trn}> [------------] {direction}:");
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
                            
                            writer.WriteLine($"\t{fNum:D3}:{value}");

                            // Subfield logging
                            LogSubfields(writer, fNum, value);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to log message to file: {ex.Message}");
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
    }
}

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
        private static readonly string LogDir = @"c:\Users\admin\source\repos\napasswitchServer\Logs";
        private static readonly string LogFilePath = Path.Combine(LogDir, "message_log.txt");
        private static readonly object _lock = new object();

        static MessageLogger()
        {
            try
            {
                if (!Directory.Exists(LogDir))
                {
                    Directory.CreateDirectory(LogDir);
                }

                if (File.Exists(LogFilePath))
                {
                    // Basic rotation: if > 10MB, rename and start new
                    var info = new FileInfo(LogFilePath);
                    if (info.Length > 10 * 1024 * 1024)
                    {
                        File.Move(LogFilePath, Path.Combine(LogDir, $"message_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt"));
                    }
                }
            }
            catch { /* Ignore logging errors */ }
        }

        public static void LogMessage(string sessionId, string direction, IsoMessage message)
        {
            try
            {
                lock (_lock)
                {
                    using (var writer = new StreamWriter(LogFilePath, append: true))
                    {
                        string trn = message.GetTRN() ?? "N/A";
                        string mti = message.MessageType;
                        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

                        writer.WriteLine();
                        writer.WriteLine("┌────────────────────────────────────────────────────────────────────────────┐");
                        writer.WriteLine($"│ {direction,-12} | MTI: {mti} | TRN: {trn,-16} | {timestamp} │");
                        writer.WriteLine("└────────────────────────────────────────────────────────────────────────────┘");
                        writer.WriteLine($" Session ID: {sessionId}");
                        if (!string.IsNullOrEmpty(message.Header)) writer.WriteLine($" Header:     {message.Header}");

                        // 1. Transaction Identifiers
                        writer.WriteLine("\n === [ TRANSACTION IDENTIFIERS ] ===");
                        LogField(writer, message, 11, "STAN");
                        LogField(writer, message, 37, "RRN");
                        LogField(writer, message, 63, "TRN");
                        LogField(writer, message, 38, "Auth ID");
                        LogField(writer, message, 7,  "Transmission Time");

                        // 2. Card Data
                        writer.WriteLine("\n === [ CARD DATA ] ===");
                        LogField(writer, message, 2,  "PAN", SecureDataHandler.MaskPAN(message.GetField(2)));
                        LogField(writer, message, 14, "Expiration");
                        LogField(writer, message, 22, "Entry Mode");
                        if (message.HasField(35)) writer.WriteLine("  035 (Track 2):        [REDACTED - SECURITY POLICY]");

                        // 3. Amounts & Currency
                        writer.WriteLine("\n === [ AMOUNTS ] ===");
                        LogField(writer, message, 4,  "Amount", FormatAmount(message.GetField(4)));
                        LogField(writer, message, 49, "Currency");
                        LogField(writer, message, 54, "Additional Amounts");

                        // 4. Routing
                        writer.WriteLine("\n === [ ROUTING ] ===");
                        LogField(writer, message, 32, "Acquirer ID");
                        LogField(writer, message, 33, "Forwarding ID");
                        LogField(writer, message, 100, "Receiving ID");

                        // 5. Merchant & Terminal
                        writer.WriteLine("\n === [ MERCHANT / TERMINAL ] ===");
                        LogField(writer, message, 41, "Terminal ID");
                        LogField(writer, message, 42, "Merchant ID");
                        LogField(writer, message, 43, "Merchant Name/Loc");
                        LogField(writer, message, 18, "Merchant Type (MCC)");

                        // 6. Response Data (only for responses)
                        if (mti.EndsWith("10") || mti.EndsWith("30") || mti.EndsWith("10") || mti.EndsWith("21") || mti.EndsWith("30"))
                        {
                            writer.WriteLine("\n === [ RESPONSE DATA ] ===");
                            LogField(writer, message, 39, "Response Code");
                            LogField(writer, message, 102, "Account ID 1");
                            LogField(writer, message, 103, "Account ID 2");
                        }

                        // 7. Other Fields
                        var loggedFields = new int[] { 2, 4, 7, 11, 14, 18, 22, 32, 33, 35, 37, 38, 39, 41, 42, 43, 49, 54, 63, 100, 102, 103 };
                        var otherFields = message.Fields.Keys.Where(k => !loggedFields.Contains(k)).OrderBy(k => k).ToList();
                        
                        if (otherFields.Any())
                        {
                            writer.WriteLine("\n === [ OTHER FIELDS ] ===");
                            foreach (var fNum in otherFields)
                            {
                                LogField(writer, message, fNum, IsoMessage.GetFieldDescription(fNum));
                            }
                        }

                        writer.WriteLine("\n" + new string('-', 80));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to log message to file: {ex.Message}");
            }
        }

        private static void LogField(StreamWriter writer, IsoMessage message, int fNum, string label, string? overrideValue = null)
        {
            if (message.HasField(fNum))
            {
                string value = overrideValue ?? message.GetField(fNum)!;
                writer.WriteLine($"  {fNum:D3} ({label,-18}): {value}");
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

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using core.Models;

namespace data
{
    
    /// Fallback transaction logger that writes to local file when database is unavailable
    /// Transactions are queued for later database insertion when connectivity is restored
    
    public class FallbackTransactionLogger : IDisposable
    {
        private readonly string _fallbackFilePath;
        private readonly ConcurrentQueue<FallbackLogEntry> _pendingEntries;
        private readonly object _fileLock = new object();
        private readonly Timer _flushTimer;
        private bool _disposed;

        public int PendingCount => _pendingEntries.Count;

        public FallbackTransactionLogger(string? fallbackDirectory = null)
        {
            var directory = fallbackDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FallbackLogs");
            
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            _fallbackFilePath = Path.Combine(directory, $"transactions_{DateTime.Now:yyyyMMdd}.log");
            _pendingEntries = new ConcurrentQueue<FallbackLogEntry>();
            
            // Flush to file every 5 seconds
            _flushTimer = new Timer(FlushToFile, null, 5000, 5000);
        }

        
        /// Log a transaction to the fallback file
        
        public void LogTransaction(IsoMessage request, IsoMessage? response, string sessionId, int processingTimeMs, string direction)
        {
            var entry = new FallbackLogEntry
            {
                Timestamp = DateTime.UtcNow,
                SessionId = sessionId,
                MessageType = request.MessageType,
                PAN = MaskPAN(request.GetPAN()),
                ProcessingCode = request.GetProcessingCode(),
                Amount = request.GetAmount(),
                STAN = request.GetSTAN(),
                ResponseCode = response?.GetResponseCode(),
                ProcessingTimeMs = processingTimeMs,
                Direction = direction
            };

            _pendingEntries.Enqueue(entry);
            Console.WriteLine($"[FALLBACK-LOG] Transaction {sessionId} queued for fallback logging");
        }

        private void FlushToFile(object? state)
        {
            if (_pendingEntries.IsEmpty) return;

            var entriesToWrite = new System.Collections.Generic.List<FallbackLogEntry>();
            
            while (_pendingEntries.TryDequeue(out var entry))
            {
                entriesToWrite.Add(entry);
            }

            if (entriesToWrite.Count == 0) return;

            try
            {
                lock (_fileLock)
                {
                    using var writer = new StreamWriter(_fallbackFilePath, append: true);
                    foreach (var entry in entriesToWrite)
                    {
                        writer.WriteLine(entry.ToLogLine());
                    }
                }
                Console.WriteLine($"[FALLBACK-LOG] Flushed {entriesToWrite.Count} entries to file");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FALLBACK-LOG] ERROR: Failed to flush to file: {ex.Message}");
                // Re-queue failed entries
                foreach (var entry in entriesToWrite)
                {
                    _pendingEntries.Enqueue(entry);
                }
            }
        }

        
        /// Get all pending entries for database insertion
        
        public FallbackLogEntry[] GetPendingEntries()
        {
            return _pendingEntries.ToArray();
        }

        
        /// Try to recover pending entries and insert them to database
        
        public async Task<int> RecoverToDatabase(TransactionLogger dbLogger)
        {
            int recovered = 0;
            
            while (_pendingEntries.TryDequeue(out var entry))
            {
                try
                {
                    // Note: This would need a method in TransactionLogger to accept raw data
                    // For now, just log that we would recover
                    Console.WriteLine($"[FALLBACK-LOG] Would recover entry {entry.SessionId} to database");
                    recovered++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FALLBACK-LOG] Failed to recover entry: {ex.Message}");
                    _pendingEntries.Enqueue(entry); // Re-queue on failure
                    break;
                }
            }

            return recovered;
        }

        private string MaskPAN(string? pan)
        {
            if (string.IsNullOrEmpty(pan) || pan.Length < 10)
                return "****";

            return $"{pan[..6]}****{pan[^4..]}";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _flushTimer?.Dispose();
            FlushToFile(null); // Final flush
        }
    }

    public class FallbackLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string? MessageType { get; set; }
        public string? PAN { get; set; }
        public string? ProcessingCode { get; set; }
        public string? Amount { get; set; }
        public string? STAN { get; set; }
        public string? ResponseCode { get; set; }
        public int ProcessingTimeMs { get; set; }
        public string Direction { get; set; } = string.Empty;

        public string ToLogLine()
        {
            return $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff}|{SessionId}|{MessageType}|{PAN}|{ProcessingCode}|{Amount}|{STAN}|{ResponseCode}|{ProcessingTimeMs}|{Direction}";
        }

        public static FallbackLogEntry? FromLogLine(string line)
        {
            try
            {
                var parts = line.Split('|');
                if (parts.Length < 10) return null;

                return new FallbackLogEntry
                {
                    Timestamp = DateTime.Parse(parts[0]),
                    SessionId = parts[1],
                    MessageType = parts[2],
                    PAN = parts[3],
                    ProcessingCode = parts[4],
                    Amount = parts[5],
                    STAN = parts[6],
                    ResponseCode = parts[7],
                    ProcessingTimeMs = int.Parse(parts[8]),
                    Direction = parts[9]
                };
            }
            catch
            {
                return null;
            }
        }
    }
}

using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using System.IO;

namespace core.Helpers
{
    /// <summary>
    /// Centralized structured logging facade for the NAPAS Switch.
    /// Replaces raw Console.WriteLine with Serilog structured logging.
    /// All projects reference this via NapasSwitch.Data.
    ///
    /// Production outputs:
    ///   Console  → human-readable (for operators)
    ///   .log     → human-readable rolling file (for quick grep)
    ///   .json    → compact JSON rolling file (for ELK/Seq/Splunk ingestion)
    /// </summary>
    public static class SwitchLogger
    {
        private static ILogger _logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();

        private static bool _initialized;

        /// <summary>
        /// Initialize Serilog with console + rolling file sinks.
        /// Call once at startup from Program.Main before any logging.
        /// </summary>
        public static void Initialize(string? logDirectory = null)
        {
            if (_initialized) return;

            string logDir = logDirectory
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);

            string textLogPath = Path.Combine(logDir, "switch-.log");
            string jsonLogPath = Path.Combine(logDir, "switch-.json");

            _logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                // Console: human-readable for operators
                .WriteTo.Console(
                    outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                // Text file: human-readable for quick grep/tail
                .WriteTo.File(
                    textLogPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    fileSizeLimitBytes: 50 * 1024 * 1024,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                // JSON file: machine-parseable for ELK/Seq/Splunk
                .WriteTo.File(
                    new CompactJsonFormatter(),
                    jsonLogPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    fileSizeLimitBytes: 50 * 1024 * 1024)
                .CreateLogger();

            _initialized = true;
            _logger.Information("Structured logging initialized. LogDir={LogDir}", Path.GetFileName(logDir));
        }

        // ---- Thin wrappers that mirror Console.WriteLine patterns ----

        public static void Debug(string messageTemplate, params object[] args)
            => _logger.Debug(messageTemplate, args);

        public static void Info(string messageTemplate, params object[] args)
            => _logger.Information(messageTemplate, args);

        public static void Warn(string messageTemplate, params object[] args)
            => _logger.Warning(messageTemplate, args);

        public static void Error(string messageTemplate, params object[] args)
            => _logger.Error(messageTemplate, args);

        public static void Error(Exception ex, string messageTemplate, params object[] args)
            => _logger.Error(ex, messageTemplate, args);

        public static void Fatal(string messageTemplate, params object[] args)
            => _logger.Fatal(messageTemplate, args);

        public static void Fatal(Exception ex, string messageTemplate, params object[] args)
            => _logger.Fatal(ex, messageTemplate, args);

        /// <summary>
        /// Flush and close the logger. Call at shutdown.
        /// </summary>
        public static void CloseAndFlush()
        {
            if (_logger is IDisposable disposable)
                disposable.Dispose();
        }
    }
}

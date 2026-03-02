using Serilog;
using Serilog.Context;
using Serilog.Events;
using Serilog.Formatting.Compact;
using System;
using System.IO;

namespace core.Helpers
{
    /// <summary>
    /// Centralized structured logging facade for the NAPAS Switch.
    ///
    /// Every log line answers three questions without reading the message body:
    ///   1. Severity  - from Serilog level (INF/WRN/ERR/FTL)
    ///   2. Subsystem - from the Category property (INIT, HSM, H2H, ROUTING, etc.)
    ///   3. Context   - from SessionId/TransactionId passed as structured parameters
    ///
    /// Category is a first-class structured field, queryable in Seq/Splunk/Elastic
    /// without regex. Use ForContext() to create a scoped logger per subsystem.
    ///
    /// Production outputs:
    ///   Console  - human-readable (for operators)
    ///   .log     - human-readable rolling file (for quick grep)
    ///   .json    - compact JSON rolling file (for ELK/Seq/Splunk ingestion)
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
                // Category appears as a clean column between level and message
                .WriteTo.Console(
                    outputTemplate: "{Timestamp:HH:mm:ss.fff} {Level:u3} {Category,-12:lj} {Message:lj}{NewLine}{Exception}")
                // Text file: human-readable for quick grep/tail
                .WriteTo.File(
                    textLogPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    fileSizeLimitBytes: 50 * 1024 * 1024,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3} {Category,-12:lj} {Message:lj}{NewLine}{Exception}")
                // JSON file: machine-parseable for ELK/Seq/Splunk
                // Category is automatically included as a structured property
                .WriteTo.File(
                    new CompactJsonFormatter(),
                    jsonLogPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    fileSizeLimitBytes: 50 * 1024 * 1024)
                .CreateLogger();

            _initialized = true;
            _logger
                .ForContext("Category", "SYSTEM")
                .Information("Structured logging initialized. LogDir={LogDir}", Path.GetFileName(logDir));
        }

        // ---- Scoped logger for subsystem tagging ----

        /// <summary>
        /// Create a scoped logger with a Category property.
        /// Usage: SwitchLogger.ForContext("HSM").Info("Initialized with {KeyCount} keys", count);
        /// The category becomes a queryable structured field in JSON logs.
        /// </summary>
        public static CategoryLogger ForContext(string category) => new(category);

        // ---- Convenience methods (no category - use ForContext for new code) ----

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

        /// <summary>
        /// Lightweight scoped logger that attaches a Category property to every log call.
        /// Struct to avoid heap allocation on every ForContext() call.
        /// </summary>
        public readonly struct CategoryLogger
        {
            private readonly string _category;
            internal CategoryLogger(string category) => _category = category;

            private ILogger Scoped => _logger.ForContext("Category", _category);

            public void Debug(string messageTemplate, params object[] args)
                => Scoped.Debug(messageTemplate, args);

            public void Info(string messageTemplate, params object[] args)
                => Scoped.Information(messageTemplate, args);

            public void Warn(string messageTemplate, params object[] args)
                => Scoped.Warning(messageTemplate, args);

            public void Error(string messageTemplate, params object[] args)
                => Scoped.Error(messageTemplate, args);

            public void Error(Exception ex, string messageTemplate, params object[] args)
                => Scoped.Error(ex, messageTemplate, args);
        }
    }
}

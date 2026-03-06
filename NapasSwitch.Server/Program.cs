using System;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using core.Configuration;
using core.Helpers;
using core.Security;

namespace server
{
    class Program
    {
        static async Task Main(string[] args)
        {
            bool headless = args.Contains("--headless")
                || Environment.GetEnvironmentVariable("NAPAS_HEADLESS") == "true";

            if (!headless)
                Console.Title = "NAPAS Payment Switch Server";

            SwitchLogger.Initialize();

            Console.WriteLine(@"
╔════════════════════════════════════════════════════════════╗
║                      Switch                                ║
║                    Version 1.0                             ║
╚════════════════════════════════════════════════════════════╝
");

            try
            {
                // Step 1: Load configurations
                SwitchLogger.Info("Initializing configuration...");

                // Try to find Config directory
                string? configPath = FindConfigDirectory();

                if (configPath == null)
                {
                    SwitchLogger.Error("Config directory not found. Please create a 'Config' folder at the solution root with: BINconfig.xml, ACQconfig.xml, RCconfig.xml, DBconfig.xml");
                    if (!headless) { Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); }
                    return;
                }
                
                ConfigurationLoader.Instance.LoadConfigurations(configPath);

                // Step 2: Get database configuration
                var dbConfig = ConfigurationLoader.Instance.DatabaseConfig;
                string dbConnectionString = dbConfig.ConnectionString;
                bool enableLogging = dbConfig.EnableLogging;

                // Step 3: Test database connection if logging is enabled
                if (enableLogging)
                {
                    if (TestDatabaseConnection(dbConnectionString))
                    {
                        SwitchLogger.Info("Database connection verified (logging: ENABLED)");
                    }
                    else
                    {
                        SwitchLogger.Warn("Database connection failed");

                        if (headless)
                        {
                            SwitchLogger.Warn("Headless mode: continuing without database logging");
                            enableLogging = false;
                            dbConnectionString = "";
                        }
                        else
                        {
                            Console.WriteLine("Press 'C' to continue without logging, or any other key to exit");
                            var key = Console.ReadKey();
                            Console.WriteLine();

                            if (key.Key != ConsoleKey.C)
                            {
                                SwitchLogger.Info("Startup cancelled");
                                return;
                            }

                            enableLogging = false;
                            dbConnectionString = "";
                            SwitchLogger.Info("Continuing without database logging");
                        }
                    }
                }
                else
                {
                    SwitchLogger.Info("Database logging: DISABLED");
                }

                // Step 4: Resolve HSM provider
                IHsmProvider? hsmProvider = null;
                string? allowStub = Environment.GetEnvironmentVariable("ALLOW_HSM_STUB");
                if (string.Equals(allowStub, "true", StringComparison.OrdinalIgnoreCase))
                {
                    hsmProvider = new SoftwareHsmStub();
                }
                else if (headless)
                {
                    hsmProvider = new SoftwareHsmStub();
                    SwitchLogger.ForContext("SECURITY").Warn("Headless mode: using SoftwareHsmStub. NOT FOR PRODUCTION!");
                }
                else
                {
                    SwitchLogger.Warn("No HSM provider configured and ALLOW_HSM_STUB is not set.");
                    Console.WriteLine("No HSM provider. Press 'C' to continue with software stub, or any other key to exit");

                    var hsmKey = Console.ReadKey();
                    Console.WriteLine();

                    if (hsmKey.Key != ConsoleKey.C)
                    {
                        SwitchLogger.Info("Startup cancelled — no HSM provider");
                        return;
                    }

                    Environment.SetEnvironmentVariable("ALLOW_HSM_STUB", "true");
                    hsmProvider = new SoftwareHsmStub();
                    SwitchLogger.ForContext("SECURITY").Warn("Using SoftwareHsmStub for this session. NOT FOR PRODUCTION!");
                }

                // Step 5: Create and start the server
                // Load ports from ServerConfig.xml instead of hardcoding
                var serverConfig = ConfigurationLoader.Instance.ServerConfig;
                int[] ports = serverConfig.GetAllPorts();

                using var server = new TcpSwitchServer(ports, dbConnectionString, enableLogging, hsmProvider);

                if (!headless)
                {
                    Console.WriteLine("\nPress ENTER to start the server, or 'Q' to quit...");
                    var startKey = Console.ReadKey();
                    Console.WriteLine();

                    if (startKey.Key == ConsoleKey.Q)
                    {
                        SwitchLogger.Info("Server startup cancelled");
                        return;
                    }
                }

                // Step 6: Connect to TS (persistent connection)
                SwitchLogger.Info("Connecting to Transaction Switch...");
                await server.ConnectToTSAsync();

                Console.WriteLine();
                server.Start();

                if (headless)
                {
                    var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
                    try { await Task.Delay(Timeout.Infinite, cts.Token); }
                    catch (TaskCanceledException) { }
                    SwitchLogger.Info("Shutdown signal received");
                    server.Stop();
                }
                else
                {
                    Console.WriteLine(" Commands: [S] Show connections  [P] Pause/Resume  [Q] Quit");
                    Console.WriteLine();

                    bool running = true;
                    bool paused = false;
                    while (running)
                    {
                        if (Console.KeyAvailable)
                        {
                            var cmd = Console.ReadKey(intercept: true);
                            switch (cmd.Key)
                            {
                                case ConsoleKey.S:
                                    server.PrintActiveConnections();
                                    break;
                                case ConsoleKey.P:
                                    if (!paused)
                                    {
                                        server.Stop();
                                        paused = true;
                                        SwitchLogger.Info("Server paused — press [P] to resume");
                                        Console.WriteLine(" ** SERVER PAUSED — press [P] to resume, [Q] to quit **");
                                    }
                                    else
                                    {
                                        server.Start();
                                        paused = false;
                                        SwitchLogger.Info("Server resumed");
                                    }
                                    break;
                                case ConsoleKey.Q:
                                    SwitchLogger.Info("Shutting down server...");
                                    if (!paused) server.Stop();
                                    running = false;
                                    break;
                            }
                        }
                        else
                        {
                            await Task.Delay(100);
                        }
                    }
                }
            }
            catch (FileNotFoundException ex)
            {
                SwitchLogger.Error(ex, "Configuration file not found: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                SwitchLogger.Fatal(ex, "Unexpected error: {Message}", ex.Message);
            }
            finally
            {
                SwitchLogger.CloseAndFlush();
                if (!headless)
                {
                    Console.WriteLine("\nPress any key to exit...");
                    Console.ReadKey();
                }
            }
        }

        private static string? FindConfigDirectory()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string? current = baseDir;
            
            for (int i = 0; i < 6; i++)
            {
                if (current == null) break;
                
                string testPath = Path.Combine(current, "Config");
                if (Directory.Exists(testPath))
                {
                    return testPath;
                }
                
                current = Directory.GetParent(current)?.FullName;
            }
            
            return null;
        }

        static bool TestDatabaseConnection(string connectionString)
        {
            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    
                    // Check if TransactionLog table exists
                    var cmd = new SqlCommand(
                        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TransactionLog'", 
                        connection);
                    
                    int tableCount = (int)cmd.ExecuteScalar();

                    if (tableCount == 0)
                    {
                        SwitchLogger.Error("TransactionLog table does not exist. Please run the iso.sql script first");
                        return false;
                    }

                    // Drop orphaned objects from prior schema versions that cause runtime errors.
                    // The UpdatedAt trigger fires on every UPDATE but the column no longer exists.
                    try
                    {
                        using var cleanupCmd = new SqlCommand(@"
                            IF EXISTS (SELECT * FROM sys.triggers WHERE name = 'TR_UnsettledTransactions_UpdatedAt')
                                DROP TRIGGER TR_UnsettledTransactions_UpdatedAt;
                            IF EXISTS (SELECT * FROM sys.triggers WHERE name = 'TR_PendingTransactions_UpdatedAt')
                                DROP TRIGGER TR_PendingTransactions_UpdatedAt;", connection);
                        cleanupCmd.ExecuteNonQuery();
                    }
                    catch { /* Best effort — schema script will handle it on next run */ }

                    return true;
                }
            }
            catch (SqlException ex)
            {
                SwitchLogger.Error(ex, "SQL Error: {Message}. Connection: {ConnStr}", ex.Message, MaskConnectionString(connectionString));
                return false;
            }
            catch (Exception ex)
            {
                SwitchLogger.Error(ex, "Connection test failed: {Message}", ex.Message);
                return false;
            }
        }

        static string MaskConnectionString(string connectionString)
        {
            // Hide password in connection string for security
            if (connectionString.Contains("Password="))
            {
                int pwdIndex = connectionString.IndexOf("Password=");
                int endIndex = connectionString.IndexOf(";", pwdIndex);
                if (endIndex == -1) endIndex = connectionString.Length;
                
                string beforePwd = connectionString.Substring(0, pwdIndex);
                string afterPwd = endIndex < connectionString.Length ? connectionString.Substring(endIndex) : "";
                
                return beforePwd + "Password=*****" + afterPwd;
            }
            return connectionString;
        }
    }
}
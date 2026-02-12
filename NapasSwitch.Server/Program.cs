using System;
using System.Data.SqlClient;
using System.IO;
using core.Configuration;
using core.Helpers;

namespace server
{
    class Program
    {
        static async System.Threading.Tasks.Task Main(string[] args)
        {
            Console.Title = "NAPAS Payment Switch Server";
            SwitchLogger.Initialize();

            Console.WriteLine(@"
╔════════════════════════════════════════════════════════════╗
║                                                            ║
║             NAPAS ISO-8583 Payment Switch                  ║
║                    Version 1.0                             ║
║                                                            ║
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
                    Console.WriteLine("\n[INFO] Press any key to exit...");
                    Console.ReadKey();
                    return;
                }
                
                ConfigurationLoader.Instance.LoadConfigurations(configPath);

                // Step 2: Get database configuration
                var dbConfig = ConfigurationLoader.Instance.DatabaseConfig;
                string dbConnectionString = dbConfig.ConnectionString;
                bool enableLogging = dbConfig.EnableLogging;

                SwitchLogger.Info("Database logging: {EnableLogging}", enableLogging ? "ENABLED" : "DISABLED");

                // Step 3: Test database connection if logging is enabled
                if (enableLogging)
                {
                    SwitchLogger.Info("Testing database connection...");

                    if (TestDatabaseConnection(dbConnectionString))
                    {
                        SwitchLogger.Info("Database connection successful");
                    }
                    else
                    {
                        SwitchLogger.Warn("Database connection failed");
                        Console.WriteLine("[WARNING] Press 'C' to continue without logging, or any other key to exit");
                        
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

                // Step 4: Create and start the server
                // Load ports from ServerConfig.xml instead of hardcoding
                var serverConfig = ConfigurationLoader.Instance.ServerConfig;
                int[] ports = serverConfig.GetAllPorts();
                
                SwitchLogger.Info("Listening on {PortCount} ports: {Ports}", ports.Length, string.Join(", ", ports));
                using var server = new TcpSwitchServer(ports, dbConnectionString, enableLogging);

                Console.WriteLine("\n[READY] Press ENTER to start the server, or 'Q' to quit...");
                var startKey = Console.ReadKey();
                Console.WriteLine();
                
                if (startKey.Key == ConsoleKey.Q)
                {
                    SwitchLogger.Info("Server startup cancelled");
                    return;
                }

                // Step 5: Connect to TS (persistent connection)
                SwitchLogger.Info("Connecting to Transaction Switch...");
                await server.ConnectToTSAsync(); // Use await, don't block with .Wait()

                Console.WriteLine();

                // Start server
                server.Start();

                // Console command loop
                Console.WriteLine(" Commands: [S] Show connections  [Q] Quit");
                Console.WriteLine();

                bool running = true;
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
                            case ConsoleKey.Q:
                                SwitchLogger.Info("Shutting down server...");
                                server.Stop();
                                running = false;
                                break;
                        }
                    }
                    else
                    {
                        await System.Threading.Tasks.Task.Delay(100);
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
                Console.WriteLine("\n[INFO] Press any key to exit...");
                Console.ReadKey();
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

                    SwitchLogger.Info($"[DB-TEST] Connection successful, TransactionLog table verified");
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
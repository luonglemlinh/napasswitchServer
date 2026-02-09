using System;
using System.Data.SqlClient;
using System.IO;
using core.Configuration;

namespace server
{
    class Program
    {
        static async System.Threading.Tasks.Task Main(string[] args)
        {
            Console.Title = "NAPAS Payment Switch Server";
            
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
                Console.WriteLine("[INIT] Initializing configuration...");
                
                // Try to find Config directory
                string? configPath = FindConfigDirectory();
                
                if (configPath == null)
                {
                    Console.WriteLine("[ERROR] Config directory not found.");
                    Console.WriteLine("[ERROR] Please create a 'Config' folder at the solution root with:");
                    Console.WriteLine("        - BINconfig.xml ");
                    Console.WriteLine("        - ACQconfig.xml ");
                    Console.WriteLine("        - RCconfig.xml");
                    Console.WriteLine("        - DBconfig.xml");
                    Console.WriteLine("\n[INFO] Press any key to exit...");
                    Console.ReadKey();
                    return;
                }
                
                ConfigurationLoader.Instance.LoadConfigurations(configPath);

                // Step 2: Get database configuration
                var dbConfig = ConfigurationLoader.Instance.DatabaseConfig;
                string dbConnectionString = dbConfig.ConnectionString;
                bool enableLogging = dbConfig.EnableLogging;

                Console.WriteLine($"[INIT] Database logging: {(enableLogging ? "ENABLED" : "DISABLED")}");

                // Step 3: Test database connection if logging is enabled
                if (enableLogging)
                {
                    Console.WriteLine("[INIT] Testing database connection...");
                    
                    if (TestDatabaseConnection(dbConnectionString))
                    {
                        Console.WriteLine("[INIT] Database connection successful");
                    }
                    else
                    {
                        Console.WriteLine("[WARNING] Database connection failed");
                        Console.WriteLine("[WARNING] Press 'C' to continue without logging, or any other key to exit");
                        
                        var key = Console.ReadKey();
                        Console.WriteLine();
                        
                        if (key.Key != ConsoleKey.C)
                        {
                            Console.WriteLine("[INFO] Startup cancelled");
                            return;
                        }
                        
                        enableLogging = false;
                        dbConnectionString = "";
                        Console.WriteLine("[INFO] Continuing without database logging");
                    }
                }

                // Step 4: Create and start the server
                int[] ports = { 1111, 2222, 3333, 1177 };
                var server = new TcpSwitchServer(ports, dbConnectionString, enableLogging);

                Console.WriteLine("\n[READY] Press ENTER to start the server, or 'Q' to quit...");
                var startKey = Console.ReadKey();
                Console.WriteLine();
                
                if (startKey.Key == ConsoleKey.Q)
                {
                    Console.WriteLine("[INFO] Server startup cancelled");
                    return;
                }

                // Step 5: Connect to TS (persistent connection)
                Console.WriteLine("\n[TS] Connecting to Transaction Switch...");
                await server.ConnectToTSAsync(); // Use await, don't block with .Wait()

                Console.WriteLine();

                // Start server (this blocks)
                server.Start();
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine($"\n[ERROR] Configuration file not found: {ex.Message}");
                Console.WriteLine("[ERROR] Please ensure Config folder exists at solution root with:");
                Console.WriteLine("        - BINconfig.xml");
                Console.WriteLine("        - ACQconfig.xml");
                Console.WriteLine("        - RCconfig.xml");
                Console.WriteLine("        - DBconfig.xml");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[FATAL] Unexpected error: {ex.Message}");
                Console.WriteLine($"[FATAL] Stack trace: {ex.StackTrace}");
            }
            finally
            {
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
                        Console.WriteLine("[ERROR] TransactionLog table does not exist");
                        Console.WriteLine("[ERROR] Please run the iso.sql script first");
                        return false;
                    }
                    
                    Console.WriteLine("[DB-TEST] Connection successful");
                    Console.WriteLine("[DB-TEST] TransactionLog table verified");
                    return true;
                }
            }
            catch (SqlException ex)
            {
                Console.WriteLine($"[ERROR] SQL Error: {ex.Message}");
                Console.WriteLine($"[ERROR] Connection String: {MaskConnectionString(connectionString)}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Connection test failed: {ex.Message}");
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
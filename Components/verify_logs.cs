
using System;
using System.IO;
using core.Configuration;
using core.Helpers;

namespace Verification
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("--- Logging Directory Verification ---");

            // 1. Check default value from ServerConfig.xml (which I just changed to Linux path)
            // Note: On Windows, Path.Combine with a Linux path like /home/... usually returns the Linux path itself if rooted,
            // but File.Exists might fail if it's not a valid Windows path.
            
            try 
            {
                // We need to bypass the "FindConfigDirectory" logic or ensure it works.
                // For this test, we'll just point to the actual Config dir.
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../Config");
                configPath = Path.GetFullPath(configPath);
                
                Console.WriteLine($"Config Path: {configPath}");
                
                // Test 1: No env var
                Environment.SetEnvironmentVariable("NAPAS_LOG_DIRECTORY", null);
                ConfigurationLoader.Instance.LoadConfigurations(configPath);
                string logDir = ConfigurationLoader.Instance.ServerConfig.Settings.LogDirectory;
                Console.WriteLine($"Default LogDir (from XML): {logDir}");
                
                if (logDir == "/home/azureuser/napasswitch/app_logs")
                {
                    Console.WriteLine("SUCCESS: Default LogDir matches expected Linux path.");
                }
                else
                {
                    Console.WriteLine("FAILURE: Default LogDir does NOT match.");
                }

                // Test 2: Env var override
                string testPath = "C:\\Temp\\NapasLogsTest";
                Environment.SetEnvironmentVariable("NAPAS_LOG_DIRECTORY", testPath);
                
                // Reload configs to see the override
                ConfigurationLoader.Instance.LoadConfigurations(configPath);
                logDir = ConfigurationLoader.Instance.ServerConfig.Settings.LogDirectory;
                Console.WriteLine($"Overridden LogDir: {logDir}");
                
                if (logDir == testPath)
                {
                    Console.WriteLine("SUCCESS: LogDir successfully overridden by environment variable.");
                }
                else
                {
                    Console.WriteLine("FAILURE  : LogDir override failed.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR during verification: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using core.Models.Configuration;

namespace core.Configuration
{
    /// <summary>
    /// Loads and manages all XML configuration files for the switch
    /// This is a Singleton - only one instance exists throughout the application
    /// </summary>
    public class ConfigurationLoader
    {
        private static ConfigurationLoader? _instance;
        private static readonly object _lock = new object();

        // Cached configurations
        private BinRoutingConfiguration? _binRouting;
        private AcquirerRoutingConfiguration? _acquirerRouting;
        private ResponseCodeConfiguration? _responseCodes;
        private DatabaseConfiguration? _databaseConfig;

        // Quick lookup dictionaries (for performance!)
        private Dictionary<string, IssuerBankConfig> _binToIssuerMap = new();
        private Dictionary<string, AcquirerConfig> _acquirerMap = new();
        private Dictionary<string, string> _responseCodeMap = new();

        private ConfigurationLoader() { }

        /// <summary>
        /// Get the single instance of ConfigurationLoader
        /// </summary>
        public static ConfigurationLoader Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new ConfigurationLoader();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Load all configuration files from the Config directory at solution root
        /// </summary>
        public void LoadConfigurations(string configDirectory)
        {
            if (!Directory.Exists(configDirectory))
                throw new DirectoryNotFoundException($"Config directory not found: {configDirectory}");

            Console.WriteLine($"[CONFIG] Loading configurations from: {configDirectory}");

            // Load BIN Routing
            string binConfigPath = Path.Combine(configDirectory, "BINconfig.xml");
            _binRouting = LoadXmlConfig<BinRoutingConfiguration>(binConfigPath);
            BuildBinToIssuerMap();
            Console.WriteLine($"[CONFIG] Loaded {_binRouting.Banks.Count} issuer banks with {_binToIssuerMap.Count} total BINs");

            // Load Acquirer Routing
            string acqConfigPath = Path.Combine(configDirectory, "ACQconfig.xml");
            _acquirerRouting = LoadXmlConfig<AcquirerRoutingConfiguration>(acqConfigPath);
            BuildAcquirerMap();
            Console.WriteLine($"[CONFIG] Loaded {_acquirerRouting.Acquirers.Count} acquirer banks");

            // Load Response Codes
            string rcConfigPath = Path.Combine(configDirectory, "RCconfig.xml");
            _responseCodes = LoadXmlConfig<ResponseCodeConfiguration>(rcConfigPath);
            BuildResponseCodeMap();
            Console.WriteLine($"[CONFIG] Loaded {_responseCodes.Codes.Count} response codes");

            // ADD THIS: Load Database Configuration
            string dbConfigPath = Path.Combine(configDirectory, "DBconfig.xml");
            if (File.Exists(dbConfigPath))
            {
                _databaseConfig = LoadXmlConfig<DatabaseConfiguration>(dbConfigPath);
                Console.WriteLine($"[CONFIG] Loaded database configuration (Logging: {_databaseConfig.EnableLogging})");
            }
            else
            {
                Console.WriteLine($"[CONFIG] WARNING: DBconfig.xml not found, database logging disabled");
                _databaseConfig = new DatabaseConfiguration { EnableLogging = false };
            }

            Console.WriteLine("[CONFIG] All configurations loaded successfully!\n");
        }

        /// <summary>
        /// Convenience method: Auto-detect config path relative to application
        /// Useful for development - looks for Config folder at solution root
        /// </summary>
        public void LoadConfigurationsAuto()
        {
            // Try to find Config folder by going up from bin directory
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // Go up from bin\Debug\net8.0 (or Release) to solution root
            string? solutionRoot = baseDir;
            for (int i = 0; i < 5; i++) // Try up to 5 levels up
            {
                string testPath = Path.Combine(solutionRoot!, "Config");
                if (Directory.Exists(testPath))
                {
                    LoadConfigurations(testPath);
                    return;
                }
                solutionRoot = Directory.GetParent(solutionRoot!)?.FullName;
                if (solutionRoot == null) break;
            }

            throw new DirectoryNotFoundException(
                "Could not find Config directory. Please call LoadConfigurations() with explicit path.");
        }

        /// <summary>
        /// Generic method to load any XML config file
        /// </summary>
        private T LoadXmlConfig<T>(string filePath) where T : class
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Configuration file not found: {filePath}");

            var serializer = new XmlSerializer(typeof(T));
            using (var reader = new StreamReader(filePath))
            {
                var config = serializer.Deserialize(reader) as T;
                if (config == null)
                    throw new InvalidOperationException($"Failed to deserialize {filePath}");
                return config;
            }
        }

        private void BuildBinToIssuerMap()
        {
            _binToIssuerMap.Clear();
            foreach (var bank in _binRouting!.Banks)
            {
                foreach (var bin in bank.AllBins)
                {
                    _binToIssuerMap[bin] = bank;
                }
            }
        }

        private void BuildAcquirerMap()
        {
            _acquirerMap.Clear();
            foreach (var acq in _acquirerRouting!.Acquirers)
            {
                _acquirerMap[acq.AcquirerCode] = acq;
            }
        }

        private void BuildResponseCodeMap()
        {
            _responseCodeMap.Clear();
            foreach (var rc in _responseCodes!.Codes)
            {
                _responseCodeMap[rc.Code] = rc.Description;
            }
        }

        // ========== PUBLIC API ==========

        public IssuerBankConfig? GetIssuerByBIN(string cardBIN)
        {
            return _binToIssuerMap.ContainsKey(cardBIN)
                ? _binToIssuerMap[cardBIN]
                : null;
        }

        public AcquirerConfig? GetAcquirerByCode(string acquirerCode)
        {
            return _acquirerMap.ContainsKey(acquirerCode)
                ? _acquirerMap[acquirerCode]
                : null;
        }

        public string GetResponseDescription(string responseCode)
        {
            return _responseCodeMap.ContainsKey(responseCode)
                ? _responseCodeMap[responseCode]
                : $"Unknown response code: {responseCode}";
        }

        public List<IssuerBankConfig> GetAllIssuers()
        {
            return _binRouting?.Banks ?? new List<IssuerBankConfig>();
        }

        public List<AcquirerConfig> GetAllAcquirers()
        {
            return _acquirerRouting?.Acquirers ?? new List<AcquirerConfig>();
        }

        public bool IsBINRoutable(string cardBIN)
        {
            return _binToIssuerMap.ContainsKey(cardBIN);
        }

        public ConfigurationStats GetStats()
        {
            return new ConfigurationStats
            {
                TotalIssuers = _binRouting?.Banks.Count ?? 0,
                TotalBINs = _binToIssuerMap.Count,
                TotalAcquirers = _acquirerRouting?.Acquirers.Count ?? 0,
                TotalResponseCodes = _responseCodes?.Codes.Count ?? 0
            };
        }

        public DatabaseConfiguration DatabaseConfig => _databaseConfig ?? throw new InvalidOperationException("Database configuration not loaded");
    }

    public class ConfigurationStats
    {
        public int TotalIssuers { get; set; }
        public int TotalBINs { get; set; }
        public int TotalAcquirers { get; set; }
        public int TotalResponseCodes { get; set; }

        public override string ToString()
        {
            return $"Issuers: {TotalIssuers}, BINs: {TotalBINs}, Acquirers: {TotalAcquirers}, Response Codes: {TotalResponseCodes}";
        }
    }
}       
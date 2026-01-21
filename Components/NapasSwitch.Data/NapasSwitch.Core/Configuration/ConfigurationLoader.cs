using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using core.Models.Configuration;

namespace core.Configuration
{
    
    /// Loads and manages all XML configuration files for the switch
    /// This is a Singleton - only one instance exists throughout the application
    
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
        
        // Default/fallback issuer for when no BIN match is found
        private IssuerBankConfig? _defaultIssuer;

        private ConfigurationLoader() { }

        
        /// Get the single instance of ConfigurationLoader
        
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

        
        /// Load all configuration files from the Config directory at solution root
        
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

        
        /// Convenience method: Auto-detect config path relative to application
        /// Useful for development - looks for Config folder at solution root
        
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

        
        /// Generic method to load any XML config file
        
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
            _defaultIssuer = null;
            
            foreach (var bank in _binRouting!.Banks)
            {
                // Check if this is the default/fallback issuer
                if (bank.IsDefault)
                {
                    _defaultIssuer = bank;
                    Console.WriteLine($"[CONFIG] Default issuer set to: {bank.IssuerName} ({bank.Host}:{bank.Port})");
                }
                
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
            // First try exact BIN match
            if (_binToIssuerMap.ContainsKey(cardBIN))
                return _binToIssuerMap[cardBIN];
            
            // If no match, return the default issuer (if configured)
            if (_defaultIssuer != null)
            {
                Console.WriteLine($"[ROUTING] No BIN match for {cardBIN}, using default: {_defaultIssuer.IssuerName}");
                return _defaultIssuer;
            }
            
            return null;
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
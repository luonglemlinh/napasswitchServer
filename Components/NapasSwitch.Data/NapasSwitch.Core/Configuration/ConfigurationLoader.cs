using System;
using core.Helpers;
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
        private ServerConfiguration? _serverConfig;

        // Quick lookup dictionaries (for performance!)
        // Volatile ensures visibility across threads when swapped atomically during reload.
        private volatile Dictionary<string, IssuerBankConfig> _binToIssuerMap = new();
        private volatile Dictionary<string, AcquirerConfig> _acquirerMap = new();
        private volatile Dictionary<string, string> _responseCodeMap = new();
        
        // Default/fallback issuer for when no BIN match is found
        private volatile IssuerBankConfig? _defaultIssuer;

        // Lock for protecting configuration reload operations
        private readonly object _configLock = new object();

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
            lock (_configLock)
            {
            if (!Directory.Exists(configDirectory))
                throw new DirectoryNotFoundException($"Config directory not found: {configDirectory}");

            SwitchLogger.Info($"[CONFIG] Loading configurations from: {Path.GetFileName(configDirectory)}/");

            // Load BIN Routing
            string binConfigPath = Path.Combine(configDirectory, "BINconfig.xml");
            _binRouting = LoadXmlConfig<BinRoutingConfiguration>(binConfigPath);
            BuildBinToIssuerMap();
            SwitchLogger.Info($"[CONFIG] Loaded {_binRouting.Banks.Count} issuer banks with {_binToIssuerMap.Count} total BINs");

            // Load Acquirer Routing
            string acqConfigPath = Path.Combine(configDirectory, "ACQconfig.xml");
            _acquirerRouting = LoadXmlConfig<AcquirerRoutingConfiguration>(acqConfigPath);
            BuildAcquirerMap();
            SwitchLogger.Info($"[CONFIG] Loaded {_acquirerRouting.Acquirers.Count} acquirer banks");

            // Load Response Codes
            string rcConfigPath = Path.Combine(configDirectory, "RCconfig.xml");
            _responseCodes = LoadXmlConfig<ResponseCodeConfiguration>(rcConfigPath);
            BuildResponseCodeMap();
            SwitchLogger.Info($"[CONFIG] Loaded {_responseCodes.Codes.Count} response codes");

            // Load Database Configuration
            string dbConfigPath = Path.Combine(configDirectory, "DBconfig.xml");
            if (File.Exists(dbConfigPath))
            {
                _databaseConfig = LoadXmlConfig<DatabaseConfiguration>(dbConfigPath);
                SwitchLogger.Info($"[CONFIG] Loaded database configuration (Logging: {_databaseConfig.EnableLogging})");
            }
            else
            {
                SwitchLogger.Info($"[CONFIG] WARNING: DBconfig.xml not found, database logging disabled");
                _databaseConfig = new DatabaseConfiguration { EnableLogging = false };
            }

            // Allow environment variable to override the connection string
            string? envConnStr = Environment.GetEnvironmentVariable("NAPAS_DB_CONNECTION_STRING");
            if (!string.IsNullOrEmpty(envConnStr))
            {
                _databaseConfig.ConnectionString = envConnStr;
                SwitchLogger.Info($"[CONFIG] Database connection string overridden by NAPAS_DB_CONNECTION_STRING env variable");
            }

            // Load Server Configuration (Listener Ports)
            string serverConfigPath = Path.Combine(configDirectory, "ServerConfig.xml");
            if (File.Exists(serverConfigPath))
            {
                _serverConfig = LoadXmlConfig<ServerConfiguration>(serverConfigPath);
                SwitchLogger.Info($"[CONFIG] Loaded server configuration - ISS Ports: [{string.Join(", ", _serverConfig.IssuerPorts)}], ACQ Ports: [{string.Join(", ", _serverConfig.AcquirerPorts)}]");
            }
            else
            {
                SwitchLogger.Info($"[CONFIG] WARNING: ServerConfig.xml not found, using default ports");
                _serverConfig = new ServerConfiguration 
                { 
                    IssuerPorts = new List<int> { 1111, 2222, 3333 },
                    AcquirerPorts = new List<int> { 1177 }
                };
            }

            SwitchLogger.Info($"[CONFIG] All configurations loaded successfully!\n");
            } // end lock
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
            var newMap = new Dictionary<string, IssuerBankConfig>();
            IssuerBankConfig? newDefault = null;
            
            foreach (var bank in _binRouting!.Banks)
            {
                // ENVIRONMENT OVERRIDES
                // Allows setting NAPAS_BIN_HOST_TCB or NAPAS_BIN_PORT_TS to override XML values
                string envHost = Environment.GetEnvironmentVariable($"NAPAS_BIN_HOST_{bank.BankCode}");
                string envPort = Environment.GetEnvironmentVariable($"NAPAS_BIN_PORT_{bank.BankCode}");

                if (!string.IsNullOrEmpty(envHost))
                {
                    SwitchLogger.Info($"[CONFIG] Overriding {bank.BankCode} Host: {bank.Host} -> {envHost}");
                    bank.Host = envHost;
                }

                if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out int portVal))
                {
                    SwitchLogger.Info($"[CONFIG] Overriding {bank.BankCode} Port: {bank.Port} -> {portVal}");
                    bank.Port = portVal;
                }

                // Check if this is the default/fallback issuer
                if (bank.IsDefault)
                {
                    newDefault = bank;
                    SwitchLogger.Info($"[CONFIG] Default issuer set to: {bank.IssuerName} ({bank.Host}:{bank.Port})");
                }
                
                foreach (var bin in bank.AllBins)
                {
                    newMap[bin] = bank;
                }
            }

            // Atomic reference swap
            _binToIssuerMap = newMap;
            _defaultIssuer = newDefault;
        }

        private void BuildAcquirerMap()
        {
            var newMap = new Dictionary<string, AcquirerConfig>();
            foreach (var acq in _acquirerRouting!.Acquirers)
            {
                newMap[acq.AcquirerCode] = acq;
            }
            _acquirerMap = newMap;
        }

        private void BuildResponseCodeMap()
        {
            var newMap = new Dictionary<string, string>();
            foreach (var rc in _responseCodes!.Codes)
            {
                newMap[rc.Code] = rc.Description;
            }
            _responseCodeMap = newMap;
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
                SwitchLogger.Info($"[ROUTING] No BIN match for {cardBIN}, using default: {_defaultIssuer.IssuerName}");
                return _defaultIssuer;
            }
            
            return null;
        }

        public IssuerBankConfig? GetIssuerByCode(string issuerCode)
        {
            return _binRouting?.Banks.FirstOrDefault(b => b.IssuerCode == issuerCode);
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
        
        public ServerConfiguration ServerConfig => _serverConfig ?? throw new InvalidOperationException("Server configuration not loaded");
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
using System.Collections.Generic;
using System.Xml.Serialization;

namespace core.Models.Configuration
{
    /// <summary>
    /// Server listener port configuration
    /// Defines which TCP ports the server listens on for ISS and ACQ connections
    /// </summary>
    [XmlRoot("ServerConfiguration")]
    public class ServerConfiguration
    {
        [XmlArray("ISSlisteningPorts")]
        [XmlArrayItem("Port")]
        public List<int> ISSlisteningPorts { get; set; } = new();

        [XmlArray("ISSconnectPorts")]
        [XmlArrayItem("Port")]
        public List<int> ISSconnectPorts { get; set; } = new();

        [XmlArray("AcquirerPorts")]
        [XmlArrayItem("Port")]
        public List<int> AcquirerPorts { get; set; } = new();

        [XmlElement("Settings")]
        public ServerSettings Settings { get; set; } = new();

        [XmlElement("Defaults")]
        public DefaultFieldsConfig Defaults { get; set; } = new();

        /// <summary>
        /// Get all listener ports combined
        /// </summary>
        public int[] GetAllPorts()
        {
            var allPorts = new List<int>();
            allPorts.AddRange(ISSlisteningPorts);
            allPorts.AddRange(ISSconnectPorts);
            allPorts.AddRange(AcquirerPorts);
            return allPorts.ToArray();
        }
    }

    /// <summary>
    /// Default field values for mandatory NAPAS data elements.
    /// Previously hardcoded in TcpSwitchServer; now configurable via ServerConfig.xml.
    /// </summary>
    public class DefaultFieldsConfig
    {
        [XmlElement("DefaultAcquirerId")]
        public string DefaultAcquirerId { get; set; } = "970418";

        [XmlElement("DefaultMerchantId")]
        public string DefaultMerchantId { get; set; } = "NAPAS_MERCHT_01";

        [XmlElement("DefaultMerchantName")]
        public string DefaultMerchantName { get; set; } = "NAPAS TEST MERCHANT       HANOI        VN";

        [XmlElement("DefaultMerchantCategoryCode")]
        public string DefaultMerchantCategoryCode { get; set; } = "6011";

        [XmlElement("DefaultPOSEntryMode")]
        public string DefaultPOSEntryMode { get; set; } = "011";

        [XmlElement("DefaultPOSConditionCode")]
        public string DefaultPOSConditionCode { get; set; } = "00";

        [XmlElement("DefaultCurrencyCode")]
        public string DefaultCurrencyCode { get; set; } = "704";
    }

    public class ServerSettings
    {
        [XmlElement("MaxConcurrentConnection")]
        public int MaxConcurrentConnections { get; set; } = 1000;

        [XmlElement("ConnectionTimeout")]
        public int ConnectionTimeout { get; set; } = 300000; // 5 minutes default

        [XmlElement("HealthCheckPort")]
        public int HealthCheckPort { get; set; } = 8080;

        [XmlElement("LogDirectory")]
        public string LogDirectory { get; set; } = "Logs";
    }
}

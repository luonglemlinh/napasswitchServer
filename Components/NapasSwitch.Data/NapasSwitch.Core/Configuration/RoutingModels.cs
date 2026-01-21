using System.Collections.Generic;
using System.Xml.Serialization;

namespace core.Models.Configuration
{
    [XmlRoot("BinRoutingConfiguration")]
    public class BinRoutingConfiguration
    {
        [XmlArray("Banks")]
        [XmlArrayItem("Bank")]
        public List<IssuerBankConfig> Banks { get; set; } = new();
    }

    public class IssuerBankConfig
    {
        public string BankCode { get; set; } = string.Empty;
        public string BankName { get; set; } = string.Empty;

        public string IssuerCode { get; set; } = string.Empty;
        public string IssuerName { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public int Timeout { get; set; }
        
        /// <summary>
        /// If true, this issuer is used as fallback when no BIN match is found
        /// </summary>
        public bool IsDefault { get; set; } = false;

        [XmlArray("Bins")]
        [XmlArrayItem("Bin")]
        public List<string> Bins { get; set; } = new();

        [XmlIgnore]
        public IEnumerable<string> AllBins => Bins ?? new List<string>();
    }

    [XmlRoot("AcquirerRoutingConfiguration")]
    public class AcquirerRoutingConfiguration
    {
        [XmlArray("Acquirers")]
        [XmlArrayItem("Acquirer")]
        public List<AcquirerConfig> Acquirers { get; set; } = new();
    }

    public class AcquirerConfig
    {
        public string AcquirerCode { get; set; } = string.Empty;
        public string AcquirerID { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public int Timeout { get; set; }
        public int MaxConnections { get; set; }
    }

    [XmlRoot("ResponseCodeConfiguration")]
    public class ResponseCodeConfiguration
    {
        [XmlArray("Codes")]
        [XmlArrayItem("Code")]
        public List<ResponseCode> Codes { get; set; } = new();
    }

    public class ResponseCode
    {
        [XmlAttribute]
        public string Code { get; set; } = string.Empty;

        [XmlElement]
        public string Description { get; set; } = string.Empty;
    }
}

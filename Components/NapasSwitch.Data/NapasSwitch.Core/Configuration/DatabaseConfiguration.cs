using System.Xml.Serialization;

namespace core.Models.Configuration
{
    
    /// Database configuration loaded from DBconfig.xml
    
    [XmlRoot("DatabaseConfiguration")]
    public class DatabaseConfiguration
    {
        public string ConnectionString { get; set; } = string.Empty;
        public bool EnableLogging { get; set; } = true;
        public int CommandTimeout { get; set; } = 30;
    }
}
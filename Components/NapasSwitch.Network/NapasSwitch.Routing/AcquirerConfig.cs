namespace NapasSwitch.Routing;

public class AcquirerConfig
{
    public string AcquirerCode { get; set; } = string.Empty;
    public string AcquirerID { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public int Timeout { get; set; }
    public int MaxConnections { get; set; }

}

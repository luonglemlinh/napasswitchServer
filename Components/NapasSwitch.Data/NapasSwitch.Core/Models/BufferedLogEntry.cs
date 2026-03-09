using System;

namespace core.Models
{
    public class BufferedLogEntry
    {
        public string? TRANSACTIONID { get; set; }
        public string? ACQ { get; set; }
        public string? ISS { get; set; }
        public string? SENDER { get; set; }
        public string DIRECTION { get; set; } = string.Empty;
        public string MESSAGETYPE { get; set; } = string.Empty;
        public string? PROCESSINGCODE { get; set; }
        public decimal? AMOUNT { get; set; }
        public string? STAN { get; set; }
        public string? RRN { get; set; }
        public DateTime LOGTIME { get; set; } = DateTime.UtcNow;
        public string? RC { get; set; }
        public string? SESSIONID { get; set; }
        public string RAWMESSAGE { get; set; } = string.Empty;
    }
}

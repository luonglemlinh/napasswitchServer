using System;

namespace core.Models
{
    public class BufferedLogEntry
    {
        public string Direction { get; set; } = string.Empty;
        public string? ACQ { get; set; }
        public string? ISS { get; set; }
        public string MessageType { get; set; } = string.Empty;
        public string? ProcessingCode { get; set; }
        public decimal? Amount { get; set; }
        public string? STAN { get; set; }
        public string? RRN { get; set; }
        public string? ResponseCode { get; set; }
        public string RawMessage { get; set; } = string.Empty;
        public DateTime LogTime { get; set; } = DateTime.UtcNow;
    }
}

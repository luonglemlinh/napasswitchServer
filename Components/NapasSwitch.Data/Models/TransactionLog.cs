using System;

namespace data.Models
{
    
    /// Database entity for storing transaction logs
    /// Every transaction that passes through the switch is recorded here
    
    public class TransactionLog
    {
        public long Id { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string MessageType { get; set; } = string.Empty;
        public string? PAN { get; set; }
        public string? ProcessingCode { get; set; }
        public decimal? Amount { get; set; }
        public string? STAN { get; set; }
        public string? AcquirerID { get; set; }
        public string? IssuerID { get; set; }
        public string? ResponseCode { get; set; }
        public string? TerminalID { get; set; }
        public string? MerchantID { get; set; }
        public DateTime TransactionTime { get; set; }
        public DateTime LoggedAt { get; set; } = DateTime.UtcNow;
        public int ProcessingTimeMs { get; set; }
        
        
        /// Direction: INBOUND (from ACQ), OUTBOUND (to ISS), RESPONSE (from ISS)
        
        public string Direction { get; set; } = string.Empty;
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace core.Models
{
    public class Transaction
    {
        public required string TransactionId { get; set; }
        public required string SessionId { get; set; }
        public required string AcquirerId { get; set; }
        public string? IssuerId { get; set; }
        public required string CardNumber { get; set; }
        public required string TransactionType { get; set; }
        public required string Amount { get; set; }
        public required string ResponseCode { get; set; }
        public string? ResponseDescription { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public TimeSpan ProcessingTime { get; set; }
    }
}

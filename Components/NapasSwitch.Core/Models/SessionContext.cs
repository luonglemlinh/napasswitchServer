using System;
using System.Collections.Generic;
using System.Text;

namespace core.Models
{
    public class SessionContext
    {
        public required string SessionId { get; set; }
        public required string AcquirerId { get; set; }
        public string? IssuerId { get; set; }
        public required string CardNumber { get; set; }
        public required string TransactionType { get; set; }
        public required decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public Dictionary<string, string>? ISOfields { get; set; } = new();
    }
}

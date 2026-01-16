using System;
using System.Collections.Generic;
using System.Text;

namespace core.Models
{
    /// <summary>
    /// Represents an ISO-8583 message
    /// Used by IsoParser to parse and build messages
    /// </summary>
    public class IsoMessage
    {
        /// <summary>
        /// Message Type Indicator (MTI)
        /// Examples: "0200" (Purchase Request), "0210" (Purchase Response)
        /// </summary>
        public string MessageType { get; set; } = string.Empty;

        /// <summary>
        /// Data Elements - Field number (2-128) mapped to field value
        /// Example: Fields[2] = "4111111111111111" (card number)
        /// </summary>
        public Dictionary<int, string> Fields { get; set; } = new();

        // ========== Helper Methods for Common Fields ==========

        /// <summary>
        /// Field 2: Primary Account Number (Card Number)
        /// </summary>
        public string? GetPAN() => GetField(2);
        public void SetPAN(string value) => SetField(2, value);

        /// <summary>
        /// Field 3: Processing Code (transaction type)
        /// "000000" = Purchase, "310000" = Balance Inquiry, "020000" = Void
        /// </summary>
        public string? GetProcessingCode() => GetField(3);
        public void SetProcessingCode(string value) => SetField(3, value);

        /// <summary>
        /// Field 4: Transaction Amount (in cents/smallest unit)
        /// Example: "000000010000" = 100.00
        /// </summary>
        public string? GetAmount() => GetField(4);
        public void SetAmount(decimal amount) => SetField(4, ((long)(amount * 100)).ToString("D12"));

        /// <summary>
        /// Field 11: System Trace Audit Number (STAN) - Unique transaction ID
        /// </summary>
        public string? GetSTAN() => GetField(11);
        public void SetSTAN(string value) => SetField(11, value);

        /// <summary>
        /// Field 32: Acquiring Institution ID (which ACQ sent this)
        /// </summary>
        public string? GetAcquirerID() => GetField(32);
        public void SetAcquirerID(string value) => SetField(32, value);

        /// <summary>
        /// Field 33: Forwarding Institution ID (which ISS to route to)
        /// </summary>
        public string? GetIssuerID() => GetField(33);
        public void SetIssuerID(string value) => SetField(33, value);

        /// <summary>
        /// Field 39: Response Code
        /// "00" = Approved, "51" = Insufficient Funds, etc.
        /// </summary>
        public string? GetResponseCode() => GetField(39);
        public void SetResponseCode(string value) => SetField(39, value);

        /// <summary>
        /// Field 41: Card Acceptor Terminal ID
        /// </summary>
        public string? GetTerminalID() => GetField(41);
        public void SetTerminalID(string value) => SetField(41, value);

        /// <summary>
        /// Field 42: Card Acceptor ID (Merchant ID)
        /// </summary>
        public string? GetMerchantID() => GetField(42);
        public void SetMerchantID(string value) => SetField(42, value);

        // ========== Generic Field Access ==========

        /// <summary>
        /// Get field value by field number
        /// Returns null if field doesn't exist
        /// </summary>
        public string? GetField(int fieldNumber)
        {
            return Fields.ContainsKey(fieldNumber) ? Fields[fieldNumber] : null;
        }

        /// <summary>
        /// Set field value by field number
        /// Removes field if value is null or empty
        /// </summary>
        public void SetField(int fieldNumber, string? value)
        {
            if (string.IsNullOrEmpty(value))
                Fields.Remove(fieldNumber);
            else
                Fields[fieldNumber] = value;
        }

        /// <summary>
        /// Check if a field is present
        /// </summary>
        public bool HasField(int fieldNumber) => Fields.ContainsKey(fieldNumber);

        /// <summary>
        /// Get card BIN (first 6 digits of PAN) for routing
        /// </summary>
        public string? GetCardBIN()
        {
            var pan = GetPAN();
            return pan?.Length >= 6 ? pan.Substring(0, 6) : null;
        }

        /// <summary>
        /// Display-friendly representation (masks PAN)
        /// </summary>
        public override string ToString()
        {
            var pan = GetPAN();
            var maskedPAN = pan != null && pan.Length >= 10
                ? $"{pan.Substring(0, 6)}****{pan.Substring(pan.Length - 4)}"
                : "N/A";

            return $"MTI: {MessageType} | PAN: {maskedPAN} | STAN: {GetSTAN()} | RC: {GetResponseCode()}";
        }
    }
}
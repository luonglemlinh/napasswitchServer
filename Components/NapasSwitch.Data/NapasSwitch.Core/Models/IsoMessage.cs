using System;
using System.Collections.Generic;
using System.Text;

namespace core.Models
{
    
    /// Represents an ISO-8583 message
    /// Used by IsoParser to parse and build messages
    
    public class IsoMessage
    {
        
        /// Message Type Indicator (MTI)
        /// Examples: "0200" (Purchase Request), "0210" (Purchase Response)
        
        public string MessageType { get; set; } = string.Empty;
        
        /// <summary>
        /// F00: Message Header (Optional)
        /// Examples: "NAPASBASE.ISO", TPDU "6000000000"
        /// </summary>
        public string Header { get; set; } = string.Empty;

        /// <summary>
        /// F01: Primary Bitmap (Hex string)
        /// </summary>
        public string PrimaryBitmap { get; set; } = string.Empty;

        /// <summary>
        /// Secondary Bitmap if present (Hex string)
        /// </summary>
        public string SecondaryBitmap { get; set; } = string.Empty;

        
        /// Data Elements - Field number (2-128) mapped to field value
        /// Example: Fields[2] = "4111111111111111" (card number)
        
        public Dictionary<int, string> Fields { get; set; } = new();

        // ========== Helper Methods for Common Fields ==========

        
        /// Field 2: Primary Account Number (Card Number)
        
        public string? GetPAN() => GetField(2);
        public void SetPAN(string value) => SetField(2, value);

        
        /// Field 3: Processing Code (transaction type)
        /// "000000" = Purchase, "310000" = Balance Inquiry, "020000" = Void
        
        public string? GetProcessingCode() => GetField(3);
        public void SetProcessingCode(string value) => SetField(3, value);

        
        /// Field 4: Transaction Amount (in cents/smallest unit)
        /// Example: "000000010000" = 100.00
        
        public string? GetAmount() => GetField(4);
        public void SetAmount(decimal amount) => SetField(4, ((long)(amount * 100)).ToString("D12"));

        
        /// Field 11: System Trace Audit Number (STAN) - Unique transaction ID
        
        public string? GetSTAN() => GetField(11);
        public void SetSTAN(string value) => SetField(11, value);

        
        /// Field 32: Acquiring Institution ID (which ACQ sent this)
        
        public string? GetAcquirerID() => GetField(32);
        public void SetAcquirerID(string value) => SetField(32, value);

        
        /// Field 33: Forwarding Institution ID (which ISS to route to)
        
        public string? GetIssuerID() => GetField(33);
        public void SetIssuerID(string value) => SetField(33, value);

        
        /// Field 63: Transaction Reference Number (TRN)
        
        public string? GetTRN() => GetField(63);
        public void SetTRN(string value) => SetField(63, value);

        
        /// Field 39: Response Code
        /// "00" = Approved, "51" = Insufficient Funds, etc.
        
        public string? GetResponseCode() => GetField(39);
        public void SetResponseCode(string value) => SetField(39, value);

        
        /// Field 41: Card Acceptor Terminal ID
        
        public string? GetTerminalID() => GetField(41);
        public void SetTerminalID(string value) => SetField(41, value);

        
        /// Field 42: Card Acceptor ID (Merchant ID)
        
        public string? GetMerchantID() => GetField(42);
        public void SetMerchantID(string value) => SetField(42, value);

        // ========== Generic Field Access ==========

        
        /// Get field value by field number
        /// Returns null if field doesn't exist
        
        public string? GetField(int fieldNumber)
        {
            return Fields.ContainsKey(fieldNumber) ? Fields[fieldNumber] : null;
        }

        
        /// Set field value by field number
        /// Removes field if value is null or empty
        
        public void SetField(int fieldNumber, string? value)
        {
            if (string.IsNullOrEmpty(value))
                Fields.Remove(fieldNumber);
            else
                Fields[fieldNumber] = value;
        }

        
        /// Check if a field is present
        
        public bool HasField(int fieldNumber) => Fields.ContainsKey(fieldNumber);

        
        /// Get card BIN (first 6 digits of PAN) for routing
        
        public string? GetCardBIN()
        {
            var pan = GetPAN();
            return pan?.Length >= 6 ? pan.Substring(0, 6) : null;
        }

        
        /// Display-friendly representation (masks PAN)
        
        public override string ToString()
        {
            var pan = GetPAN();
            var maskedPAN = pan != null && pan.Length >= 10
                ? $"{pan.Substring(0, 6)}****{pan.Substring(pan.Length - 4)}"
                : "N/A";

            string headerPart = string.IsNullOrEmpty(Header) ? "" : $"[{Header}] ";
            return $"{headerPart}MTI: {MessageType} | PAN: {maskedPAN} | STAN: {GetSTAN()} | RC: {GetResponseCode()}";
        }
    }
}
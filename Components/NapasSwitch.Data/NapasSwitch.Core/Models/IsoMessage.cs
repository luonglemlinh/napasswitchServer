using System;
using System.Collections.Generic;
using System.Text;
using core.Helpers;

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

        /// <summary>
        /// Get Field 4 as decimal (in currency units, e.g., 100.00 instead of 10000)
        /// </summary>
        public decimal GetAmountDecimal()
        {
            var amountStr = GetField(4);
            if (decimal.TryParse(amountStr, out decimal parsedAmount))
            {
                return parsedAmount / 100;
            }
            return 0;
        }

        
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

        /// Field 23: Card Sequence Number (used in chip/EMV transactions)
        public string? GetCardSequenceNumber() => GetField(23);
        public void SetCardSequenceNumber(string value) => SetField(23, value);


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

        /// Field 55: ICC System Related Data (EMV chip data in TLV format)
        public string? GetICCData() => GetField(55);
        public void SetICCData(string value) => SetField(55, value);

        /// <summary>
        /// Check if this is a chip/EMV transaction based on DE#22 POS Entry Mode.
        /// Chip prefixes: 05 (ICC), 07 (Contactless ICC), 91 (Contactless VSDC).
        /// </summary>
        public bool IsChipTransaction()
        {
            var posEntryMode = GetField(22);
            if (string.IsNullOrEmpty(posEntryMode) || posEntryMode.Length < 2) return false;
            return posEntryMode.StartsWith("05") || posEntryMode.StartsWith("07") || posEntryMode.StartsWith("91");
        }

        /// <summary>
        /// Build DE#90 (Original Data Elements) from an original request message.
        /// Fixed 42-byte numeric: MTI(4) + STAN(6) + TransDateTime(10) + AcqID(11) + FwdID(11)
        /// </summary>
        public static string BuildDE90(IsoMessage originalRequest)
        {
            return BuildDE90(
                originalRequest.MessageType,
                originalRequest.GetField(11),
                originalRequest.GetField(7),
                originalRequest.GetField(32),
                originalRequest.GetField(33));
        }

        /// <summary>
        /// Build DE#90 (Original Data Elements) from individual field values.
        /// Fixed 42-byte numeric: MTI(4) + STAN(6) + TransDateTime(10) + AcqID(11) + FwdID(11)
        /// Sub-element 5 (forwarding institution) is filled with '0' when not provided.
        /// </summary>
        public static string BuildDE90(string? originalMti, string? originalStan, string? originalDateTime, string? originalAcqId, string? originalFwdId)
        {
            string mti = (originalMti ?? "0200").PadRight(4, '0')[..4];
            string stan = (originalStan ?? "000000").PadLeft(6, '0')[..6];
            string dateTime = (originalDateTime ?? "0000000000").PadLeft(10, '0')[..10];
            string acqId = (originalAcqId ?? "").PadRight(11, '0')[..11];
            string fwdId = string.IsNullOrWhiteSpace(originalFwdId)
                ? "00000000000"
                : originalFwdId.PadRight(11, '0')[..11];

            return $"{mti}{stan}{dateTime}{acqId}{fwdId}";
        }

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
            // 1. Try Field 2 (PAN)
            var pan = GetPAN();
            if (!string.IsNullOrEmpty(pan) && pan.Length >= 6)
                return pan.Substring(0, 6);

            // 2. Fallback to Field 35 (Track 2)
            var track2 = GetField(35);
            if (!string.IsNullOrEmpty(track2))
            {
                var panFromTrack2 = GetPANFromTrack2(track2);
                if (!string.IsNullOrEmpty(panFromTrack2) && panFromTrack2.Length >= 6)
                    return panFromTrack2.Substring(0, 6);
            }

            return null;
        }

        private string? GetPANFromTrack2(string track2)
        {
            // Standard ISO 7813 Track 2: ;PAN=EXPIRY... or PAN=EXPIRY...
            // Way4/NAPAS often uses 'D' as separator
            char[] separators = { '=', 'D', 'd' };
            int sepIndex = track2.IndexOfAny(separators);
            
            string panPart = sepIndex > 0 ? track2.Substring(0, sepIndex) : track2;
            
            // Strip leading sentinels or spaces
            panPart = panPart.TrimStart(';', ' ', '?', 'B');
            
            return panPart;
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

        public static string GetFieldDescription(int fieldNumber)
        {
            return fieldNumber switch
            {
                2 => "PAN",
                3 => "Processing Code",
                4 => "Amount",
                7 => "Transmission Date/Time",
                11 => "STAN",
                12 => "Local Time",
                13 => "Local Date",
                14 => "Expiration Date",
                18 => "Merchant Type",
                19 => "Acquire Country",
                22 => "POS Entry Mode",
                23 => "Card Sequence Number",
                25 => "POS Condition Code",
                32 => "Acquirer ID",
                33 => "Fwd Inst ID Code",
                35 => "Track 2",
                37 => "RRN",
                38 => "Auth ID",
                39 => "Response Code",
                41 => "Terminal ID",
                42 => "Merchant ID",
                43 => "Merchant Name/Loc",
                49 => "Currency Code",
                52 => "PIN Block",
                54 => "Additional Amounts",
                55 => "ICC/EMV Data",
                63 => "TRN",
                70 => "Network Info Code",
                90 => "Original Data Elements",
                100 => "Receiving ID",
                102 => "Account ID 1",
                103 => "Account ID 2",
                _ => $"DE{fieldNumber}"
            };
        }

        public void LogImportantFields(string sessionId, string prefix = "TS-RESPONSE")
        {
            SwitchLogger.Info("[{SessionId}] === {Prefix} IMPORTANT FIELDS ===", sessionId, prefix);
            SwitchLogger.Info("   MTI: {MessageType}", MessageType);

            int[] importantFields = { 2, 3, 4, 7, 11, 12, 13, 32, 33, 37, 38, 39, 41, 42, 52, 55, 63 };

            foreach (int field in importantFields)
            {
                if (HasField(field))
                {
                    string value = GetField(field)!;

                    // Mask sensitive data
                    if (field == 2) 
                    {
                        value = value.Length >= 10 
                            ? $"{value.Substring(0, 6)}****{value.Substring(value.Length - 4)}" 
                            : "******";
                    }
                    else if (field == 35)
                    {
                        // Mask Track 2 if logged elsewhere but here we keep it safe
                        value = "[MASKED]";
                    }
                    else if (field == 52)
                    {
                        value = "[PIN BLOCK PRESENT]";
                    }
                    else if (field == 55)
                    {
                        value = $"[EMV/ICC Data: {value.Length / 2} bytes]";
                    }

                    SwitchLogger.Info("   {FieldNum} ({FieldDesc}): {Value}", field.ToString("D3"), GetFieldDescription(field), value);
                }
            }
            SwitchLogger.Info("[{SessionId}] =====================================", sessionId);
        }
        public void LogAllFields(string sessionId, string prefix = "ISO-DEBUG")
        {
            SwitchLogger.Info("[{SessionId}] === {Prefix} FULL MESSAGE DUMP ===", sessionId, prefix);
            SwitchLogger.Info("   MTI: {MessageType}", MessageType);
            if (!string.IsNullOrEmpty(Header)) SwitchLogger.Info("   Header: {Header}", Header);

            foreach (var field in Fields.OrderBy(f => f.Key))
            {
                int fieldNum = field.Key;
                string value = field.Value;
                string label = GetFieldDescription(fieldNum);

                // DE1 (Bitmap) special handling to show both halves
                if (fieldNum == 1)
                {
                    string fullBitmap = PrimaryBitmap;
                    if (!string.IsNullOrEmpty(SecondaryBitmap)) fullBitmap += SecondaryBitmap;
                    value = fullBitmap;
                }

                // Masking rule for testing
                if (fieldNum == 2) 
                    value = value.Length >= 10 ? $"{value.Substring(0, 6)}****{value.Substring(value.Length - 4)}" : "******";
                else if (fieldNum == 35 || fieldNum == 36)
                    value = "[TRACK DATA MASKED]";
                else if (fieldNum == 52)
                    value = "[PIN BLOCK MASKED]";
                else if (fieldNum == 55)
                    value = $"[EMV/ICC Data: {value.Length / 2} bytes]";

                SwitchLogger.Info("   {FieldNum} ({Label}): {Value}", fieldNum.ToString("D3"), label, value);
            }
            SwitchLogger.Info("[{SessionId}] =====================================", sessionId);
        }
    }
}
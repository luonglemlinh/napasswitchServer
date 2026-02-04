using System;

namespace core.Helpers
{
    /// <summary>
    /// Helper class for identifying and describing transaction types based on MTI and Processing Code
    /// </summary>
    public static class TransactionTypeHelper
    {
        /// <summary>
        /// Get transaction type identifier based on MTI and Processing Code (Field 3)
        /// </summary>
        /// <param name="mti">Message Type Indicator (e.g., "0200", "0400")</param>
        /// <param name="processingCode">Processing Code from Field 3 (e.g., "000000", "300000")</param>
        /// <returns>Transaction type identifier</returns>
        public static string GetTransactionType(string mti, string? processingCode)
        {
            if (string.IsNullOrEmpty(mti))
                return "UNKNOWN";

            // Reversal messages (0400/0410/0420/0430)
            if (mti.StartsWith("04"))
                return "REVERSAL";

            // For financial messages (0200/0210), check processing code
            if (mti == "0200" || mti == "0210")
            {
                if (string.IsNullOrEmpty(processingCode) || processingCode.Length < 2)
                    return "FINANCIAL";

                // First 2 digits of processing code indicate transaction type
                string txnType = processingCode.Substring(0, 2);

                return txnType switch
                {
                    "00" => "PURCHASE",           // 00xxxx - Purchase
                    "01" => "CASH_WITHDRAWAL",    // 01xxxx - Cash Withdrawal
                    "09" => "CASH_DEPOSIT",       // 09xxxx - Cash Deposit
                    "20" => "REFUND",             // 20xxxx - Refund
                    "30" => "BALANCE_INQUIRY",    // 30xxxx - Balance Inquiry
                    "31" => "BALANCE_INQUIRY",    // 31xxxx - Available Balance Inquiry
                    "40" => "TRANSFER",           // 40xxxx - Transfer
                    _ => $"FINANCIAL_{txnType}"   // Unknown financial type
                };
            }

            // Network management messages (0800/0810)
            if (mti == "0800" || mti == "0810")
                return "NETWORK_MGMT";

            return $"MTI_{mti}";
        }

        /// <summary>
        /// Get human-readable description of transaction type
        /// </summary>
        /// <param name="mti">Message Type Indicator</param>
        /// <param name="processingCode">Processing Code from Field 3</param>
        /// <returns>Human-readable description</returns>
        public static string GetTransactionDescription(string mti, string? processingCode)
        {
            string type = GetTransactionType(mti, processingCode);

            return type switch
            {
                "PURCHASE" => "Purchase",
                "CASH_WITHDRAWAL" => "Cash Withdrawal",
                "CASH_DEPOSIT" => "Cash Deposit",
                "BALANCE_INQUIRY" => "Balance Inquiry",
                "TRANSFER" => "Transfer",
                "REFUND" => "Refund",
                "REVERSAL" => "Reversal",
                "NETWORK_MGMT" => "Network Management",
                "FINANCIAL" => "Financial Transaction",
                _ => type.Replace("_", " ")
            };
        }

        /// <summary>
        /// Get formatted transaction type string for logging
        /// </summary>
        /// <param name="mti">Message Type Indicator</param>
        /// <param name="processingCode">Processing Code from Field 3</param>
        /// <returns>Formatted string like "Purchase (0200/000000)"</returns>
        public static string GetFormattedTransactionType(string mti, string? processingCode)
        {
            string description = GetTransactionDescription(mti, processingCode);
            string pc = processingCode ?? "N/A";
            return $"{description} ({mti}/{pc})";
        }

        /// <summary>
        /// Check if transaction is a financial transaction (requires authorization)
        /// </summary>
        public static bool IsFinancialTransaction(string mti)
        {
            return mti == "0200" || mti == "0210";
        }

        /// <summary>
        /// Check if transaction is a reversal
        /// </summary>
        public static bool IsReversal(string mti)
        {
            return mti == "0400" || mti == "0410" || mti == "0420" || mti == "0430";
        }

        /// <summary>
        /// Check if transaction is a balance inquiry (non-financial)
        /// </summary>
        public static bool IsBalanceInquiry(string? processingCode)
        {
            if (string.IsNullOrEmpty(processingCode) || processingCode.Length < 2)
                return false;

            string txnType = processingCode.Substring(0, 2);
            return txnType == "30" || txnType == "31";
        }
    }
}

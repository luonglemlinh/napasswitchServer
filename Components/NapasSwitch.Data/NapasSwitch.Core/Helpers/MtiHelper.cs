namespace core.Helpers
{
    /// <summary>
    /// Utility class for ISO-8583 Message Type Indicator (MTI) operations.
    /// Eliminates magic string usage throughout the codebase.
    /// </summary>
    public static class MtiHelper
    {
        // Request MTIs
        public const string AuthorizationRequest = "0200";
        public const string ReversalRequest = "0400";
        public const string ReversalAdvice = "0420";
        public const string NetworkManagementRequest = "0800";

        // Response MTIs
        public const string AuthorizationResponse = "0210";
        public const string ReversalResponse = "0410";
        public const string ReversalAdviceResponse = "0430";
        public const string NetworkManagementResponse = "0810";

        /// <summary>
        /// Convert a request MTI to its response MTI (e.g., 0200 ? 0210, 0420 ? 0430).
        /// </summary>
        public static string GetResponseMTI(string requestMti)
        {
            if (int.TryParse(requestMti, out int mtiVal))
                return (mtiVal + 10).ToString("D4");
            return AuthorizationResponse; // Fallback
        }

        /// <summary>
        /// Check if an MTI represents a response.
        /// ISO-8583 function codes: 0=request, 1=request response, 2=advice, 3=advice response.
        /// Both '1' (e.g., 0210, 0410, 0810) and '3' (e.g., 0430) are responses.
        /// </summary>
        public static bool IsResponse(string mti)
        {
            return mti.Length == 4 && (mti[2] == '1' || mti[2] == '3');
        }

        /// <summary>
        /// Check if the MTI is a network management message (0800/0810).
        /// </summary>
        public static bool IsNetworkManagement(string mti)
        {
            return mti.Length >= 2 && mti[0] == '0' && mti[1] == '8';
        }

        /// <summary>
        /// Check if the MTI requires UnsettledTransaction store tracking.
        /// Includes 0200 (authorization), 0400 (reversal), and 0420 (advice/void).
        /// Note: 0420 gets immediate ACK to ACQ and separate ISS forwarding,
        /// but still needs store tracking for same-day void/reversal lookup.
        /// </summary>
        public static bool RequiresStoreTracking(string mti)
        {
            return mti == AuthorizationRequest || mti == ReversalRequest || mti == ReversalAdvice;
        }

        /// <summary>
        /// Backward-compatible alias for RequiresStoreTracking.
        /// Prefer RequiresStoreTracking in new code for clarity.
        /// </summary>
        public static bool IsFinancialRequest(string mti) => RequiresStoreTracking(mti);

        /// <summary>
        /// Check if the MTI is a reversal-type message (0400 or 0420).
        /// </summary>
        public static bool IsReversal(string mti)
        {
            return mti == ReversalRequest || mti == ReversalAdvice;
        }

        /// <summary>
        /// Check if the MTI is an advice message (0420). Advice messages
        /// receive an immediate 0430 acknowledgment before ISS forwarding.
        /// </summary>
        public static bool IsAdvice(string mti)
        {
            return mti == ReversalAdvice;
        }
    }
}

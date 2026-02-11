using core.Models;

namespace core.Helpers
{
    /// <summary>
    /// Centralized ISO-8583 response builder.
    /// Consolidates duplicate response creation logic from TcpSwitchServer and IssuerConnector.
    /// </summary>
    public static class IsoResponseBuilder
    {
        private static readonly int[] EssentialFields = { 2, 3, 4, 7, 11, 12, 13, 32, 33, 37, 41, 42 };

        /// <summary>
        /// Create a response with the specified response code, copying essential fields from the request.
        /// </summary>
        public static IsoMessage CreateErrorResponse(IsoMessage request, string responseCode)
        {
            var response = new IsoMessage
            {
                MessageType = MtiHelper.GetResponseMTI(request.MessageType)
            };

            CopyEssentialFields(request, response);
            response.SetResponseCode(responseCode);
            return response;
        }

        /// <summary>
        /// Create an approved (RC 00) response, copying essential fields from the request.
        /// </summary>
        public static IsoMessage CreateSuccessResponse(IsoMessage request)
        {
            return CreateErrorResponse(request, "00");
        }

        /// <summary>
        /// Create a timeout response (RC 68 - Response received too late).
        /// </summary>
        public static IsoMessage CreateTimeoutResponse(IsoMessage request)
        {
            return CreateErrorResponse(request, "68");
        }

        /// <summary>
        /// Create a system error response with the given response code.
        /// </summary>
        public static IsoMessage CreateSystemErrorResponse(IsoMessage request, string responseCode)
        {
            return CreateErrorResponse(request, responseCode);
        }

        private static void CopyEssentialFields(IsoMessage source, IsoMessage target)
        {
            foreach (var field in EssentialFields)
            {
                if (source.HasField(field))
                    target.SetField(field, source.GetField(field));
            }
        }
    }
}

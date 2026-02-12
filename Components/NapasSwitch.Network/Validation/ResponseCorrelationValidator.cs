using System;
using core.Helpers;
using System.Collections.Generic;
using core.Models;

namespace network.Validation
{
    public class ResponseCorrelationValidator
    {
        public MessageValidationResult ValidateResponseMatchesRequest(IsoMessage request, IsoMessage response)
        {
            var result = new MessageValidationResult();

            // Critical field: DE 11 - STAN must match
            var reqStan = request.GetField(11);
            var respStan = response.GetField(11);
            if (reqStan != respStan)
            {
                result.AddDataElementResult(new DataElementValidationResult
                {
                    DataElementNumber = 11,
                    DataElementName = "STAN",
                    IsValid = false,
                    ErrorCode = "30",
                    ErrorMessage = $"STAN mismatch - Request: {reqStan}, Response: {respStan}"
                });
            }

            // Critical field: DE 4 - Amount must match (for financial transactions)
            if (request.MessageType == "0200" && response.MessageType == "0210")
            {
                var reqAmount = request.GetField(4);
                var respAmount = response.GetField(4);
                if (reqAmount != respAmount)
                {
                    result.AddDataElementResult(new DataElementValidationResult
                    {
                        DataElementNumber = 4,
                        DataElementName = "Amount",
                        IsValid = false,
                        ErrorCode = "30",
                        ErrorMessage = $"Amount mismatch - Request: {reqAmount}, Response: {respAmount}"
                    });
                }
            }

            // Critical field: DE 3 - Processing code must match
            var reqProcCode = request.GetField(3);
            var respProcCode = response.GetField(3);
            if (reqProcCode != respProcCode)
            {
                result.AddDataElementResult(new DataElementValidationResult
                {
                    DataElementNumber = 3,
                    DataElementName = "Processing Code",
                    IsValid = false,
                    ErrorCode = "30",
                    ErrorMessage = $"Processing code mismatch - Request: {reqProcCode}, Response: {respProcCode}"
                });
            }

            // Important field: DE 2 - PAN should match (if present in response)
            if (request.HasField(2) && response.HasField(2))
            {
                var reqPan = request.GetField(2);
                var respPan = response.GetField(2);
                if (reqPan != respPan)
                {
                    result.AddDataElementResult(new DataElementValidationResult
                    {
                        DataElementNumber = 2,
                        DataElementName = "PAN",
                        IsValid = false,
                        ErrorCode = "30",
                        ErrorMessage = $"PAN mismatch - Request: {MaskPAN(reqPan)}, Response: {MaskPAN(respPan)}"
                    });
                }
            }

            // Important field: DE 37 - RRN must match if present
            if (request.HasField(37) && response.HasField(37))
            {
                var reqRrn = request.GetField(37);
                var respRrn = response.GetField(37);
                if (reqRrn != respRrn)
                {
                    result.AddDataElementResult(new DataElementValidationResult
                    {
                        DataElementNumber = 37,
                        DataElementName = "RRN",
                        IsValid = false,
                        ErrorCode = "30",
                        ErrorMessage = $"RRN mismatch - Request: {reqRrn}, Response: {respRrn}"
                    });
                }
            }

            // Important field: DE 41 - Terminal ID must match
            if (request.HasField(41) && response.HasField(41))
            {
                var reqTerm = request.GetField(41);
                var respTerm = response.GetField(41);
                if (reqTerm != respTerm)
                {
                    result.AddDataElementResult(new DataElementValidationResult
                    {
                        DataElementNumber = 41,
                        DataElementName = "Terminal ID",
                        IsValid = false,
                        ErrorCode = "30",
                        ErrorMessage = $"Terminal ID mismatch - Request: {reqTerm}, Response: {respTerm}"
                    });
                }
            }

            // Important field: DE 42 - Merchant ID must match
            if (request.HasField(42) && response.HasField(42))
            {
                var reqMerch = request.GetField(42);
                var respMerch = response.GetField(42);
                if (reqMerch != respMerch)
                {
                    result.AddDataElementResult(new DataElementValidationResult
                    {
                        DataElementNumber = 42,
                        DataElementName = "Merchant ID",
                        IsValid = false,
                        ErrorCode = "30",
                        ErrorMessage = $"Merchant ID mismatch - Request: {reqMerch}, Response: {respMerch}"
                    });
                }
            }

            // Verify response MTI matches request MTI pattern
            if (!IsValidResponseMTI(request.MessageType, response.MessageType))
            {
                result.AddDataElementResult(new DataElementValidationResult
                {
                    DataElementNumber = 0,
                    DataElementName = "MTI",
                    IsValid = false,
                    ErrorCode = "30",
                    ErrorMessage = $"Invalid response MTI - Request: {request.MessageType}, Response: {response.MessageType}"
                });
            }

            if (result.IsValid)
            {
                SwitchLogger.Info($"[CORRELATION] ? Response matches request - STAN: {reqStan}");
            }
            else
            {
                SwitchLogger.Info($"[CORRELATION] ? Validation failed - STAN: {reqStan}");
                Console.WriteLine(result.GetSummary());
            }

            return result;
        }

        private bool IsValidResponseMTI(string requestMTI, string responseMTI)
        {
            // 0200 (auth req) -> 0210 (auth resp)
            if (requestMTI == "0200" && responseMTI == "0210") return true;
            
            // 0400 (reversal req) -> 0410 (reversal resp)
            if (requestMTI == "0400" && responseMTI == "0410") return true;
            
            // 0800 (network mgmt req) -> 0810 (network mgmt resp)
            if (requestMTI == "0800" && responseMTI == "0810") return true;
            
            return false;
        }

        private string MaskPAN(string? pan)
        {
            if (string.IsNullOrEmpty(pan) || pan.Length < 10)
                return "****";
            return $"{pan[..6]}****{pan[^4..]}";
        }
    }
}

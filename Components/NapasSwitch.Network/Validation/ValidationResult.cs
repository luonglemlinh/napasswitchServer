using System.Collections.Generic;
using System.Linq;

namespace network.Validation
{
    /// <summary>
    /// Result of a NAPAS data element validation
    /// </summary>
    public class DataElementValidationResult
    {
        public int DataElementNumber { get; set; }
        public string DataElementName { get; set; } = string.Empty;
        public bool IsValid { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ActualValue { get; set; }

        public override string ToString()
        {
            return IsValid 
                ? $"DE{DataElementNumber} ({DataElementName}): VALID" 
                : $"DE{DataElementNumber} ({DataElementName}): {ErrorCode} - {ErrorMessage}";
        }
    }

    /// <summary>
    /// Result of a complete ISO message validation
    /// </summary>
    public class MessageValidationResult
    {
        public bool IsValid { get; set; } = true;
        public List<DataElementValidationResult> DataElementResults { get; set; } = new();
        public string? OverallErrorCode { get; set; }
        public string? OverallErrorMessage { get; set; }

        /// <summary>
        /// Get all failed data element validations
        /// </summary>
        public List<DataElementValidationResult> GetErrors()
        {
            return DataElementResults.Where(de => !de.IsValid).ToList();
        }

        /// <summary>
        /// Get the first error code (for response)
        /// </summary>
        public string GetFirstErrorCode()
        {
            var error = DataElementResults.FirstOrDefault(de => !de.IsValid);
            return error?.ErrorCode ?? "96"; // Generic system error
        }

        /// <summary>
        /// Add data element validation result
        /// </summary>
        public void AddDataElementResult(DataElementValidationResult result)
        {
            DataElementResults.Add(result);
            if (!result.IsValid)
                IsValid = false;
        }

        /// <summary>
        /// Get summary of validation
        /// </summary>
        public string GetSummary()
        {
            var errors = GetErrors();
            if (errors.Count == 0)
                return $"Validation PASSED: All {DataElementResults.Count} data elements are valid";
            
            return $"Validation FAILED: {errors.Count} error(s) found:\n" + 
                   string.Join("\n", errors.Select(e => $"  - {e}"));
        }
    }
}
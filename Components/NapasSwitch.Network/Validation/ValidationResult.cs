using System.Collections.Generic;
using System.Linq;

namespace network.Validation
{
    
    /// Result of a NAPAS data element validation
    
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

    
    /// Result of a complete ISO message validation
    
    public class MessageValidationResult
    {
        public bool IsValid { get; set; } = true;
        public List<DataElementValidationResult> DataElementResults { get; set; } = new();
        public string? OverallErrorCode { get; set; }
        public string? OverallErrorMessage { get; set; }

        
        /// Get all failed data element validations
        
        public List<DataElementValidationResult> GetErrors()
        {
            return DataElementResults.Where(de => !de.IsValid).ToList();
        }

        
        /// Get the first error code (for response)
        
        public string GetFirstErrorCode()
        {
            var error = DataElementResults.FirstOrDefault(de => !de.IsValid);
            return error?.ErrorCode ?? "96"; // Generic system error
        }

        
        /// Add data element validation result
        
        public void AddDataElementResult(DataElementValidationResult result)
        {
            DataElementResults.Add(result);
            if (!result.IsValid)
                IsValid = false;
        }

        
        /// Get summary of validation
        
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
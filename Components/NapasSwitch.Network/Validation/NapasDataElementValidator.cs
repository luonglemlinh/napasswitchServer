    using System;
using core.Helpers;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using core.Models;

namespace network.Validation
{
    
    /// NAPAS data element definition with validation rules
    /// Corresponds to technical specification data element definitions
    
    public class NapasDataElementDefinition
    {
        public int Number { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int MinLength { get; set; }
        public int MaxLength { get; set; }
        public string? Pattern { get; set; }
        public string DataType { get; set; } = "AN"; // N=Numeric, AN=Alphanumeric, ANS=Alphanumeric+Special, B=Binary
        public HashSet<string> RequiredIn { get; set; } = new(); // MTIs where this DE is Required
        public string ErrorCode { get; set; } = "30"; // Default: Format error
        public string ErrorMessage { get; set; } = string.Empty;
        public List<string>? AllowedValues { get; set; }
    }

    
    /// Validator for NAPAS ISO-8583 messages
    /// Validates requests and responses according to NAPAS technical specification
    
    public class NapasDataElementValidator
    {
        private readonly Dictionary<int, NapasDataElementDefinition> _dataElementDefinitions;
        private readonly HashSet<string> _validMTIs;

        public NapasDataElementValidator(string? configFilePath = null)
        {
            _dataElementDefinitions = new Dictionary<int, NapasDataElementDefinition>();
            _validMTIs = new HashSet<string>();

            // Load configuration from XML file - REQUIRED
            if (string.IsNullOrEmpty(configFilePath))
                throw new ArgumentException("Configuration file path is required", nameof(configFilePath));

            if (!System.IO.File.Exists(configFilePath))
                throw new System.IO.FileNotFoundException($"NAPAS validation configuration file not found: {configFilePath}");

            LoadConfigurationFromXml(configFilePath);
        }

        
        /// Load NAPAS validation configuration from XML file
        
        private void LoadConfigurationFromXml(string filePath)
        {
            try
            {
                var doc = XDocument.Load(filePath);
                var root = doc.Root;

                if (root == null) return;

                // Load data element definitions
                var dataElements = root.Element("DataElements")?.Elements("DataElement");
                if (dataElements != null)
                {
                    foreach (var de in dataElements)
                    {
                        var definition = new NapasDataElementDefinition
                        {
                            Number = int.Parse(de.Attribute("Number")?.Value ?? "0"),
                            Name = de.Attribute("Name")?.Value ?? "",
                            Description = de.Attribute("Description")?.Value ?? "",
                            MinLength = int.Parse(de.Element("MinLength")?.Value ?? "0"),
                            MaxLength = int.Parse(de.Element("MaxLength")?.Value ?? "0"),
                            DataType = de.Element("DataType")?.Value ?? "AN",
                            Pattern = de.Element("Pattern")?.Value,
                            ErrorCode = de.Element("ErrorCode")?.Value ?? "30",
                            ErrorMessage = de.Element("ErrorMessage")?.Value ?? ""
                        };

                        // Parse RequiredMTIs
                        var RequiredIn = de.Element("RequiredIn")?.Value;
                        if (!string.IsNullOrEmpty(RequiredIn))
                        {
                            definition.RequiredIn = RequiredIn.Split(',')
                                .Select(s => s.Trim())
                                .Where(s => !string.IsNullOrEmpty(s))
                                .ToHashSet();
                        }

                        // Parse allowed values
                        var allowedValues = de.Element("AllowedValues")?.Elements("Value");
                        if (allowedValues != null && allowedValues.Any())
                        {
                            definition.AllowedValues = allowedValues
                                .Select(v => v.Value)
                                .ToList();
                        }

                        _dataElementDefinitions[definition.Number] = definition;
                    }
                }

                // Load valid MTIs
                var mtis = root.Element("MessageTypes")?.Elements("MTI");
                if (mtis != null)
                {
                    foreach (var mti in mtis)
                    {
                        var code = mti.Attribute("Code")?.Value;
                        if (!string.IsNullOrEmpty(code))
                            _validMTIs.Add(code);
                    }
                }

                SwitchLogger.Info($"[NAPAS Validator] Loaded {_dataElementDefinitions.Count} data element definitions from {filePath}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load NAPAS validation configuration from {filePath}: {ex.Message}", ex);
            }
        }

        
        /// Validate complete ISO message according to NAPAS specifications
        
        public MessageValidationResult ValidateMessage(IsoMessage message)
        {
            var result = new MessageValidationResult { IsValid = true };

            // Validate MTI
            var mtiResult = ValidateMessageTypeIndicator(message.MessageType);
            result.AddDataElementResult(mtiResult);

            if (!mtiResult.IsValid)
            {
                result.OverallErrorCode = mtiResult.ErrorCode;
                result.OverallErrorMessage = "Invalid message type";
                return result;
            }

            // Validate each data element present in the message
            foreach (var kvp in message.Fields)
            {
                var deResult = ValidateDataElement(kvp.Key, kvp.Value, message.MessageType);
                result.AddDataElementResult(deResult);
            }

            // Check Requireddata elements
            var RequiredResults = CheckRequiredDataElements(message);
            foreach (var deResult in RequiredResults)
            {
                result.AddDataElementResult(deResult);
            }

            foreach (var deResult in ValidateBalanceInquiryRules(message))
            {
                result.AddDataElementResult(deResult);
            }

            // 3. Custom cross-field validation for DE#35 (Track 2) vs DE#22 (POS Entry Mode)
            if (message.HasField(35) && message.HasField(22))
            {
                string posMode = message.GetField(22)!;
                string track2 = message.GetField(35)!;
                
                // Chip transactions: 05, 07, 91 (per Napas spec prefixes)
                bool isChip = posMode.StartsWith("05") || posMode.StartsWith("07") || posMode.StartsWith("91");
                
                if (isChip)
                {
                    // Match based on ISO 7813 structure: [PAN]D[ED]D[SC][DD]
                    var match = Regex.Match(track2, @"^([0-9]{1,19})D([0-9]{4}|D)([0-9]{3}|D)([0-9D]{0,10})$");
                    if (match.Success)
                    {
                        string scGroup = match.Groups[3].Value;
                        if (!string.IsNullOrEmpty(scGroup) && scGroup != "D")
                        {
                            char scFirst = scGroup[0];
                            if (scFirst != '2' && scFirst != '6')
                            {
                                result.AddDataElementResult(new DataElementValidationResult
                                {
                                    DataElementNumber = 35,
                                    DataElementName = "Track-2 Data",
                                    IsValid = false,
                                    ErrorCode = "30",
                                    ErrorMessage = $"Service Code first digit '{scFirst}' is invalid for chip transaction (must be 2 or 6)"
                                });
                            }
                        }
                    }
                }
            }

            if (!result.IsValid)
            {
                result.OverallErrorCode = result.GetFirstErrorCode();
                result.OverallErrorMessage = $"Validation failed: {result.GetErrors().Count} error(s)";
            }

            return result;
        }

        private IEnumerable<DataElementValidationResult> ValidateBalanceInquiryRules(IsoMessage message)
        {
            var results = new List<DataElementValidationResult>();
            var processingCode = message.GetField(3);
            if (string.IsNullOrEmpty(processingCode))
            {
                return results;
            }

            var amount = message.GetField(4) ?? string.Empty;
            var isBalanceInquiry = processingCode.StartsWith("30", StringComparison.Ordinal);
            var isPurchase = processingCode.StartsWith("00", StringComparison.Ordinal);

            if (isBalanceInquiry)
            {
                if (!IsBalanceInquiryMti(message.MessageType))
                {
                    results.Add(new DataElementValidationResult
                    {
                        DataElementNumber = 0,
                        DataElementName = "MTI",
                        IsValid = false,
                        ErrorCode = "30",
                        ErrorMessage = $"Balance inquiry processing code requires MTI 0100/0110/0200/0210 (actual: {message.MessageType})"
                    });
                }

                if (!string.IsNullOrEmpty(amount) && !IsZeroAmount(amount))
                {
                    results.Add(BuildAmountRuleResult("Balance inquiry must use zero amount"));
                }
            }
            else if (isPurchase)
            {
                if (!string.IsNullOrEmpty(amount) && IsZeroAmount(amount))
                {
                    results.Add(BuildAmountRuleResult("Purchase must use a non-zero amount"));
                }
            }

            return results;
        }

        private static bool IsZeroAmount(string amount)
        {
            return amount.All(c => c == '0');
        }

        private static bool IsBalanceInquiryMti(string mti)
        {
            return mti == "0100" || mti == "0110" || mti == "0200" || mti == "0210";
        }

        private DataElementValidationResult BuildAmountRuleResult(string errorMessage)
        {
            var errorCode = _dataElementDefinitions.TryGetValue(4, out var definition)
                ? definition.ErrorCode
                : "13";

            return new DataElementValidationResult
            {
                DataElementNumber = 4,
                DataElementName = "DE4_Amount",
                IsValid = false,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage
            };
        }

        
        /// Validate a single data element
        
        public DataElementValidationResult ValidateDataElement(int deNumber, string value, string mti)
        {
            if (!_dataElementDefinitions.TryGetValue(deNumber, out var definition))
            {
                // Unknown DE - pass through (not validated)
                return new DataElementValidationResult
                {
                    DataElementNumber = deNumber,
                    DataElementName = $"DE{deNumber}",
                    IsValid = true,
                    ActualValue = MaskSensitiveDataElement(deNumber, value)
                };
            }

            var result = new DataElementValidationResult
            {
                DataElementNumber = deNumber,
                DataElementName = definition.Name,
                IsValid = true,
                ActualValue = MaskSensitiveDataElement(deNumber, value)
            };

            // Check null/empty
            if (string.IsNullOrEmpty(value))
            {
                if (definition.RequiredIn.Contains(mti))
                {
                    result.IsValid = false;
                    result.ErrorCode = definition.ErrorCode;
                    result.ErrorMessage = $"{definition.Name} is Requiredfor MTI {mti}";
                }
                return result;
            }

            // Check length
            if (value.Length < definition.MinLength || value.Length > definition.MaxLength)
            {
                result.IsValid = false;
                result.ErrorCode = definition.ErrorCode;
                result.ErrorMessage = $"{definition.Name} length must be {definition.MinLength}-{definition.MaxLength} (actual: {value.Length})";
                return result;
            }

            // Check data type
            if (!ValidateDataType(value, definition.DataType))
            {
                result.IsValid = false;
                result.ErrorCode = definition.ErrorCode;
                result.ErrorMessage = $"{definition.Name} must be {GetDataTypeDescription(definition.DataType)} format";
                return result;
            }

            // Check pattern
            if (!string.IsNullOrEmpty(definition.Pattern) && !Regex.IsMatch(value, definition.Pattern))
            {
                result.IsValid = false;
                result.ErrorCode = definition.ErrorCode;
                result.ErrorMessage = $"{definition.Name} format is invalid - {definition.ErrorMessage}";
                return result;
            }

            // Check allowed values
            if (definition.AllowedValues != null && definition.AllowedValues.Count > 0)
            {
                if (!definition.AllowedValues.Contains(value))
                {
                    result.IsValid = false;
                    result.ErrorCode = definition.ErrorCode;
                    result.ErrorMessage = $"{definition.Name} has invalid value. Allowed: {string.Join(", ", definition.AllowedValues)}";
                    return result;
                }
            }

            return result;
        }

        
        /// Validate message type indicator
        
        private DataElementValidationResult ValidateMessageTypeIndicator(string mti)
        {
            var result = new DataElementValidationResult
            {
                DataElementNumber = 0,
                DataElementName = "MTI",
                IsValid = true,
                ActualValue = mti
            };

            if (string.IsNullOrEmpty(mti) || mti.Length != 4)
            {
                result.IsValid = false;
                result.ErrorCode = "96";
                result.ErrorMessage = "MTI must be 4 digits";
                return result;
            }

            if (_validMTIs.Count > 0 && !_validMTIs.Contains(mti))
            {
                result.IsValid = false;
                result.ErrorCode = "96";
                result.ErrorMessage = $"Unsupported MTI: {mti}";
            }

            return result;
        }

        
        
        /// Check Required data elements based on MTI
        private List<DataElementValidationResult> CheckRequiredDataElements(IsoMessage message)
        {
            var results = new List<DataElementValidationResult>();
            var mti = message.MessageType;

            // SPECIAL CASE: If this is an error response (RC != 00), relax mandatory field requirements.
            // Many TS/ISS implementations omit mandatory fields when signaling a format or processing error.
            if (mti.EndsWith("10") || mti.EndsWith("30"))
            {
                string? rc = message.GetResponseCode();
                if (!string.IsNullOrEmpty(rc) && rc != "00")
                {
                    // For error responses, only validate fields that are actually present.
                    // Do not flag missing fields.
                    SwitchLogger.Info($"[VALIDATOR] Relaxed validation applied for response with RC {rc}. Mandatory field checks skipped.");
                    return results; 
                }
            }

            // Dynamic validation based on XML configuration (RequiredIn tags)
            foreach (var kvp in _dataElementDefinitions)
            {
                var definition = kvp.Value;
                if (definition.RequiredIn.Contains(mti))
                {
                    if (!message.Fields.ContainsKey(definition.Number))
                    {
                        results.Add(new DataElementValidationResult
                        {
                            DataElementNumber = definition.Number,
                            DataElementName = definition.Name,
                            IsValid = false,
                            ErrorCode = definition.ErrorCode,
                            ErrorMessage = $"Required DE{definition.Number} ({definition.Name}) is missing for MTI {mti}"
                        });
                    }
                }
            }

            return results;
        }

        private DataElementValidationResult BuildMissingResult(int deNumber, string mti)
        {
            if (_dataElementDefinitions.TryGetValue(deNumber, out var definition))
            {
                return new DataElementValidationResult
                {
                    DataElementNumber = deNumber,
                    DataElementName = definition.Name,
                    IsValid = false,
                    ErrorCode = definition.ErrorCode,
                    ErrorMessage = $"RequiredDE{deNumber} ({definition.Name}) is missing for MTI {mti}"
                };
            }

            return new DataElementValidationResult
            {
                DataElementNumber = deNumber,
                DataElementName = $"DE{deNumber}",
                IsValid = false,
                ErrorCode = "30",
                ErrorMessage = $"RequiredDE{deNumber} is missing for MTI {mti}"
            };
        }
        
        /// Validate data type (N=Numeric, AN=Alphanumeric, ANS=Alphanumeric+Special, B=Binary/Hex)
        
        private bool ValidateDataType(string value, string dataType)
        {
            return dataType switch
            {
                "N" => Regex.IsMatch(value, @"^[0-9]+$"),
                "AN" => Regex.IsMatch(value, @"^[a-zA-Z0-9]+$"),
                "ANS" => Regex.IsMatch(value, @"^[a-zA-Z0-9\s\.,\-/]+$"),
                "B" => Regex.IsMatch(value, @"^[0-9A-F]+$", RegexOptions.IgnoreCase), // Binary as hex
                "Z" => Regex.IsMatch(value, @"^[0-9D=]+$", RegexOptions.IgnoreCase), // Track data (allows D or = separator)
                _ => true
            };
        }

        
        /// Get human-readable data type description
        
        private string GetDataTypeDescription(string dataType)
        {
            return dataType switch
            {
                "N" => "numeric",
                "AN" => "alphanumeric",
                "ANS" => "alphanumeric with special characters",
                "B" => "binary/hexadecimal",
                "Z" => "track data",
                _ => dataType
            };
        }

        
        /// Mask sensitive data element values for logging (PAN, PIN, Track data)
        
        private string MaskSensitiveDataElement(int deNumber, string value)
        {
            if (deNumber == 2 && value.Length >= 10) // DE2: PAN
            {
                return $"{value.Substring(0, 6)}****{value.Substring(value.Length - 4)}";
            }
            if (deNumber == 35 || deNumber == 36) // DE35/36: Track 2/3 data
            {
                return "****TRACK_DATA****";
            }
            if (deNumber == 52) // DE52: PIN block
            {
                return "****PIN_BLOCK****";
            }
            return value;
        }

        
        /// Get data element definition by number
        
        public NapasDataElementDefinition? GetDataElementDefinition(int deNumber)
        {
            _dataElementDefinitions.TryGetValue(deNumber, out var definition);
            return definition;
        }

        
        /// Get all data element definitions
        
        public Dictionary<int, NapasDataElementDefinition> GetAllDataElementDefinitions()
        {
            return _dataElementDefinitions;
        }
    }
}
using System;
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

                Console.WriteLine($"[NAPAS Validator] Loaded {_dataElementDefinitions.Count} data element definitions from {filePath}");
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

            if (!result.IsValid)
            {
                result.OverallErrorCode = result.GetFirstErrorCode();
                result.OverallErrorMessage = $"Validation failed: {result.GetErrors().Count} error(s)";
            }

            return result;
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

        
        
        /// Check Requireddata elements based on MTI
        
        private List<DataElementValidationResult> CheckRequiredDataElements(IsoMessage message)
        {
            var results = new List<DataElementValidationResult>();
            var mti = message.MessageType;

            // Base required fields for financial transactions (0200/0400)
            // Only enforce truly essential fields - let config handle the rest
            if (mti == "0200" || mti == "0400")
            {
                // These are essential for routing and processing
                int[] baseRequired = { 2, 3, 4, 11 }; // PAN, ProcessingCode, Amount, STAN
                foreach (var de in baseRequired)
                {
                    if (!message.Fields.ContainsKey(de))
                    {
                        results.Add(BuildMissingResult(de, mti));
                    }
                }
            }

            // Track-2 related requirements: enforce when DE35 (Track 2) is present
            if (message.Fields.ContainsKey(35))
            {
                int[] trackRequired = { 14, 35, 52 };
                foreach (var de in trackRequired)
                {
                    if (!message.Fields.ContainsKey(de))
                    {
                        results.Add(BuildMissingResult(de, mti));
                    }
                }
            }

            // Existing configuration-based RequiredIn rules
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
                            ErrorMessage = $"RequiredDE{definition.Number} ({definition.Name}) is missing for MTI {mti}"
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
                "Z" => Regex.IsMatch(value, @"^[0-9]+$"), // Track 2 data
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
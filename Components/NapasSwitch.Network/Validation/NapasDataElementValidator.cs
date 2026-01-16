using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using core.Models;

namespace network.Validation
{
    /// <summary>
    /// NAPAS data element definition with validation rules
    /// Corresponds to technical specification data element definitions
    /// </summary>
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

    /// <summary>
    /// Validator for NAPAS ISO-8583 messages
    /// Validates requests and responses according to NAPAS technical specification
    /// </summary>
    public class NapasDataElementValidator
    {
        private readonly Dictionary<int, NapasDataElementDefinition> _dataElementDefinitions;
        private readonly HashSet<string> _validMTIs;

        public NapasDataElementValidator(string? configFilePath = null)
        {
            _dataElementDefinitions = new Dictionary<int, NapasDataElementDefinition>();
            _validMTIs = new HashSet<string>();

            // Load configuration from XML file
            if (!string.IsNullOrEmpty(configFilePath) && System.IO.File.Exists(configFilePath))
            {
                LoadConfigurationFromXml(configFilePath);
            }
            else
            {
                // Fallback to hardcoded configuration
                InitializeDefaultConfiguration();
            }
        }

        /// <summary>
        /// Load NAPAS validation configuration from XML file
        /// </summary>
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
                Console.WriteLine($"[NAPAS Validator] Error loading config: {ex.Message}");
                InitializeDefaultConfiguration();
            }
        }

        /// <summary>
        /// Initialize default hardcoded configuration (fallback)
        /// </summary>
        private void InitializeDefaultConfiguration()
        {
            // Critical NAPAS data elements - minimum required for operation
            _dataElementDefinitions[2] = new NapasDataElementDefinition
            {
                Number = 2, Name = "DE2_PAN", Description = "Primary Account Number",
                MinLength = 13, MaxLength = 19, DataType = "N", Pattern = @"^[0-9]{13,19}$",
                RequiredIn = new HashSet<string> { "0200", "0210", "0400", "0410" },
                ErrorCode = "14", ErrorMessage = "Invalid card number"
            };

            _dataElementDefinitions[3] = new NapasDataElementDefinition
            {
                Number = 3, Name = "DE3_ProcessingCode", Description = "Processing Code",
                MinLength = 6, MaxLength = 6, DataType = "N", Pattern = @"^[0-9]{6}$",
                RequiredIn = new HashSet<string> { "0200", "0210", "0400", "0410" },
                ErrorCode = "40", ErrorMessage = "Invalid processing code"
            };

            _dataElementDefinitions[4] = new NapasDataElementDefinition
            {
                Number = 4, Name = "DE4_Amount", Description = "Transaction Amount",
                MinLength = 12, MaxLength = 12, DataType = "N", Pattern = @"^[0-9]{12}$",
                RequiredIn = new HashSet<string> { "0200", "0210", "0400", "0410" },
                ErrorCode = "13", ErrorMessage = "Invalid amount"
            };

            _dataElementDefinitions[11] = new NapasDataElementDefinition
            {
                Number = 11, Name = "DE11_STAN", Description = "System Trace Audit Number",
                MinLength = 6, MaxLength = 6, DataType = "N", Pattern = @"^[0-9]{6}$",
                RequiredIn = new HashSet<string> { "0200", "0210", "0400", "0410", "0800", "0810" },
                ErrorCode = "96", ErrorMessage = "Invalid STAN"
            };

            _dataElementDefinitions[12] = new NapasDataElementDefinition
            {
                Number = 12, Name = "DE12_LocalTime", Description = "Local Time hhmmss",
                MinLength = 6, MaxLength = 6, DataType = "N", Pattern = @"^[0-9]{6}$",
                RequiredIn = new HashSet<string> { "0200", "0210", "0400", "0410" },
                ErrorCode = "30", ErrorMessage = "Invalid local time"
            };

            _dataElementDefinitions[13] = new NapasDataElementDefinition
            {
                Number = 13, Name = "DE13_LocalDate", Description = "Local Date MMDD",
                MinLength = 4, MaxLength = 4, DataType = "N", Pattern = @"^[0-9]{4}$",
                RequiredIn = new HashSet<string> { "0200", "0210", "0400", "0410" },
                ErrorCode = "30", ErrorMessage = "Invalid local date"
            };

            _dataElementDefinitions[39] = new NapasDataElementDefinition
            {
                Number = 39, Name = "DE39_ResponseCode", Description = "Response Code",
                MinLength = 2, MaxLength = 2, DataType = "N", Pattern = @"^[0-9]{2}$",
                RequiredIn = new HashSet<string> { "0210", "0410", "0810" },
                ErrorCode = "96", ErrorMessage = "Invalid response code"
            };

            _dataElementDefinitions[41] = new NapasDataElementDefinition
            {
                Number = 41, Name = "DE41_TerminalID", Description = "Terminal ID",
                MinLength = 8, MaxLength = 8, DataType = "AN", Pattern = @"^[a-zA-Z0-9]{8}$",
                RequiredIn = new HashSet<string> { "0200", "0210", "0400", "0410" },
                ErrorCode = "03", ErrorMessage = "Invalid terminal ID"
            };

            _dataElementDefinitions[42] = new NapasDataElementDefinition
            {
                Number = 42, Name = "DE42_MerchantID", Description = "Merchant ID",
                MinLength = 15, MaxLength = 15, DataType = "AN", Pattern = @"^[a-zA-Z0-9\s]{15}$",
                RequiredIn = new HashSet<string> { "0200", "0210", "0400", "0410" },
                ErrorCode = "03", ErrorMessage = "Invalid merchant ID"
            };

            // Valid MTIs - populate without reassigning readonly field
            _validMTIs.Clear();
            _validMTIs.UnionWith(new[] { "0200", "0210", "0400", "0410", "0420", "0430", "0800", "0810" });

            Console.WriteLine($"[NAPAS Validator] Initialized with {_dataElementDefinitions.Count} default data element definitions");
        }

        /// <summary>
        /// Validate complete ISO message according to NAPAS specifications
        /// </summary>
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

        /// <summary>
        /// Validate a single data element
        /// </summary>
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

        /// <summary>
        /// Validate message type indicator
        /// </summary>
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

        /// <summary>
        /// Check Requireddata elements based on MTI
        /// </summary>
        private List<DataElementValidationResult> CheckRequiredDataElements(IsoMessage message)
        {
            var results = new List<DataElementValidationResult>();
            var mti = message.MessageType;

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

        /// <summary>
        /// Validate data type (N=Numeric, AN=Alphanumeric, ANS=Alphanumeric+Special, B=Binary/Hex)
        /// </summary>
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

        /// <summary>
        /// Get human-readable data type description
        /// </summary>
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

        /// <summary>
        /// Mask sensitive data element values for logging (PAN, PIN, Track data)
        /// </summary>
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

        /// <summary>
        /// Get data element definition by number
        /// </summary>
        public NapasDataElementDefinition? GetDataElementDefinition(int deNumber)
        {
            _dataElementDefinitions.TryGetValue(deNumber, out var definition);
            return definition;
        }

        /// <summary>
        /// Get all data element definitions
        /// </summary>
        public Dictionary<int, NapasDataElementDefinition> GetAllDataElementDefinitions()
        {
            return _dataElementDefinitions;
        }
    }
}
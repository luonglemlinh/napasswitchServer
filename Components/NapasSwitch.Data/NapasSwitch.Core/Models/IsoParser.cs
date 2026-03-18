using core.Helpers;
using core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace core.ISO8583;


/// Parses ISO 8583 messages into IsoMessage objects
/// Supports expandable field schema

public class IsoParser
{
    private readonly IsoSchema _schema;

    public IsoParser(IsoSchema? schema = null)
    {
        _schema = schema ?? IsoSchema.GetNapasSchema(); 
    }

    
    /// Parse ISO message from hex string
    /// Example: "0200F23C048108A18000000000040000000..." 
    
    public IsoMessage Parse(string hexMessage)
    {
        var bytes = HexStringToBytes(hexMessage);
        return Parse(bytes);
    }

    
    /// Parse ISO message from byte array
    
    public IsoMessage Parse(byte[] messageBytes)
    {
        if (messageBytes.Length < 4)
            throw new ArgumentException("Invalid ISO message: too short");

        int offset = 0;
        string header = "";

        // Detect Header (F00)  
        // MTIs usually start with '0' (0200, 0400, 0800, etc.)
        for (int i = 0; i <= Math.Min(messageBytes.Length - 4, 32); i++)
        {
            if (char.IsDigit((char)messageBytes[i]) && 
                char.IsDigit((char)messageBytes[i+1]) &&
                char.IsDigit((char)messageBytes[i+2]) &&
                char.IsDigit((char)messageBytes[i+3]))
            {
                // Found potential MTI at index i
                if (i > 0)
                {
                    header = Encoding.ASCII.GetString(messageBytes, 0, i);
                    offset = i;
                }
                break;
            }
        }

        // 1. Extract Message Type (4 characters)
        string messageType = ExtractMessageType(messageBytes, ref offset);

        // 2. Parse Bitmap (8 bytes primary, optionally 8 bytes secondary)
        var bitmapInfo = ParseBitmap(messageBytes, ref offset);

        var fields = ExtractFields(messageBytes, bitmapInfo.Bitmap, ref offset);

        // Add F00 and F01 to dictionary for completeness
        if (!string.IsNullOrEmpty(header)) fields[0] = header;
        fields[1] = bitmapInfo.PrimaryHex;

        return new IsoMessage
        {
            MessageType = messageType,
            Header = header,
            PrimaryBitmap = bitmapInfo.PrimaryHex,
            SecondaryBitmap = bitmapInfo.SecondaryHex,
            Fields = fields
        };
    }

    private record BitmapParseResult(bool[] Bitmap, string PrimaryHex, string SecondaryHex);

    private string ExtractMessageType(byte[] data, ref int offset)
    {
        string msgType = System.Text.Encoding.ASCII.GetString(data, offset, 4);
        offset += 4;
        return msgType;
    }

    
    
    /// Parse bitmap - supports both BINARY (8 bytes) and HEX (16 ASCII chars) formats
    /// If bit 1 is set, secondary bitmap follows (additional 8 bytes/16 chars for fields 65-128)
    
    private BitmapParseResult ParseBitmap(byte[] data, ref int offset)
    {
        var bitmap = new bool[128]; // Support up to 128 fields
        string primaryHex = "";
        string secondaryHex = "";

        // Detect if bitmap is in HEX format (16 ASCII hex characters) or BINARY format (8 bytes)
        bool isHexFormat = IsHexBitmap(data, offset);
        byte[] primaryBitmap;
        
        if (isHexFormat)
        {
            primaryHex = Encoding.ASCII.GetString(data, offset, 16);
            primaryBitmap = HexStringToBytes(primaryHex);
            offset += 16;
        }
        else
        {
            primaryBitmap = new byte[8];
            Array.Copy(data, offset, primaryBitmap, 0, 8);
            primaryHex = BitConverter.ToString(primaryBitmap).Replace("-", "");
            offset += 8;
        }

        // Convert primary bitmap to bits
        for (int i = 0; i < 64; i++)
        {
            int byteIndex = i / 8;
            int bitIndex = 7 - (i % 8);
            bitmap[i] = ((primaryBitmap[byteIndex] >> bitIndex) & 1) == 1;
        }

        // Check if secondary bitmap is present (bit 1 / field 1 = first bit)
        if (bitmap[0])
        {
            byte[] secondaryBitmap;
            
            if (isHexFormat)
            {
                secondaryHex = Encoding.ASCII.GetString(data, offset, 16);
                secondaryBitmap = HexStringToBytes(secondaryHex);
                offset += 16;
            }
            else
            {
                secondaryBitmap = new byte[8];
                Array.Copy(data, offset, secondaryBitmap, 0, 8);
                secondaryHex = BitConverter.ToString(secondaryBitmap).Replace("-", "");
                offset += 8;
            }

            for (int i = 0; i < 64; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = 7 - (i % 8);
                bitmap[64 + i] = ((secondaryBitmap[byteIndex] >> bitIndex) & 1) == 1;
            }
        }

        return new BitmapParseResult(bitmap, primaryHex, secondaryHex);
    }

    /// <summary>
    /// Check if the bitmap at the given offset is in HEX ASCII format
    /// HEX format uses characters 0-9, A-F, a-f
    /// BINARY format uses raw bytes (often containing non-printable characters)
    /// </summary>
    private bool IsHexBitmap(byte[] data, int offset)
    {
        if (offset + 16 > data.Length)
            return false; // Not enough bytes for HEX format, assume binary
        
        // Check if all 16 bytes are valid HEX ASCII characters
        for (int i = 0; i < 16; i++)
        {
            byte b = data[offset + i];
            bool isHexChar = (b >= '0' && b <= '9') || 
                             (b >= 'A' && b <= 'F') || 
                             (b >= 'a' && b <= 'f');
            if (!isHexChar)
                return false;
        }
        
        return true;
    }

    
    /// Extract field values based on bitmap and schema
    
    private Dictionary<int, string> ExtractFields(byte[] data, bool[] bitmap, ref int offset)
    {
        var fields = new Dictionary<int, string>();

        for (int fieldNum = 2; fieldNum < 128; fieldNum++) // Field 1 is bitmap
        {
            if (!bitmap[fieldNum - 1])
                continue; // Field not present

            var fieldDef = _schema.GetField(fieldNum);
            if (fieldDef == null)
            {
                // Cannot determine field length without schema definition;
                // all subsequent field offsets would be corrupted if we continue.
                SwitchLogger.Info($"[PARSER-WARN] Field DE#{fieldNum} present in bitmap but NOT in schema - stopping field extraction to prevent offset corruption");
                break;
            }

            string value = ExtractFieldValue(data, fieldDef, ref offset);
            fields[fieldNum] = value;
        }

        return fields;
    }

    
    /// Extract a single field value based on its definition
    
    private string ExtractFieldValue(byte[] data, IsoFieldDefinition fieldDef, ref int offset)
    {
        return fieldDef.Type switch
        {
            FieldType.Fixed => ExtractFixedField(data, fieldDef, ref offset),
            FieldType.Variable => ExtractVariableField(data, fieldDef, ref offset),
            _ => throw new InvalidOperationException($"Unknown field type: {fieldDef.Type}")
        };
    }

    private string ExtractFixedField(byte[] data, IsoFieldDefinition fieldDef, ref int offset)
    {
        int length = fieldDef.FixedLength!.Value;
        if (offset + length > data.Length)
            throw new ArgumentException($"Insufficient data for fixed field DE#{fieldDef.FieldNumber}. Expected {length} bytes at offset {offset}, but only {data.Length - offset} bytes remain.");

        string value;
        if (fieldDef.IsBinary)
        {
            // Binary data (like PIN block) is converted to HEX string internally
            value = BitConverter.ToString(data, offset, length).Replace("-", "");
        }
        else
        {
            value = System.Text.Encoding.ASCII.GetString(data, offset, length);
        }
        
        offset += length;
        return value;
    }

    private string ExtractVariableField(byte[] data, IsoFieldDefinition fieldDef, ref int offset)
    {
        int digits = fieldDef.LengthEncoding switch
        {
            LengthEncoding.LLVAR => 2,
            LengthEncoding.LLLVAR => 3,
            _ => throw new InvalidOperationException($"Unknown encoding: {fieldDef.LengthEncoding}")
        };

        if (offset + digits > data.Length)
            throw new ArgumentException($"Insufficient data for variable field DE#{fieldDef.FieldNumber} length prefix at offset {offset}");

        int fieldLength = 0;
        bool isBinaryLength = false;

        // Try reading as ASCII digits first (standard NAPAS)
        for (int i = 0; i < digits; i++)
        {
            byte b = data[offset + i];
            if (b < '0' || b > '9')
            {
                isBinaryLength = true;
                break;
            }
            fieldLength = fieldLength * 10 + (b - '0');
        }

        if (isBinaryLength)
        {
            // FALLBACK: If NOT ASCII digits, try interpreting as BINARY length (Mastercard/Visa style)
            // LLLVAR usually uses 2 bytes for binary length (e.g. 0x0206 = 518)
            // LLVAR usually uses 1 byte
            int binaryLenSize = fieldDef.LengthEncoding == LengthEncoding.LLLVAR ? 2 : 1;
            
            fieldLength = 0;
            for (int i = 0; i < binaryLenSize; i++)
            {
                fieldLength = (fieldLength << 8) | data[offset + i];
            }
            
            SwitchLogger.Info($"[PARSER] DE#{fieldDef.FieldNumber} using binary length: {fieldLength} (at offset {offset})");
            offset += binaryLenSize;
        }
        else
        {
            offset += digits;
        }

        if (offset + fieldLength > data.Length)
             throw new ArgumentException($"Insufficient data for variable field DE#{fieldDef.FieldNumber} content. Expected {fieldLength} bytes at offset {offset}");

        string value;
        if (fieldDef.IsBinary)
        {
             value = BitConverter.ToString(data, offset, fieldLength).Replace("-", "");
        }
        else
        {
            value = Encoding.ASCII.GetString(data, offset, fieldLength);
        }
        
        offset += fieldLength;
        return value;
    }

    private static byte[] HexStringToBytes(string hex)
    {
        if (hex.Length % 2 != 0)
            throw new ArgumentException("Hex string must have even length");

        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < hex.Length; i += 2)
        {
            bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
        }
        return bytes;
    }

    public bool UseHexBitmap { get; set; } = false;

    public byte[] Build(IsoMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.MessageType))
            throw new ArgumentException("MessageType is required", nameof(message));

        var bytes = new List<byte>();

        // 1. Message Type
        bytes.AddRange(Encoding.ASCII.GetBytes(message.MessageType));

        // 2. Bitmap - only set bits for fields that exist in the schema
        var bitmap = new bool[128];
        foreach (var fieldNum in message.Fields.Keys)
        {
            if (fieldNum < 2 || fieldNum > 128) continue;
            if (_schema.GetField(fieldNum) == null) continue; // Skip fields without schema definition
            bitmap[fieldNum - 1] = true;
            if (fieldNum > 64) bitmap[0] = true; // indicate secondary bitmap
        }

        // Primary bitmap
        bytes.AddRange(BuildBitmapSegment(bitmap, 0));

        // Secondary bitmap if present
        if (bitmap[0])
            bytes.AddRange(BuildBitmapSegment(bitmap, 64));

        // 3. Fields
        for (int fieldNum = 2; fieldNum <= 128; fieldNum++)
        {
            if (!bitmap[fieldNum - 1]) continue;
            var fieldDef = _schema.GetField(fieldNum);
            if (fieldDef == null) continue;
            if (!message.Fields.TryGetValue(fieldNum, out var value) || value is null)
                continue;

            bytes.AddRange(BuildFieldValue(fieldDef, value));
        }

        return bytes.ToArray();
    }

    private IEnumerable<byte> BuildBitmapSegment(bool[] bitmap, int offset)
    {
        // Build bitmap as BINARY (8 raw bytes)
        var result = new byte[8];
        for (int i = 0; i < 64; i++)
        {
            int bitIndex = offset + i;
            int byteIndex = i / 8;
            int bitPos = 7 - (i % 8);
            if (bitmap[bitIndex])
                result[byteIndex] |= (byte)(1 << bitPos);
        }

        if (UseHexBitmap)
        {
            // Convert 8 binary bytes to 16 ASCII Hex chars
            string hex = BitConverter.ToString(result).Replace("-", "");
            return Encoding.ASCII.GetBytes(hex);
        }

        return result;
    }

    private IEnumerable<byte> BuildFieldValue(IsoFieldDefinition fieldDef, string value)
    {
        return fieldDef.Type switch
        {
            FieldType.Fixed => BuildFixedField(fieldDef, value),
            FieldType.Variable => BuildVariableField(fieldDef, value),
            _ => throw new InvalidOperationException($"Unknown field type: {fieldDef.Type}")
        };
    }

    private IEnumerable<byte> BuildFixedField(IsoFieldDefinition fieldDef, string value)
    {
        int length = fieldDef.FixedLength!.Value;
        var val = value ?? string.Empty;

        if (fieldDef.IsBinary)
        {
            // Internal HEX string -> Wire Binary bytes
            byte[] binary;
            try
            {
                binary = HexStringToBytes(val);
            }
            catch
            {
                // If not valid hex, fallback to empty/padding or throw
                binary = new byte[length];
            }

            if (binary.Length < length)
            {
                var padded = new byte[length];
                Array.Copy(binary, 0, padded, 0, binary.Length);
                return padded;
            }
            return binary.Take(length);
        }

        if (val.Length < length)
            val = val.PadRight(length, ' ');
        else if (val.Length > length)
            val = val.Substring(0, length);
            
        return Encoding.ASCII.GetBytes(val);
    }

    /// <summary>
    /// Builds a variable-length field with LLVAR or LLLVAR encoding per NAPAS specification
    /// LLVAR: 2-byte length prefix (zero-padded) + variable data
    /// LLLVAR: 3-byte length prefix (zero-padded) + variable data
    /// 
    /// Example for DE#32 with LLVAR:
    ///   Input: "970418" (6 chars)
    ///   Length: 6 → "06" (2 bytes, zero-padded)
    ///   Output: "06970418" (8 bytes total)
    /// </summary>
    private IEnumerable<byte> BuildVariableField(IsoFieldDefinition fieldDef, string value)
    {
        var val = value ?? string.Empty;
        byte[] contentBytes;
        int length;

        if (fieldDef.IsBinary)
        {
            try
            {
                contentBytes = HexStringToBytes(val);
            }
            catch
            {
                contentBytes = Array.Empty<byte>();
            }
            length = contentBytes.Length;
        }
        else
        {
            contentBytes = Encoding.ASCII.GetBytes(val);
            length = contentBytes.Length;
        }

        string prefix = fieldDef.LengthEncoding switch
        {
            LengthEncoding.LLVAR => length.ToString("D2"),
            LengthEncoding.LLLVAR => length.ToString("D3"),
            _ => throw new InvalidOperationException($"Unknown encoding: {fieldDef.LengthEncoding}")
        };

        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes(prefix));
        bytes.AddRange(contentBytes);
        return bytes;
    }
}
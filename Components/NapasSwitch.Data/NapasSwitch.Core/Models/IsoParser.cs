using core.Const;
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

        // 1. Extract Message Type (4 characters = 2 bytes in BCD)
        string messageType = ExtractMessageType(messageBytes, ref offset);

        // 2. Parse Bitmap (8 bytes primary, optionally 8 bytes secondary)
        bool[] bitmap = ParseBitmap(messageBytes, ref offset);

        // 3. Extract fields based on bitmap
        var fields = ExtractFields(messageBytes, bitmap, ref offset);

        return new IsoMessage
        {
            MessageType = messageType,
            Fields = fields
        };
    }

    private string ExtractMessageType(byte[] data, ref int offset)
    {
        string msgType = System.Text.Encoding.ASCII.GetString(data, offset, 4);
        offset += 4;
        return msgType;
    }

    
    
    /// Parse bitmap - supports both BINARY (8 bytes) and HEX (16 ASCII chars) formats
    /// If bit 1 is set, secondary bitmap follows (additional 8 bytes/16 chars for fields 65-128)
    
    private bool[] ParseBitmap(byte[] data, ref int offset)
    {
        var bitmap = new bool[128]; // Support up to 128 fields

        // Detect if bitmap is in HEX format (16 ASCII hex characters) or BINARY format (8 bytes)
        // HEX format: characters are 0-9, A-F, a-f
        bool isHexFormat = IsHexBitmap(data, offset);

        byte[] primaryBitmap;
        
        if (isHexFormat)
        {
            // HEX format: 16 ASCII characters representing 8 bytes
            string hexStr = Encoding.ASCII.GetString(data, offset, 16);
            primaryBitmap = HexStringToBytes(hexStr);
            offset += 16;
        }
        else
        {
            // BINARY format: 8 raw bytes
            primaryBitmap = new byte[8];
            Array.Copy(data, offset, primaryBitmap, 0, 8);
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
                string hexStr = Encoding.ASCII.GetString(data, offset, 16);
                secondaryBitmap = HexStringToBytes(hexStr);
                offset += 16;
            }
            else
            {
                secondaryBitmap = new byte[8];
                Array.Copy(data, offset, secondaryBitmap, 0, 8);
                offset += 8;
            }

            for (int i = 0; i < 64; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = 7 - (i % 8);
                bitmap[64 + i] = ((secondaryBitmap[byteIndex] >> bitIndex) & 1) == 1;
            }
        }

        return bitmap;
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
                continue; // Field not defined in schema

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
            FieldType.Fixed => ExtractFixedField(data, fieldDef.FixedLength!.Value, ref offset),
            FieldType.Variable => ExtractVariableField(data, fieldDef.LengthEncoding, ref offset),
            _ => throw new InvalidOperationException($"Unknown field type: {fieldDef.Type}")
        };
    }

    private string ExtractFixedField(byte[] data, int length, ref int offset)
    {
        string value = System.Text.Encoding.ASCII.GetString(data, offset, length);
        offset += length;
        return value;
    }

    private string ExtractVariableField(byte[] data, LengthEncoding encoding, ref int offset)
    {
            int asciiLenDigits = encoding switch
            {
                LengthEncoding.LLVAR => 2,
                LengthEncoding.LLLVAR => 3,
                _ => throw new InvalidOperationException($"Unknown encoding: {encoding}")
            };

            int fieldLength;

            // First try ASCII length prefix
            if (TryParseAsciiLength(data, offset, asciiLenDigits, out fieldLength))
            {
                offset += asciiLenDigits;
            }
            else if (TryParsePackedBcdLength(data, offset, asciiLenDigits, out fieldLength, out int bytesConsumed))
            {
                offset += bytesConsumed;
            }
            else
            {
                string lengthStr = System.Text.Encoding.ASCII.GetString(data, offset, Math.Min(asciiLenDigits, data.Length - offset));
                throw new InvalidOperationException($"Invalid length prefix: {lengthStr}");
            }

        string value = System.Text.Encoding.ASCII.GetString(data, offset, fieldLength);
        offset += fieldLength;
        return value;
    }

        private static bool TryParseAsciiLength(byte[] data, int offset, int digits, out int value)
        {
            value = 0;
            if (offset + digits > data.Length) return false;
            for (int i = 0; i < digits; i++)
            {
                byte b = data[offset + i];
                if (b < '0' || b > '9') return false;
                value = value * 10 + (b - '0');
            }
            return true;
        }

        private static bool TryParsePackedBcdLength(byte[] data, int offset, int digits, out int value, out int bytesConsumed)
        {
            value = 0;
            bytesConsumed = 0;

            // Packed BCD: each nibble is a digit. For LLVAR (2 digits) -> 1 byte. For LLLVAR (3 digits) -> 2 bytes (use first 3 nibbles).
            int requiredNibbles = digits;
            int requiredBytes = (requiredNibbles + 1) / 2;

            if (offset + requiredBytes > data.Length) return false;

            int nibblesRead = 0;
            for (int i = 0; i < requiredBytes; i++)
            {
                byte b = data[offset + i];
                byte high = (byte)((b >> 4) & 0x0F);
                byte low = (byte)(b & 0x0F);

                if (nibblesRead < requiredNibbles)
                {
                    if (high > 9) return false;
                    value = value * 10 + high;
                    nibblesRead++;
                }
                if (nibblesRead < requiredNibbles)
                {
                    if (low > 9) return false;
                    value = value * 10 + low;
                    nibblesRead++;
                }
            }

            if (nibblesRead != requiredNibbles) return false;

            bytesConsumed = requiredBytes;
            return true;
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

        // 2. Bitmap
        var bitmap = new bool[128];
        foreach (var fieldNum in message.Fields.Keys)
        {
            if (fieldNum < 1 || fieldNum > 128) continue;
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
            FieldType.Fixed => BuildFixedField(fieldDef.FixedLength!.Value, value),
            FieldType.Variable => BuildVariableField(fieldDef.LengthEncoding, value),
            _ => throw new InvalidOperationException($"Unknown field type: {fieldDef.Type}")
        };
    }

    private IEnumerable<byte> BuildFixedField(int length, string value)
    {
        var val = value ?? string.Empty;
        if (val.Length < length)
            val = val.PadRight(length, ' ');
        else if (val.Length > length)
            val = val.Substring(0, length);
        return Encoding.ASCII.GetBytes(val);
    }

    private IEnumerable<byte> BuildVariableField(LengthEncoding encoding, string value)
    {
        var val = value ?? string.Empty;
        int length = val.Length;
        string prefix = encoding switch
        {
            LengthEncoding.LLVAR => length.ToString("D2"),
            LengthEncoding.LLLVAR => length.ToString("D3"),
            _ => throw new InvalidOperationException($"Unknown encoding: {encoding}")
        };

        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes(prefix));
        bytes.AddRange(Encoding.ASCII.GetBytes(val));
        return bytes;
    }
}
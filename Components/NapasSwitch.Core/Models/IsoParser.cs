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

    
    /// Parse bitmap (8 bytes = 64 fields)
    /// If bit 1 is set, secondary bitmap follows (additional 8 bytes for fields 65-128)
    
    private bool[] ParseBitmap(byte[] data, ref int offset)
    {
        var bitmap = new bool[128]; // Support up to 128 fields

        // Primary bitmap (8 bytes)
        byte[] primaryBitmap = new byte[8];
        Array.Copy(data, offset, primaryBitmap, 0, 8);
        offset += 8;

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
            byte[] secondaryBitmap = new byte[8];
            Array.Copy(data, offset, secondaryBitmap, 0, 8);
            offset += 8;

            for (int i = 0; i < 64; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = 7 - (i % 8);
                bitmap[64 + i] = ((secondaryBitmap[byteIndex] >> bitIndex) & 1) == 1;
            }
        }

        return bitmap;
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
        int lengthPrefix = encoding switch
        {
            LengthEncoding.LLVAR => 2,
            LengthEncoding.LLLVAR => 3,
            _ => throw new InvalidOperationException($"Unknown encoding: {encoding}")
        };

        string lengthStr = System.Text.Encoding.ASCII.GetString(data, offset, lengthPrefix);
        offset += lengthPrefix;

        if (!int.TryParse(lengthStr, out int fieldLength))
            throw new InvalidOperationException($"Invalid length prefix: {lengthStr}");

        string value = System.Text.Encoding.ASCII.GetString(data, offset, fieldLength);
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
        var result = new byte[8];
        for (int i = 0; i < 64; i++)
        {
            int bitIndex = offset + i;
            int byteIndex = i / 8;
            int bitPos = 7 - (i % 8);
            if (bitmap[bitIndex])
                result[byteIndex] |= (byte)(1 << bitPos);
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
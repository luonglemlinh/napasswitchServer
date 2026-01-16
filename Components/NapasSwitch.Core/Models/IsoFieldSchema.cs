using System.Collections.Generic;

namespace core.ISO8583
{
    public class IsoFieldDefinition
    {
        public int FieldNumber { get; set; }
        public string Description { get; set; } = string.Empty;
        public FieldType Type { get; set; }
        public int? FixedLength { get; set; }
        public int? MaxLength { get; set; }
        public LengthEncoding LengthEncoding { get; set; } = LengthEncoding.LLVAR;

        public IsoFieldDefinition() { }

        public IsoFieldDefinition(int fieldNumber, string description, FieldType type, int? fixedLength = null, int? maxLength = null)
        {
            FieldNumber = fieldNumber;
            Description = description;
            Type = type;
            FixedLength = fixedLength;
            MaxLength = maxLength;
        }
    }

    public enum FieldType
    {
        Fixed,
        Variable,
        Bitmap
    }

    public enum LengthEncoding
    {
        LLVAR,
        LLLVAR
    }

    public class IsoSchema
    {
        public Dictionary<int, IsoFieldDefinition> Fields { get; set; } = new();

        public void AddField(int fieldNumber, string description, FieldType type, int? fixedLength = null, int? maxLength = null)
        {
            Fields[fieldNumber] = new IsoFieldDefinition(fieldNumber, description, type, fixedLength, maxLength);
        }

        public IsoFieldDefinition? GetField(int fieldNumber)
        {
            Fields.TryGetValue(fieldNumber, out var field);
            return field;
        }

        public static IsoSchema GetNapasSchema()
        {
            var schema = new IsoSchema();

            schema.AddField(1, "Bitmap", FieldType.Bitmap);
            schema.AddField(2, "PAN (Primary Account Number)", FieldType.Variable, maxLength: 19);
            schema.AddField(3, "Processing Code", FieldType.Fixed, fixedLength: 6);
            schema.AddField(4, "Amount, Transaction", FieldType.Fixed, fixedLength: 12);
            schema.AddField(11, "STAN (System Trace Audit Number)", FieldType.Fixed, fixedLength: 6);
            schema.AddField(12, "Time, Transaction", FieldType.Fixed, fixedLength: 6);
            schema.AddField(13, "Date, Transaction", FieldType.Fixed, fixedLength: 4);
            schema.AddField(39, "Response Code", FieldType.Fixed, fixedLength: 2);
            schema.AddField(41, "Card Acceptor Terminal ID", FieldType.Fixed, fixedLength: 8);
            schema.AddField(42, "Card Acceptor ID Code", FieldType.Fixed, fixedLength: 15);
            schema.AddField(43, "Card Acceptor Name/Location", FieldType.Variable, maxLength: 40);

            return schema;
        }
    }
}

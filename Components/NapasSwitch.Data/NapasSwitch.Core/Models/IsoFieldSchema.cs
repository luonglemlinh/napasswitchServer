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

            // Bitmap
            schema.AddField(1, "Bitmap", FieldType.Bitmap);
            
            // DE2: PAN - LLVAR, max 19 digits
            schema.AddField(2, "PAN (Primary Account Number)", FieldType.Variable, maxLength: 19);
            
            // DE3: Processing Code - Fixed 6 numeric
            schema.AddField(3, "Processing Code", FieldType.Fixed, fixedLength: 6);
            
            // DE4: Amount - Fixed 12 numeric
            schema.AddField(4, "Amount, Transaction", FieldType.Fixed, fixedLength: 12);
            
            // DE7: Transmission Date/Time - Fixed 10 numeric (MMddHHmmss)
            schema.AddField(7, "Transmission Date/Time", FieldType.Fixed, fixedLength: 10);
            
            // DE11: STAN - Fixed 6 numeric
            schema.AddField(11, "STAN (System Trace Audit Number)", FieldType.Fixed, fixedLength: 6);
            
            // DE12: Local Time - Fixed 6 numeric (HHmmss)
            schema.AddField(12, "Time, Local Transaction", FieldType.Fixed, fixedLength: 6);
            
            // DE13: Local Date - Fixed 4 numeric (MMdd)
            schema.AddField(13, "Date, Local Transaction", FieldType.Fixed, fixedLength: 4);
            
            // DE14: Expiration Date - Fixed 4 numeric (YYMM)
            schema.AddField(14, "Date, Expiration", FieldType.Fixed, fixedLength: 4);
            
            // DE18: Merchant Category Code - Fixed 4 numeric
            schema.AddField(18, "Merchant Category Code", FieldType.Fixed, fixedLength: 4);
            
            // DE22: POS Entry Mode - Fixed 3 numeric
            schema.AddField(22, "POS Entry Mode", FieldType.Fixed, fixedLength: 3);
            
            // DE23: Card Sequence Number - Fixed 3 numeric
            schema.AddField(23, "Card Sequence Number", FieldType.Fixed, fixedLength: 3);
            
            // DE25: POS Condition Code - Fixed 2 numeric
            schema.AddField(25, "POS Condition Code", FieldType.Fixed, fixedLength: 2);
            
            // DE32: Acquiring Institution ID - LLVAR, max 11 digits
            schema.AddField(32, "Acquiring Institution ID", FieldType.Variable, maxLength: 11);
            
            // DE33: Forwarding Institution ID - LLVAR, max 11 digits
            schema.AddField(33, "Forwarding Institution ID", FieldType.Variable, maxLength: 11);
            
            // DE35: Track 2 Data - LLVAR, max 37
            schema.AddField(35, "Track 2 Data", FieldType.Variable, maxLength: 37);
            
            // DE37: Retrieval Reference Number - Fixed 12 alphanumeric
            schema.AddField(37, "Retrieval Reference Number", FieldType.Fixed, fixedLength: 12);
            
            // DE38: Authorization ID Response - Fixed 6 alphanumeric
            schema.AddField(38, "Authorization ID Response", FieldType.Fixed, fixedLength: 6);
            
            // DE39: Response Code - Fixed 2 numeric
            schema.AddField(39, "Response Code", FieldType.Fixed, fixedLength: 2);
            
            // DE41: Card Acceptor Terminal ID - Fixed 8 alphanumeric
            schema.AddField(41, "Card Acceptor Terminal ID", FieldType.Fixed, fixedLength: 8);
            
            // DE42: Card Acceptor ID Code - Fixed 15 alphanumeric
            schema.AddField(42, "Card Acceptor ID Code", FieldType.Fixed, fixedLength: 15);
            
            // DE43: Card Acceptor Name/Location - Fixed 40 alphanumeric (right-padded with spaces)
            schema.AddField(43, "Card Acceptor Name/Location", FieldType.Fixed, fixedLength: 40);
            
            // DE49: Currency Code - Fixed 3 numeric
            schema.AddField(49, "Currency Code, Transaction", FieldType.Fixed, fixedLength: 3);
            
            // DE52: PIN Block - Fixed 16 hex (8 bytes binary)
            schema.AddField(52, "PIN Data", FieldType.Fixed, fixedLength: 16);
            
            // DE55: ICC Data - LLLVAR, max 999
            var de55 = new IsoFieldDefinition(55, "ICC System Related Data", FieldType.Variable, maxLength: 999);
            de55.LengthEncoding = LengthEncoding.LLLVAR;
            schema.Fields[55] = de55;
            
            // DE70: Network Management Information Code - Fixed 3 numeric
            schema.AddField(70, "Network Management Information Code", FieldType.Fixed, fixedLength: 3);

            return schema;
        }
    }
}

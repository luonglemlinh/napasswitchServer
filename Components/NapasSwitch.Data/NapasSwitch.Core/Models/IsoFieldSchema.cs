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

            // DE5: Settlement Amount - Fixed 12 numeric
            schema.AddField(5, "Amount, Settlement", FieldType.Fixed, fixedLength: 12);
            
            // DE7: Transmission Date/Time - Fixed 10 numeric (MMddHHmmss)
            schema.AddField(7, "Transmission Date/Time", FieldType.Fixed, fixedLength: 10);
            
            // DE9: Settlement Conversion Rate - Fixed 8 numeric
            schema.AddField(9, "Conversion Rate, Settlement", FieldType.Fixed, fixedLength: 8);

            // DE10: Billing Conversion Rate - Fixed 8 numeric
            schema.AddField(10, "Conversion Rate, Cardholder Billing", FieldType.Fixed, fixedLength: 8);
            
            // DE11: STAN - Fixed 6 numeric
            schema.AddField(11, "STAN (System Trace Audit Number)", FieldType.Fixed, fixedLength: 6);
            
            // DE12: Local Time - Fixed 6 numeric (HHmmss)
            schema.AddField(12, "Time, Local Transaction", FieldType.Fixed, fixedLength: 6);
            
            // DE13: Local Date - Fixed 4 numeric (MMdd)
            schema.AddField(13, "Date, Local Transaction", FieldType.Fixed, fixedLength: 4);
            
            // DE14: Expiration Date - Fixed 4 numeric (YYMM)
            schema.AddField(14, "Date, Expiration", FieldType.Fixed, fixedLength: 4);
            
            // DE15: Settlement Date - Fixed 4 numeric (MMdd)
            schema.AddField(15, "Date, Settlement", FieldType.Fixed, fixedLength: 4);
            
            // DE18: Merchant Category Code - Fixed 4 numeric
            schema.AddField(18, "Merchant Category Code", FieldType.Fixed, fixedLength: 4);
            
            // DE19: Acquiring Country Code - Fixed 3 numeric
            schema.AddField(19, "Acquiring Country Code", FieldType.Fixed, fixedLength: 3);

            // DE22: POS Entry Mode - Fixed 3 numeric
            schema.AddField(22, "POS Entry Mode", FieldType.Fixed, fixedLength: 3);
            
            // DE23: Card Sequence Number - Fixed 3 numeric
            schema.AddField(23, "Card Sequence Number", FieldType.Fixed, fixedLength: 3);
            
            // DE25: POS Condition Code - Fixed 2 numeric
            schema.AddField(25, "POS Condition Code", FieldType.Fixed, fixedLength: 2);
            
            // DE32: Acquiring Institution Identification Code - LLVAR, max 11 digits
            schema.AddField(32, "Acquiring Institution ID", FieldType.Variable, maxLength: 11);
            
            // DE33: Forwarding Institution ID - LLVAR, max 11 digits
            schema.AddField(33, "Forwarding Institution ID", FieldType.Variable, maxLength: 11);
            
            // DE35: Track 2 Data - LLVAR, max 37
            var de35 = new IsoFieldDefinition(35, "Track 2 Data", FieldType.Variable, maxLength: 37);
            de35.LengthEncoding = LengthEncoding.LLVAR;
            schema.Fields[35] = de35;

            // DE36: Track 3 Data - LLLVAR, max 104
            var de36 = new IsoFieldDefinition(36, "Track 3 Data", FieldType.Variable, maxLength: 104);
            de36.LengthEncoding = LengthEncoding.LLLVAR;
            schema.Fields[36] = de36;
            
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
            
            // DE43: Card Acceptor Name/Location - Fixed 40 alphanumeric
            schema.AddField(43, "Card Acceptor Name/Location", FieldType.Fixed, fixedLength: 40);

            // DE45: Track 1 Data - LLVAR, max 79
            var de45 = new IsoFieldDefinition(45, "Track 1 Data", FieldType.Variable, maxLength: 79);
            de45.LengthEncoding = LengthEncoding.LLVAR;
            schema.Fields[45] = de45;

            // DE48: Additional Private Data - LLLVAR, max 999
            var de48 = new IsoFieldDefinition(48, "Additional Private Data", FieldType.Variable, maxLength: 999);
            de48.LengthEncoding = LengthEncoding.LLLVAR;
            schema.Fields[48] = de48;
            
            // DE49: Currency Code - Transaction, Fixed 3 numeric
            schema.AddField(49, "Currency Code, Transaction", FieldType.Fixed, fixedLength: 3);
            
            // DE50: Currency Code - Settlement, Fixed 3 numeric
            schema.AddField(50, "Currency Code, Settlement", FieldType.Fixed, fixedLength: 3);

            // DE51: Billing Currency Code - Fixed 3 numeric
            schema.AddField(51, "Currency Code, Cardholder Billing", FieldType.Fixed, fixedLength: 3);
            
            // DE52: PIN Data - Fixed 16 hex
            schema.AddField(52, "PIN Data", FieldType.Fixed, fixedLength: 16);
            
            // DE54: Additional Amounts - LLLVAR, max 120
            var de54 = new IsoFieldDefinition(54, "Additional Amounts", FieldType.Variable, maxLength: 120);
            de54.LengthEncoding = LengthEncoding.LLLVAR;
            schema.Fields[54] = de54;
            
            // DE55: ICC System Related Data - LLLVAR, max 999
            var de55 = new IsoFieldDefinition(55, "ICC System Related Data", FieldType.Variable, maxLength: 999);
            de55.LengthEncoding = LengthEncoding.LLLVAR;
            schema.Fields[55] = de55;

            // DE60: Private Data - LLLVAR, max 60
            var de60 = new IsoFieldDefinition(60, "Private Data", FieldType.Variable, maxLength: 60);
            de60.LengthEncoding = LengthEncoding.LLLVAR;
            schema.Fields[60] = de60;

            // DE62: Service Code - LLVAR, max 10
            var de62 = new IsoFieldDefinition(62, "Service Code", FieldType.Variable, maxLength: 10);
            de62.LengthEncoding = LengthEncoding.LLVAR;
            schema.Fields[62] = de62;

            // DE63: Transaction Reference Number - LLLVAR, max 16
            var de63 = new IsoFieldDefinition(63, "Transaction Reference Number", FieldType.Variable, maxLength: 16);
            de63.LengthEncoding = LengthEncoding.LLLVAR;
            schema.Fields[63] = de63;
            
            // DE70: Network Management Information Code - Fixed 3 numeric
            schema.AddField(70, "Network Management Information Code", FieldType.Fixed, fixedLength: 3);

            // DE90: Original Data Elements - Fixed 42 numeric
            schema.AddField(90, "Original Data Elements", FieldType.Fixed, fixedLength: 42);

            // DE95: Replacement Amounts - Fixed 42 alphanumeric
            schema.AddField(95, "Replacement Amounts", FieldType.Fixed, fixedLength: 42);

            // DE100: Receiving Institution ID - LLVAR, max 11
            schema.AddField(100, "Receiving Institution ID", FieldType.Variable, maxLength: 11);

            // DE102: Account Identification 1 - LLVAR, max 28
            schema.AddField(102, "Account Identification 1", FieldType.Variable, maxLength: 28);

            // DE103: Account Identification 2 - LLVAR, max 28
            schema.AddField(103, "Account Identification 2", FieldType.Variable, maxLength: 28);

            // DE104: Content Transfer - LLLVAR, max 210
            var de104 = new IsoFieldDefinition(104, "Content Transfer", FieldType.Variable, maxLength: 210);
            de104.LengthEncoding = LengthEncoding.LLLVAR;
            schema.Fields[104] = de104;

            // DE105: New PIN Block - LLLVAR, max 999
            var de105 = new IsoFieldDefinition(105, "New PIN Block", FieldType.Variable, maxLength: 999);
            de105.LengthEncoding = LengthEncoding.LLLVAR;
            schema.Fields[105] = de105;

            // DE120: Record Data - LLLVAR, max 999
            var de120 = new IsoFieldDefinition(120, "Record Data", FieldType.Variable, maxLength: 999);
            de120.LengthEncoding = LengthEncoding.LLLVAR;
            schema.Fields[120] = de120;

            // DE123: POS Data Code - Fixed 15 alphanumeric
            schema.AddField(123, "POS Data Code", FieldType.Fixed, fixedLength: 15);

            // DE128: MAC - Fixed 16 alphanumeric
            schema.AddField(128, "MAC", FieldType.Fixed, fixedLength: 16);

            return schema;
        }
    }
}

using System;
using System.Net.Sockets;
using System.Text;
using core.ISO8583;
using core.Models;

namespace SimpleClient
{
    class Program
    {
        private static int _stan = 1;
        
        static void Main(string[] args)
        {
            Console.WriteLine("?????????????????????????????????????????????????????");
            Console.WriteLine("?           SIMPLE ISO-8583 CLIENT                  ?");
            Console.WriteLine("?????????????????????????????????????????????????????\n");

            string host = "127.0.0.1";
            int port = 8583;

            Console.WriteLine($"Target: {host}:{port}\n");

            while (true)
            {
                Console.WriteLine("\n" + new string('?', 55));
                Console.WriteLine("Select option:");
                Console.WriteLine("  1. Send raw HEX message");
                Console.WriteLine("  2. Build & send Purchase (0200)");
                Console.WriteLine("  3. Build & send Balance Inquiry (0200)");
                Console.WriteLine("  4. Build & send Network Management (0800)");
                Console.WriteLine("  5. Exit");
                Console.Write("\n> ");

                string? choice = Console.ReadLine()?.Trim();

                switch (choice)
                {
                    case "1":
                        SendRawHexMessage(host, port);
                        break;
                    case "2":
                        SendPurchaseTransaction(host, port);
                        break;
                    case "3":
                        SendBalanceInquiry(host, port);
                        break;
                    case "4":
                        SendNetworkManagement(host, port);
                        break;
                    case "5":
                    case "exit":
                        Console.WriteLine("\nGoodbye!");
                        return;
                    default:
                        Console.WriteLine("Invalid option");
                        break;
                }
            }
        }

        static void SendRawHexMessage(string host, int port)
        {
            Console.WriteLine("\nPaste your ISO-8583 message in HEX format:");
            Console.Write("> ");

            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) return;

            input = input.Replace(" ", "").Replace("-", "").Replace(":", "").Trim();

            byte[] messageBytes;
            try
            {
                messageBytes = Enumerable.Range(0, input.Length)
                    .Where(x => x % 2 == 0)
                    .Select(x => Convert.ToByte(input.Substring(x, 2), 16))
                    .ToArray();
                
                Console.WriteLine($"\n? Parsed {messageBytes.Length} bytes");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"? Invalid HEX: {ex.Message}");
                Console.ResetColor();
                return;
            }

            SendToSwitch(messageBytes, host, port);
        }

        static void SendPurchaseTransaction(string host, int port)
        {
            Console.WriteLine("\n=== BUILD PURCHASE TRANSACTION ===");
            
            Console.Write("PAN (card number): ");
            string? pan = Console.ReadLine()?.Trim() ?? "9704180000000001";
            
            Console.Write("Amount (VND, no decimals): ");
            string? amountInput = Console.ReadLine()?.Trim() ?? "100000";
            
            Console.Write("Terminal ID (8 chars): ");
            string? terminalId = Console.ReadLine()?.Trim() ?? "00000001";
            
            Console.Write("Merchant ID (15 chars): ");
            string? merchantId = Console.ReadLine()?.Trim() ?? "NAPASMERCHANT01";

            var message = BuildPurchaseMessage(pan, amountInput, terminalId, merchantId);
            
            var parser = new IsoParser();
            byte[] messageBytes = parser.Build(message);
            
            Console.WriteLine("\n[BUILT MESSAGE]");
            PrintMessageDetails(message);
            Console.WriteLine($"  HEX: {BitConverter.ToString(messageBytes).Replace("-", "")}");
            
            SendToSwitch(messageBytes, host, port);
        }

        static void SendBalanceInquiry(string host, int port)
        {
            Console.WriteLine("\n=== BUILD BALANCE INQUIRY ===");
            
            Console.Write("PAN (card number): ");
            string? pan = Console.ReadLine()?.Trim() ?? "9704180000000001";
            
            Console.Write("Terminal ID (8 chars): ");
            string? terminalId = Console.ReadLine()?.Trim() ?? "TERM0001";
            
            Console.Write("Merchant ID (15 chars): ");
            string? merchantId = Console.ReadLine()?.Trim() ?? "MERCHANT0000001";

            var message = BuildBalanceInquiryMessage(pan, terminalId, merchantId);
            
            var parser = new IsoParser();
            byte[] messageBytes = parser.Build(message);
            
            Console.WriteLine("\n[BUILT MESSAGE]");
            PrintMessageDetails(message);
            
            SendToSwitch(messageBytes, host, port);
        }

        static void SendNetworkManagement(string host, int port)
        {
            Console.WriteLine("\n=== BUILD NETWORK MANAGEMENT (Sign-On) ===");

            var message = BuildNetworkManagementMessage();
            
            var parser = new IsoParser();
            byte[] messageBytes = parser.Build(message);
            
            Console.WriteLine("\n[BUILT MESSAGE]");
            PrintMessageDetails(message);
            
            SendToSwitch(messageBytes, host, port);
        }

        static IsoMessage BuildPurchaseMessage(string pan, string amount, string terminalId, string merchantId)
        {
            var now = DateTime.Now;
            
            var message = new IsoMessage
            {
                MessageType = "0200"
            };

            // DE2: PAN (LLVAR)
            message.SetField(2, pan);
            
            // DE3: Processing Code - 000000 = Purchase
            message.SetField(3, "000000");
            
            // DE4: Amount - 12 digits, left-padded with zeros (User requested x100 conversion: 123456 -> 12345600)
            long amountValue = long.TryParse(amount, out long a) ? a : 100000;
            message.SetField(4, (amountValue * 100).ToString("D12"));

            // DE7: Transmission Date - MMddHHmmss
            message.SetField(7, now.ToString("MMddHHmmss"));

            // DE11: STAN - 6 digits
            message.SetField(11, (_stan++).ToString("D6"));

            // DE12: Local Time - HHmmss
            message.SetField(12, now.ToString("HHmmss"));

            // DE13: Local Date - MMdd
            message.SetField(13, now.ToString("MMdd"));

            // DE14: Expiry Date - YYMM
            message.SetField(14, "2512");
            
            // DE18: MCC
            message.SetField(18, "5411");
            
            // DE22: POS Entry Mode - 011 = Manual entry
            message.SetField(22, "011");
            
            // DE25: POS Condition Code
            message.SetField(25, "00");
            
            // DE32: Acquirer ID (LLVAR)
            message.SetField(32, "970400");
            
            // DE37: RRN - 12 alphanumeric (right-padded with spaces)
            message.SetField(37, now.ToString("yyMMddHHmmss"));
            
            // DE41: Terminal ID - 8 chars
            message.SetField(41, terminalId.PadRight(8)[..8]);
            
            // DE42: Merchant ID - 15 chars
            message.SetField(42, merchantId.PadRight(15)[..15]);
            
            // DE43: Card Acceptor Name/Location - 40 chars (fixed)
            message.SetField(43, "SimpleClient Test           VN         ");
            
            // DE49: Currency Code - 704 = VND
            message.SetField(49, "704");

            return message;
        }

        static IsoMessage BuildBalanceInquiryMessage(string pan, string terminalId, string merchantId)
        {
            var now = DateTime.Now;
            
            var message = new IsoMessage
            {
                MessageType = "0200"
            };

            // DE2: PAN
            message.SetField(2, pan);
            
            // DE3: Processing Code - 300000 = Balance Inquiry
            message.SetField(3, "300000");
            
            // DE4: Amount - 0 for balance inquiry
            message.SetField(4, "000000000000");
            
            // DE7: Transmission DateTime - MMddHHmmss
            message.SetField(7, now.ToString("MMddHHmmss"));
            
            // DE11: STAN
            message.SetField(11, (_stan++).ToString("D6"));
            
            // DE12: Local Time - HHmmss
            message.SetField(12, now.ToString("HHmmss"));
            
            // DE13: Local Date - MMdd
            message.SetField(13, now.ToString("MMdd"));
            
            // DE18: MCC
            message.SetField(18, "6011");
            
            // DE22: POS Entry Mode
            message.SetField(22, "011");
            
            // DE25: POS Condition Code
            message.SetField(25, "00");
            
            // DE32: Acquirer ID - Match Tutor's Spec (970488)
            message.SetField(32, "970488");
            
            // DE37: RRN
            message.SetField(37, now.ToString("yyMMddHHmmss"));
            
            // DE41: Terminal ID
            message.SetField(41, terminalId.PadRight(8)[..8]);
            
            // DE42: Merchant ID
            message.SetField(42, merchantId.PadRight(15)[..15]);
            
            // DE43: Card Acceptor Name/Location - 40 chars fixed
            message.SetField(43, "SimpleClient Test           VN         ");
            
            // DE49: Currency Code
            message.SetField(49, "704");

            return message;
        }

        static IsoMessage BuildNetworkManagementMessage()
        {
            var now = DateTime.Now;
            
            var message = new IsoMessage
            {
                MessageType = "0800"
            };

            // DE7: Transmission DateTime - MMddHHmmss
            message.SetField(7, now.ToString("MMddHHmmss"));
            
            // DE11: STAN
            message.SetField(11, (_stan++).ToString("D6"));
            
            // DE70: Network Management Code - 001 = Sign-On
            message.SetField(70, "001");

            return message;
        }

        static void PrintMessageDetails(IsoMessage message)
        {
            Console.WriteLine($"  MTI: {message.MessageType}");
            Console.WriteLine($"  DE2 (PAN): {MaskPAN(message.GetField(2))}");
            Console.WriteLine($"  DE3 (Proc): {message.GetField(3)}");
            Console.WriteLine($"  DE4 (Amt): {message.GetField(4)}");
            Console.WriteLine($"  DE7 (TransDT): {message.GetField(7)}");
            Console.WriteLine($"  DE11 (STAN): {message.GetField(11)}");
            Console.WriteLine($"  DE12 (Time): {message.GetField(12)}");
            Console.WriteLine($"  DE13 (Date): {message.GetField(13)}");
        }

        static void SendToSwitch(byte[] messageBytes, string host, int port)
        {
            try
            {
                Console.WriteLine($"\n[CONNECTING] to {host}:{port}...");
                
                using var client = new TcpClient();
                client.Connect(host, port);

                Console.WriteLine("? Connected!");

                using var stream = client.GetStream();
                stream.ReadTimeout = 30000;
                stream.WriteTimeout = 30000;

                byte[] lengthBytes = new byte[2];
                lengthBytes[0] = (byte)(messageBytes.Length >> 8);
                lengthBytes[1] = (byte)(messageBytes.Length & 0xFF);

                Console.WriteLine($"\n[SENDING] {messageBytes.Length} bytes...");
                stream.Write(lengthBytes, 0, 2);
                stream.Write(messageBytes, 0, messageBytes.Length);
                stream.Flush();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("? Message sent!");
                Console.ResetColor();

                Console.WriteLine("\n[WAITING] for response...");

                byte[] responseLengthBytes = new byte[2];
                int bytesRead = stream.Read(responseLengthBytes, 0, 2);

                if (bytesRead == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("? Connection closed by server");
                    Console.ResetColor();
                    return;
                }

                int responseLength = (responseLengthBytes[0] << 8) | responseLengthBytes[1];
                Console.WriteLine($"  Response length: {responseLength} bytes");

                byte[] responseBytes = new byte[responseLength];
                int totalRead = 0;
                while (totalRead < responseLength)
                {
                    bytesRead = stream.Read(responseBytes, totalRead, responseLength - totalRead);
                    if (bytesRead == 0) break;
                    totalRead += bytesRead;
                }

                Console.WriteLine("\n?????????????????????????????????????????????????????");
                Console.WriteLine("?              RESPONSE RECEIVED                    ?");
                Console.WriteLine("?????????????????????????????????????????????????????");

                try
                {
                    var parser = new IsoParser();
                    var response = parser.Parse(responseBytes);
                    
                    string rc = response.GetField(39) ?? "96";
                    
                    Console.WriteLine($"\n  MTI: {response.MessageType}");
                    Console.WriteLine($"  STAN: {response.GetField(11)}");
                    Console.WriteLine($"  Response Code: {rc} - {GetResponseDescription(rc)}");
                    
                    if (response.HasField(4))
                        Console.WriteLine($"  Amount: {response.GetField(4)}");
                    
                    Console.WriteLine($"\n  Fields: {response.Fields.Count}");
                    foreach (var field in response.Fields.OrderBy(f => f.Key).Take(15))
                    {
                        string value = field.Key == 2 ? MaskPAN(field.Value) : field.Value;
                        Console.WriteLine($"    DE{field.Key:000}: {value}");
                    }

                    Console.WriteLine("\n" + new string('?', 55));
                    if (rc == "00")
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("  ? APPROVED");
                    }
                    else if (rc == "30")
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("  ? FORMAT ERROR");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  ? {GetResponseDescription(rc)}");
                    }
                    Console.ResetColor();
                    Console.WriteLine(new string('?', 55));
                }
                catch (Exception ex)
                {
                    string hexResponse = BitConverter.ToString(responseBytes).Replace("-", "");
                    Console.WriteLine($"\nRaw HEX: {hexResponse}");
                    Console.WriteLine($"\nError parsing: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n? ERROR: {ex.Message}");
                Console.ResetColor();
            }
        }

        static string MaskPAN(string? pan)
        {
            if (string.IsNullOrEmpty(pan) || pan.Length < 10)
                return "****";
            return $"{pan[..6]}****{pan[^4..]}";
        }

        static string GetResponseDescription(string code)
        {
            return code switch
            {
                "00" => "Approved",
                "01" => "Refer to issuer",
                "05" => "Do not honor",
                "12" => "Invalid transaction",
                "14" => "Invalid card",
                "15" => "No such issuer",
                "30" => "Format error",
                "51" => "Insufficient funds",
                "54" => "Expired card",
                "55" => "Incorrect PIN",
                "91" => "Issuer unavailable",
                "96" => "System malfunction",
                _ => $"Unknown ({code})"
            };
        }
    }
}

using System;
using System.Net.Sockets;
using System.Text;
using core.ISO8583;
using core.Models;

namespace SimpleClient
{
    class Program
    {
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
                Console.WriteLine("Paste your ISO-8583 message in HEX format:");
                Console.WriteLine("(or type 'exit' to quit)");
                Console.Write("\n> ");

                string? input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input) || input.ToLower() == "exit")
                    break;

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
                    continue;
                }

                try
                {
                    var parser = new IsoParser();
                    var message = parser.Parse(messageBytes);
                    
                    Console.WriteLine("\n[REQUEST MESSAGE]");
                    Console.WriteLine($"  MTI: {message.MessageType}");
                    Console.WriteLine($"  STAN: {message.GetField(11)}");
                    Console.WriteLine($"  Amount: {message.GetField(4)}");
                    Console.WriteLine($"  PAN: {MaskPAN(message.GetField(2))}");
                }
                catch
                {
                    Console.WriteLine("\n[RAW MESSAGE] (could not parse)");
                }

                SendToSwitch(messageBytes, host, port);
            }

            Console.WriteLine("\nGoodbye!");
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
                    foreach (var field in response.Fields.OrderBy(f => f.Key).Take(10))
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
                        Console.WriteLine("  ? FORMAT ERROR (Correlation Failed?)");
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
                "91" => "Issuer unavailable",
                "96" => "System malfunction",
                _ => $"Unknown ({code})"
            };
        }
    }
}

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using core.ISO8583;
using core.Models;

namespace IssuerSimulator
{
    class Program
    {
        private static bool _isRunning = true;
        private static int _processedCount = 0;

        static void Main(string[] args)
        {
            Console.WriteLine("╔═══════════════════════════════════════════════════╗");
            Console.WriteLine("║         ISSUER SIMULATOR (ISS)                    ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════╝\n");

            int port = 7003;
            
            if (args.Length > 0 && int.TryParse(args[0], out int customPort))
                port = customPort;

            Console.WriteLine($"Listening on port: {port}");
            Console.WriteLine("Waiting for connections from Switch...\n");

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            Console.WriteLine("Press Ctrl+C to stop\n");

            while (_isRunning)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleClient, client);
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Console.WriteLine($"Error accepting connection: {ex.Message}");
                }
            }

            listener.Stop();
        }

        static void HandleClient(object? obj)
        {
            if (obj is not TcpClient client) return;

            string sessionId = Guid.NewGuid().ToString("N")[..8];
            NetworkStream? stream = null;

            try
            {
                stream = client.GetStream();
                string clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";

                Console.WriteLine($"[{sessionId}] Connection from {clientEndpoint}");

                stream.ReadTimeout = 30000;
                stream.WriteTimeout = 30000;

                while (client.Connected)
                {
                    byte[] lengthBytes = new byte[2];
                    if (!TryReadExact(stream, lengthBytes, 0, 2))
                    {
                        Console.WriteLine($"[{sessionId}] Connection closed while reading length header");
                        break;
                    }

                    int messageLength = (lengthBytes[0] << 8) | lengthBytes[1];

                    if (messageLength <= 0 || messageLength > 9999)
                    {
                        Console.WriteLine($"[{sessionId}] Invalid message length: {messageLength}");
                        break;
                    }

                    byte[] messageBytes = new byte[messageLength];
                    if (!TryReadExact(stream, messageBytes, 0, messageLength))
                    {
                        Console.WriteLine($"[{sessionId}] Incomplete message (expected {messageLength} bytes)");
                        break;
                    }

                    Console.WriteLine($"[{sessionId}] Received {messageLength} bytes");

                    byte[]? responseBytes = ProcessMessage(messageBytes, sessionId);

                    if (responseBytes != null && responseBytes.Length > 0)
                    {
                        byte[] responseLengthBytes = new byte[2];
                        responseLengthBytes[0] = (byte)(responseBytes.Length >> 8);
                        responseLengthBytes[1] = (byte)(responseBytes.Length & 0xFF);

                        stream.Write(responseLengthBytes, 0, 2);
                        stream.Write(responseBytes, 0, responseBytes.Length);
                        stream.Flush();

                        Console.WriteLine($"[{sessionId}] Sent {responseBytes.Length} bytes response\n");
                        
                        Interlocked.Increment(ref _processedCount);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{sessionId}] Error: {ex.Message}");
            }
            finally
            {
                stream?.Close();
                client?.Close();
                Console.WriteLine($"[{sessionId}] Disconnected. Total processed: {_processedCount}\n");
            }
        }

        static byte[]? ProcessMessage(byte[] messageBytes, string sessionId)
        {
            try
            {
                var parser = new IsoParser();
                IsoMessage request = parser.Parse(messageBytes);

                Console.WriteLine($"[{sessionId}] Request MTI: {request.MessageType}");
                Console.WriteLine($"[{sessionId}]   STAN: {request.GetField(11)}");
                Console.WriteLine($"[{sessionId}]   PAN: {MaskPAN(request.GetField(2))}");
                Console.WriteLine($"[{sessionId}]   Amount: {request.GetField(4)}");
                Console.WriteLine($"[{sessionId}]   Proc Code: {request.GetField(3)}");

                IsoMessage response = request.MessageType switch
                {
                    "0200" => CreateAuthResponse(request),
                    "0400" => CreateReversalResponse(request),
                    "0800" => CreateNetworkResponse(request),
                    _ => CreateErrorResponse(request, "12")
                };

                Console.WriteLine($"[{sessionId}] Response MTI: {response.MessageType}");
                Console.WriteLine($"[{sessionId}]   RC: {response.GetField(39)}");

                return parser.Build(response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{sessionId}] Processing error: {ex.Message}");
                return null;
            }
        }

        static bool TryReadExact(NetworkStream stream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int bytesRead = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (bytesRead == 0)
                {
                    return false;
                }
                totalRead += bytesRead;
            }
            return true;
        }

        static IsoMessage CreateAuthResponse(IsoMessage request)
        {
            var response = new IsoMessage
            {
                MessageType = "0210"
            };

            foreach (var field in new[] { 2, 3, 4, 7, 11, 12, 13, 32, 37, 41, 42 })
            {
                if (request.HasField(field))
                    response.SetField(field, request.GetField(field));
            }

            string? amount = request.GetField(4);
            if (!string.IsNullOrEmpty(amount) && long.TryParse(amount, out long amt))
            {
                if (amt > 1000000)
                    response.SetResponseCode("51");
                else
                    response.SetResponseCode("00");
            }
            else
            {
                response.SetResponseCode("00");
            }

            return response;
        }

        static IsoMessage CreateReversalResponse(IsoMessage request)
        {
            var response = new IsoMessage
            {
                MessageType = "0410"
            };

            foreach (var field in new[] { 2, 3, 4, 7, 11, 12, 13, 32, 37, 41, 42 })
            {
                if (request.HasField(field))
                    response.SetField(field, request.GetField(field));
            }

            response.SetResponseCode("00");
            return response;
        }

        static IsoMessage CreateNetworkResponse(IsoMessage request)
        {
            var response = new IsoMessage
            {
                MessageType = "0810"
            };

            if (request.HasField(7)) response.SetField(7, request.GetField(7));
            if (request.HasField(11)) response.SetField(11, request.GetField(11));
            if (request.HasField(70)) response.SetField(70, request.GetField(70));

            response.SetResponseCode("00");
            return response;
        }

        static IsoMessage CreateErrorResponse(IsoMessage request, string responseCode)
        {
            var response = new IsoMessage
            {
                MessageType = request.MessageType == "0200" ? "0210" :
                              request.MessageType == "0400" ? "0410" : "0810"
            };

            foreach (var field in new[] { 2, 3, 4, 7, 11, 12, 13, 32, 37, 41, 42 })
            {
                if (request.HasField(field))
                    response.SetField(field, request.GetField(field));
            }

            response.SetResponseCode(responseCode);
            return response;
        }

        static string MaskPAN(string? pan)
        {
            if (string.IsNullOrEmpty(pan) || pan.Length < 10)
                return "****";
            return $"{pan[..6]}****{pan[^4..]}";
        }
    }
}

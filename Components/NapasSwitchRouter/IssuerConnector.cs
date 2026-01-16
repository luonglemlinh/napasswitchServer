using System;
using System.Net.Sockets;
using core.Configuration;
using core.Models;
using core.Models.Configuration;
using core.ISO8583;

namespace router
{
    /// <summary>
    /// Handles communication with Issuer (ISS) banks
    /// Forwards authorization requests and receives responses
    /// </summary>
    public class IssuerConnector
    {
        private readonly IsoParser _parser;

        public IssuerConnector()
        {
            _parser = new IsoParser();
        }

        /// <summary>
        /// Forward a message to the appropriate Issuer bank and wait for response
        /// This is the core routing logic: ACQ -> ISS -> ACQ
        /// </summary>
        public IsoMessage ForwardToIssuer(IsoMessage request, IssuerBankConfig issuerBank, string sessionId)
        {
            TcpClient? issuerClient = null;
            NetworkStream? stream = null;

            try
            {
                Console.WriteLine($"[{sessionId}] [ISS-CONNECT] Connecting to {issuerBank.IssuerName} at {issuerBank.Host}:{issuerBank.Port}");

                // Step 1: Connect to the Issuer bank
                issuerClient = new TcpClient();
                issuerClient.Connect(issuerBank.Host, issuerBank.Port);
                stream = issuerClient.GetStream();

                // Set timeouts from configuration
                stream.ReadTimeout = issuerBank.Timeout;
                stream.WriteTimeout = issuerBank.Timeout;

                Console.WriteLine($"[{sessionId}] [ISS-CONNECT] Connection established");

                // Step 2: Build and send the ISO-8583 message
                byte[] requestBytes = _parser.Build(request);

                // Prepare length header (2 bytes, big-endian)
                byte[] lengthHeader = new byte[2];
                lengthHeader[0] = (byte)(requestBytes.Length >> 8);
                lengthHeader[1] = (byte)(requestBytes.Length & 0xFF);

                // Send to ISS
                stream.Write(lengthHeader, 0, 2);
                stream.Write(requestBytes, 0, requestBytes.Length);
                stream.Flush();

                Console.WriteLine($"[{sessionId}] [ISS-SEND] Sent {requestBytes.Length} bytes to ISS");

                // Step 3: Receive response from ISS
                byte[] responseLengthBytes = new byte[2];
                int bytesRead = stream.Read(responseLengthBytes, 0, 2);

                if (bytesRead < 2)
                {
                    Console.WriteLine($"[{sessionId}] [ISS-ERROR] Failed to read response length header");
                    return CreateTimeoutResponse(request);
                }

                int responseLength = (responseLengthBytes[0] << 8) | responseLengthBytes[1];

                Console.WriteLine($"[{sessionId}] [ISS-RECV] Expecting {responseLength} bytes from ISS");

                // Read the full response
                byte[] responseBytes = new byte[responseLength];
                int totalRead = 0;

                while (totalRead < responseLength)
                {
                    bytesRead = stream.Read(responseBytes, totalRead, responseLength - totalRead);
                    if (bytesRead == 0)
                    {
                        Console.WriteLine($"[{sessionId}] [ISS-ERROR] Connection closed while reading response");
                        return CreateTimeoutResponse(request);
                    }
                    totalRead += bytesRead;
                }

                Console.WriteLine($"[{sessionId}] [ISS-RECV] Received complete response from ISS");

                // Step 4: Parse the response
                IsoMessage response = _parser.Parse(responseBytes);

                string rcDesc = ConfigurationLoader.Instance.GetResponseDescription(response.GetResponseCode() ?? "96");
                Console.WriteLine($"[{sessionId}] [ISS-RESPONSE] RC: {response.GetResponseCode()} - {rcDesc}");

                return response;
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[{sessionId}] [ISS-ERROR] Socket error: {ex.Message}");
                return CreateSystemErrorResponse(request, "91"); // Issuer unavailable
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine($"[{sessionId}] [ISS-ERROR] Timeout: {ex.Message}");
                return CreateTimeoutResponse(request);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{sessionId}] [ISS-ERROR] Unexpected error: {ex.Message}");
                return CreateSystemErrorResponse(request, "96"); // System malfunction
            }
            finally
            {
                // Always cleanup connections
                stream?.Close();
                issuerClient?.Close();
                Console.WriteLine($"[{sessionId}] [ISS-DISCONNECT] Connection closed");
            }
        }

        /// <summary>
        /// Create a timeout response (RC 68)
        /// </summary>
        private IsoMessage CreateTimeoutResponse(IsoMessage request)
        {
            return CreateSystemErrorResponse(request, "68"); // Response received too late
        }

        /// <summary>
        /// Create a system error response with specified response code
        /// </summary>
        private IsoMessage CreateSystemErrorResponse(IsoMessage request, string responseCode)
        {
            var response = new IsoMessage
            {
                MessageType = request.MessageType switch
                {
                    "0200" => "0210",
                    "0400" => "0410",
                    _ => "0210"
                }
            };

            // Copy essential fields from request
            foreach (var field in new[] { 2, 3, 4, 7, 11, 12, 13, 32, 33, 37, 41, 42 })
            {
                if (request.HasField(field))
                    response.SetField(field, request.GetField(field));
            }

            response.SetResponseCode(responseCode);
            return response;
        }
    }
}
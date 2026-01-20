using System;
using System.Net.Sockets;
using core.Configuration;
using core.Models;
using core.Models.Configuration;
using core.ISO8583;

namespace router
{
    
    /// Handles communication with Issuer (ISS) banks
    /// Forwards authorization requests and receives responses
    /// Uses connection pooling and retry logic for reliability
    
    public class IssuerConnector : IDisposable
    {
        private readonly IsoParser _parser;
        private readonly IssuerConnectionPool _connectionPool;
        private readonly RetryPolicy _retryPolicy;
        private bool _disposed;

        public IssuerConnector()
        {
            _parser = new IsoParser();
            _connectionPool = new IssuerConnectionPool(maxPoolSize: 10, connectionIdleTimeoutMs: 60000);
            _retryPolicy = new RetryPolicy(maxRetries: 3, baseDelayMs: 100, maxDelayMs: 5000);
        }

        
        /// Forward a message to the appropriate Issuer bank and wait for response
        /// Uses connection pooling and retry with exponential backoff
        
        public IsoMessage ForwardToIssuer(IsoMessage request, IssuerBankConfig issuerBank, string sessionId)
        {
            try
            {
                return _retryPolicy.Execute(
                    () => ForwardToIssuerInternal(request, issuerBank, sessionId),
                    RetryPolicy.IsRetryableException,
                    $"ForwardToIssuer-{issuerBank.IssuerCode}"
                );
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[{sessionId}] [ISS-ERROR] Socket error after retries: {ex.Message}");
                return CreateSystemErrorResponse(request, "91"); // Issuer unavailable
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine($"[{sessionId}] [ISS-ERROR] Timeout after retries: {ex.Message}");
                return CreateTimeoutResponse(request);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{sessionId}] [ISS-ERROR] Unexpected error after retries: {ex.Message}");
                return CreateSystemErrorResponse(request, "96"); // System malfunction
            }
        }

        private IsoMessage ForwardToIssuerInternal(IsoMessage request, IssuerBankConfig issuerBank, string sessionId)
        {
            PooledConnection? connection = null;

            try
            {
                Console.WriteLine($"[{sessionId}] [ISS-CONNECT] Connecting to {issuerBank.IssuerName} at {issuerBank.Host}:{issuerBank.Port}");

                // Step 1: Get connection from pool
                connection = _connectionPool.GetConnection(issuerBank);

                Console.WriteLine($"[{sessionId}] [ISS-CONNECT] Connection established");

                // Step 2: Build and send the ISO-8583 message
                byte[] requestBytes = _parser.Build(request);

                // Prepare length header (2 bytes, big-endian)
                byte[] lengthHeader = new byte[2];
                lengthHeader[0] = (byte)(requestBytes.Length >> 8);
                lengthHeader[1] = (byte)(requestBytes.Length & 0xFF);

                // Send to ISS
                connection.Stream.Write(lengthHeader, 0, 2);
                connection.Stream.Write(requestBytes, 0, requestBytes.Length);
                connection.Stream.Flush();

                Console.WriteLine($"[{sessionId}] [ISS-SEND] Sent {requestBytes.Length} bytes to ISS");

                // Step 3: Receive response from ISS
                byte[] responseLengthBytes = new byte[2];
                if (!TryReadExact(connection.Stream, responseLengthBytes, 0, 2))
                {
                    Console.WriteLine($"[{sessionId}] [ISS-ERROR] Failed to read response length header");
                    connection.MarkAsFailed();
                    throw new System.IO.IOException("Failed to read response length header");
                }

                int responseLength = (responseLengthBytes[0] << 8) | responseLengthBytes[1];

                Console.WriteLine($"[{sessionId}] [ISS-RECV] Expecting {responseLength} bytes from ISS");

                if (responseLength <= 0)
                {
                    Console.WriteLine($"[{sessionId}] [ISS-ERROR] Invalid response length: {responseLength}");
                    connection.MarkAsFailed();
                    throw new System.IO.IOException("Invalid response length");
                }

                // Read the full response
                byte[] responseBytes = new byte[responseLength];
                if (!TryReadExact(connection.Stream, responseBytes, 0, responseLength))
                {
                    Console.WriteLine($"[{sessionId}] [ISS-ERROR] Connection closed while reading response");
                    connection.MarkAsFailed();
                    throw new System.IO.IOException("Connection closed while reading response");
                }

                Console.WriteLine($"[{sessionId}] [ISS-RECV] Received complete response from ISS");

                // Step 4: Parse the response
                IsoMessage response = _parser.Parse(responseBytes);

                string rcDesc = ConfigurationLoader.Instance.GetResponseDescription(response.GetResponseCode() ?? "96");
                Console.WriteLine($"[{sessionId}] [ISS-RESPONSE] RC: {response.GetResponseCode()} - {rcDesc}");

                return response;
            }
            catch (Exception)
            {
                // Mark connection as failed so it's not returned to pool
                connection?.MarkAsFailed();
                throw;
            }
            finally
            {
                // Return connection to pool (or close if marked as failed)
                connection?.Dispose();
            }
        }

        private bool TryReadExact(NetworkStream stream, byte[] buffer, int offset, int count)
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

        public ConnectionPoolStats GetPoolStats() => _connectionPool.GetStats();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _connectionPool.Dispose();
        }

        
        /// Create a timeout response (RC 68)
        
        private IsoMessage CreateTimeoutResponse(IsoMessage request)
        {
            return CreateSystemErrorResponse(request, "68"); // Response received too late
        }

        
        /// Create a system error response with specified response code
        
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
using System;
using System.Linq;
using System.Net.Sockets;
using core.Configuration;
using core.Models;
using core.Models.Configuration;
using core.ISO8583;
using core.Security;
using core.Helpers;

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
            // Sync wrapper for backward compatibility
            return ForwardToIssuerAsync(request, issuerBank, sessionId).GetAwaiter().GetResult();
        }

        public async Task<IsoMessage> ForwardToIssuerAsync(IsoMessage request, IssuerBankConfig issuerBank, string sessionId)
        {
            try
            {
                // We wrap the internal internal logic in the retry policy
                // Note: If RetryPolicy support async, we should use it. 
                // For now, keeping it simple or wrapping Task.Run if needed.
                return await Task.Run(() => _retryPolicy.Execute(
                    () => ForwardToIssuerInternal(request, issuerBank, sessionId),
                    RetryPolicy.IsRetryableException,
                    $"ForwardToIssuer-{issuerBank.IssuerCode}"
                ));
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

                // Step 2: Build and send the ISO-8583 message
                byte[] requestBytes = _parser.Build(request);

                // Prepare length header (2 bytes, big-endian)
                byte[] lengthHeader = new byte[2];
                lengthHeader[0] = (byte)(requestBytes.Length >> 8);
                lengthHeader[1] = (byte)(requestBytes.Length & 0xFF);

                // Log request before forwarding to ISS
                MessageLogger.LogMessage(sessionId, "ISS-FORWARD", request);
                // Send to ISS
                connection.Stream.Write(lengthHeader, 0, 2);
                connection.Stream.Write(requestBytes, 0, requestBytes.Length);
                connection.Stream.Flush();

                Console.WriteLine($"[{sessionId}] [ISS-SEND] {request.MessageType} | TRN: {request.GetTRN() ?? "N/A"}");

                // Step 3: Receive response from ISS
                // First, try to read initial bytes to detect the format
                byte[] initialBytes = new byte[4];
                int initialRead = 0;
                
                // Set a reasonable read timeout
                connection.Stream.ReadTimeout = 30000;
                
                try
                {
                    // Try to read first 4 bytes to detect format
                    initialRead = connection.Stream.Read(initialBytes, 0, 4);
                }
                catch (System.IO.IOException ex)
                {
                    Console.WriteLine($"[{sessionId}] [ISS-ERROR] Timeout or error reading from TS: {ex.Message}");
                    connection.MarkAsFailed();
                    throw;
                }

                if (initialRead == 0)
                {
                    Console.WriteLine($"[{sessionId}] [ISS-ERROR] TS closed connection without response");
                    connection.MarkAsFailed();
                    throw new System.IO.IOException("TS closed connection without response");
                }

                // Technical debug logs moved to file/removed for console clarity
                // Console.WriteLine($"[{sessionId}] [ISS-DEBUG] Initial {initialRead} bytes: {BitConverter.ToString(initialBytes, 0, initialRead)}");
                
                int responseLength;
                byte[] responseBytes;
                int dataOffset = 0;

                // Try to detect response format
                // Check if first 2 bytes look like a valid 2-byte length (common format)
                int len2Byte = (initialBytes[0] << 8) | initialBytes[1];
                
                // Check if first 4 bytes look like a 4-byte length
                int len4Byte = (initialBytes[0] << 24) | (initialBytes[1] << 16) | (initialBytes[2] << 8) | initialBytes[3];

                // Check if response starts with MTI (e.g., "0210" = 30 32 31 30)
                bool startsWithMti = initialRead >= 4 && 
                    initialBytes[0] == 0x30 && // '0'
                    (initialBytes[1] == 0x32 || initialBytes[1] == 0x34 || initialBytes[1] == 0x38) && // '2', '4', or '8'
                    initialBytes[2] == 0x31 && // '1'
                    initialBytes[3] == 0x30;   // '0'

                if (startsWithMti)
                {
                    // No length header - response starts directly with MTI
                    // Need to read until connection closes or timeout
                    Console.WriteLine($"[{sessionId}] [ISS-DEBUG] Detected: Response starts with MTI (no length header)");
                    
                    var buffer = new System.IO.MemoryStream();
                    buffer.Write(initialBytes, 0, initialRead);
                    
                    byte[] chunk = new byte[1024];
                    int chunkRead;
                    connection.Stream.ReadTimeout = 2000; // Short timeout for remaining data
                    
                    try
                    {
                        while ((chunkRead = connection.Stream.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            buffer.Write(chunk, 0, chunkRead);
                        }
                    }
                    catch (System.IO.IOException) { /* Timeout is expected */ }
                    
                    responseBytes = buffer.ToArray();
                    responseLength = responseBytes.Length;
                    Console.WriteLine($"[{sessionId}] [ISS-DEBUG] Read {responseLength} bytes (no length header format)");
                }
                else 
                {
                    // Detect and handle length header (4-byte ASCII, 4-byte binary, or 2-byte binary)
                    int messageLength = 0;
                    string lengthStr = System.Text.Encoding.ASCII.GetString(initialBytes);
                    
                    int binLen4 = (initialBytes[0] << 24) | (initialBytes[1] << 16) | (initialBytes[2] << 8) | initialBytes[3];
                    int binLen2 = (initialBytes[0] << 8) | initialBytes[1];

                    if (int.TryParse(lengthStr, out int asciiLen) && asciiLen > 0 && asciiLen < 65535)
                    {
                        messageLength = asciiLen;
                        Console.WriteLine($"[{sessionId}] [ISS-DEBUG] Detected 4-byte ASCII length header: {lengthStr} (len={messageLength})");
                    }
                    else if (binLen4 > 0 && binLen4 < 65535)
                    {
                        messageLength = binLen4;
                        Console.WriteLine($"[{sessionId}] [ISS-DEBUG] Detected 4-byte binary length header. len={messageLength}");
                    }
                    else if (binLen2 > 0 && binLen2 < 65535)
                    {
                        messageLength = binLen2;
                        Console.WriteLine($"[{sessionId}] [ISS-DEBUG] Detected 2-byte binary length header. len={messageLength}");
                    }
                    else
                    {
                        Console.WriteLine($"[{sessionId}] [ISS-ERROR] Unknown response format or invalid length!");
                        Console.WriteLine($"[{sessionId}] [ISS-DEBUG] Raw header: {BitConverter.ToString(initialBytes, 0, initialRead)}");
                        connection.MarkAsFailed();
                        throw new System.IO.IOException($"Invalid message length header from TS");
                    }

                    responseLength = messageLength;
                    responseBytes = new byte[responseLength];

                    int bytesToCopyFromInitial = 0;
                    if (messageLength == binLen2) // If 2-byte binary was the detected format
                    {
                        bytesToCopyFromInitial = Math.Min(initialRead - 2, responseLength);
                        Array.Copy(initialBytes, 2, responseBytes, 0, bytesToCopyFromInitial);
                    }
                    else // 4-byte formats
                    {
                        // In Case of 4-byte header, the initial bytes were ALL length
                        bytesToCopyFromInitial = 0; 
                    }

                    if (!TryReadExact(connection.Stream, responseBytes, bytesToCopyFromInitial, responseLength - bytesToCopyFromInitial))
                    {
                        Console.WriteLine($"[{sessionId}] [ISS-ERROR] Failed to read complete response");
                        connection.MarkAsFailed();
                        throw new System.IO.IOException("Failed to read complete response");
                    }
                }

                // Binary/ASCII dumps moved to file/removed for console clarity
                // Console.WriteLine($"[{sessionId}] [ISS-DEBUG] Response HEX: {BitConverter.ToString(responseBytes).Replace("-", " ")}");
                // string asciiResponse = new string(responseBytes.Select(b => b >= 32 && b <= 126 ? (char)b : '.').ToArray());
                // Console.WriteLine($"[{sessionId}] [ISS-DEBUG] Response ASCII: {asciiResponse}");
                // Console.WriteLine($"[{sessionId}] [ISS-DEBUG] === END RESPONSE ===");
                // Step 4: Parse the response
                IsoMessage response = _parser.Parse(responseBytes);
                string rc = response.GetResponseCode() ?? "96";

                // Log response received from ISS
                MessageLogger.LogMessage(sessionId, "ISS-RECV", response);
                string respRc = response.GetResponseCode() ?? "00";
                string respTrn = response.GetTRN() ?? request.GetTRN() ?? "N/A";
                Console.WriteLine($"[{sessionId}] [ISS-RECV] {response.MessageType} | TRN: {respTrn} | RC: {respRc}");

                if (rc == "30")
                {
                    Console.WriteLine($"[{sessionId}] [ISS-ALERT] Format Error (RC 30) received! Check DE32, DE33, or DE14 padding.");
                }

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

        private static string MaskTrack2(string? track2)
        {
            if (string.IsNullOrEmpty(track2)) return "";
            // TESTING: Unmasking per user request
            return track2;
        }
    }
}
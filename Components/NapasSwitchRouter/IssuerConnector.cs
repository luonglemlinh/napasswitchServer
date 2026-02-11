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

        public async Task<IsoMessage> ForwardToIssuerAsync(IsoMessage request, IssuerBankConfig issuerBank, string sessionId, CancellationToken cancellationToken = default)
        {
            try
            {
                int retryCount = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        return await ForwardToIssuerInternalAsync(request, issuerBank, sessionId, cancellationToken);
                    }
                    catch (Exception ex) when (retryCount < 3 && RetryPolicy.IsRetryableException(ex))
                    {
                        retryCount++;
                        int delay = 100 * (int)Math.Pow(2, retryCount - 1);
                        Console.WriteLine($"[{sessionId}] [ISS-RETRY] Retry {retryCount}/3 for {issuerBank.IssuerName} after {delay}ms. Error: {ex.Message}");
                        await Task.Delay(delay, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[{sessionId}] [ISS-CANCEL] Operation cancelled");
                return IsoResponseBuilder.CreateSystemErrorResponse(request, "96");
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[{sessionId}] [ISS-ERROR] Socket error after retries: {ex.Message}");
                return IsoResponseBuilder.CreateSystemErrorResponse(request, "91");
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine($"[{sessionId}] [ISS-ERROR] Timeout after retries: {ex.Message}");
                return IsoResponseBuilder.CreateTimeoutResponse(request);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{sessionId}] [ISS-ERROR] Unexpected error after retries: {ex.Message}");
                return IsoResponseBuilder.CreateSystemErrorResponse(request, "96");
            }
        }

        private async Task<IsoMessage> ForwardToIssuerInternalAsync(IsoMessage request, IssuerBankConfig issuerBank, string sessionId, CancellationToken cancellationToken = default)
        {
            PooledConnection? connection = null;

            try
            {
                Console.WriteLine($"[{sessionId}] [ISS-CONNECT] Connecting to {issuerBank.IssuerName} at {issuerBank.Host}:{issuerBank.Port}");

                // Step 1: Get connection from pool
                // Note: GetConnection is currently sync. In a full async refactor, this should be async too.
                // For now, we accept this localized blocking call or wrap it if it takes time.
                connection = _connectionPool.GetConnection(issuerBank);

                // Step 2: Build and send the ISO-8583 message
                byte[] requestBytes = _parser.Build(request);

                // Prepare length header (2 bytes, big-endian)
                byte[] lengthHeader = new byte[2];
                lengthHeader[0] = (byte)(requestBytes.Length >> 8);
                lengthHeader[1] = (byte)(requestBytes.Length & 0xFF);

                // Log request before forwarding to ISS
                MessageLogger.LogMessage(sessionId, "ISS forward", request);
                
                // Send to ISS (Async)
                await connection.Stream.WriteAsync(lengthHeader, 0, 2);
                await connection.Stream.WriteAsync(requestBytes, 0, requestBytes.Length);
                await connection.Stream.FlushAsync();

                Console.WriteLine($"[{sessionId}] [ISS-SEND] {request.MessageType} | TRN: {request.GetTRN() ?? "N/A"}");

                // Step 3: Receive response from ISS
                byte[] initialBytes = new byte[4];
                int initialRead = 0;
                
                // Set a reasonable read timeout
                // NetworkStream.ReadAsync respects ReadTimeout in modern .NET but mostly relies on cancellation tokens.
                // We'll use a CancellationToken source for timeout.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(30000);
                
                try
                {
                    // Try to read first 4 bytes to detect format
                    initialRead = await connection.Stream.ReadAsync(initialBytes, 0, 4, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"[{sessionId}] [ISS-ERROR] Timeout reading from TS");
                    connection.MarkAsFailed();
                    throw new TimeoutException("Timeout waiting for response header");
                }
                catch (System.IO.IOException ex)
                {
                    Console.WriteLine($"[{sessionId}] [ISS-ERROR] Error reading from TS: {ex.Message}");
                    connection.MarkAsFailed();
                    throw;
                }

                if (initialRead == 0)
                {
                    Console.WriteLine($"[{sessionId}] [ISS-ERROR] TS closed connection without response");
                    connection.MarkAsFailed();
                    throw new System.IO.IOException("TS closed connection without response");
                }
                
                int responseLength;
                byte[] responseBytes;

                // Try to detect response format
                // Check if response starts with MTI (e.g., "0210" = 30 32 31 30)
                bool startsWithMti = initialRead >= 4 && 
                    initialBytes[0] == 0x30 && 
                    (initialBytes[1] == 0x32 || initialBytes[1] == 0x34 || initialBytes[1] == 0x38);

                if (startsWithMti)
                {
                    // No length header
                    Console.WriteLine($"[{sessionId}] [ISS-DEBUG] Detected: Response starts with MTI");
                    
                    using var buffer = new System.IO.MemoryStream();
                    buffer.Write(initialBytes, 0, initialRead);
                    
                    byte[] chunk = new byte[1024];
                    int chunkRead;
                    
                    // Short timeout for remaining data
                    using var chunkCts = new CancellationTokenSource(2000);
                    
                    try
                    {
                        while ((chunkRead = await connection.Stream.ReadAsync(chunk, 0, chunk.Length, chunkCts.Token)) > 0)
                        {
                            buffer.Write(chunk, 0, chunkRead);
                        }
                    }
                    catch (OperationCanceledException) { /* Expected end of stream if no closure */ }
                    
                    responseBytes = buffer.ToArray();
                    responseLength = responseBytes.Length;
                }
                else 
                {
                    // Detect and handle length header
                    int messageLength = 0;
                    string lengthStr = System.Text.Encoding.ASCII.GetString(initialBytes);
                    
                    int binLen4 = (initialBytes[0] << 24) | (initialBytes[1] << 16) | (initialBytes[2] << 8) | initialBytes[3];
                    int binLen2 = (initialBytes[0] << 8) | initialBytes[1];

                    if (int.TryParse(lengthStr, out int asciiLen) && asciiLen > 0 && asciiLen < 65535)
                        messageLength = asciiLen;
                    else if (binLen4 > 0 && binLen4 < 65535)
                        messageLength = binLen4;
                    else if (binLen2 > 0 && binLen2 < 65535)
                        messageLength = binLen2;
                    else
                    {
                        Console.WriteLine($"[{sessionId}] [ISS-ERROR] Unknown response format!");
                        connection.MarkAsFailed();
                        throw new System.IO.IOException($"Invalid message length header");
                    }

                    responseLength = messageLength;
                    responseBytes = new byte[responseLength];

                    int bytesToCopy = 0;
                    if (messageLength == binLen2) // 2-byte binary
                    {
                        bytesToCopy = Math.Min(initialRead - 2, responseLength);
                        Array.Copy(initialBytes, 2, responseBytes, 0, bytesToCopy);
                    }

                    if (!await NetworkStreamHelper.TryReadExactAsync(connection.Stream, responseBytes, bytesToCopy, responseLength - bytesToCopy, cts.Token))
                    {
                        Console.WriteLine($"[{sessionId}] [ISS-ERROR] Failed to read complete response");
                        connection.MarkAsFailed();
                        throw new System.IO.IOException("Failed to read complete response");
                    }
                }

                // Step 4: Parse the response
                IsoMessage response = _parser.Parse(responseBytes);
                string rc = response.GetResponseCode() ?? "96";

                // Log response received from ISS
                MessageLogger.LogMessage(sessionId, "ISS received", response);
                string respRc = response.GetResponseCode() ?? "00";
                string respTrn = response.GetTRN() ?? request.GetTRN() ?? "N/A";
                Console.WriteLine($"[{sessionId}] [ISS-RECV] {response.MessageType} | TRN: {respTrn} | RC: {respRc}");

                if (rc == "30")
                {
                    Console.WriteLine($"[{sessionId}] [ISS-ALERT] Format Error (RC 30) received!");
                }

                return response;
            }
            catch (Exception)
            {
                connection?.MarkAsFailed();
                throw;
            }
            finally
            {
                connection?.Dispose();
            }
        }

        public ConnectionPoolStats GetPoolStats() => _connectionPool.GetStats();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _connectionPool.Dispose();
        }

        private static string MaskTrack2(string? track2)
        {
            if (string.IsNullOrEmpty(track2)) return "";
            // TESTING: Unmasking per user request
            return track2;
        }
    }
}
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
        /// NOTE: Use ForwardToIssuerAsync instead - this sync wrapper is deprecated and may cause deadlocks

        [Obsolete("Use ForwardToIssuerAsync instead. This sync wrapper can cause deadlocks.")]
        public IsoMessage ForwardToIssuer(IsoMessage request, IssuerBankConfig issuerBank, string sessionId)
        {
            // Removed sync-over-async anti-pattern. Callers should use ForwardToIssuerAsync.
            throw new NotSupportedException("Use ForwardToIssuerAsync instead. Sync-over-async has been removed to prevent deadlocks.");
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
                        SwitchLogger.Info($"[{sessionId}] [ISS-RETRY] Retry {retryCount}/3 for {issuerBank.IssuerName} after {delay}ms. Error: {ex.Message}");
                        await Task.Delay(delay, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                SwitchLogger.Info($"[{sessionId}] [ISS-CANCEL] Operation cancelled");
                return IsoResponseBuilder.CreateSystemErrorResponse(request, "96");
            }
            catch (SocketException ex)
            {
                SwitchLogger.Info($"[{sessionId}] [ISS-ERROR] Socket error after retries: {ex.Message}");
                return IsoResponseBuilder.CreateSystemErrorResponse(request, "91");
            }
            catch (TimeoutException ex)
            {
                SwitchLogger.Info($"[{sessionId}] [ISS-ERROR] Timeout after retries: {ex.Message}");
                return IsoResponseBuilder.CreateTimeoutResponse(request);
            }
            catch (Exception ex)
            {
                SwitchLogger.Info($"[{sessionId}] [ISS-ERROR] Unexpected error after retries: {ex.Message}");
                return IsoResponseBuilder.CreateSystemErrorResponse(request, "96");
            }
        }

        private async Task<IsoMessage> ForwardToIssuerInternalAsync(IsoMessage request, IssuerBankConfig issuerBank, string sessionId, CancellationToken cancellationToken = default)
        {
            PooledConnection? connection = null;

            try
            {
                SwitchLogger.Info($"[{sessionId}] [ISS-CONNECT] Connecting to {issuerBank.IssuerName} at {issuerBank.Host}:{issuerBank.Port}");

                // Step 1: Get connection from pool (fully async — no thread blocking)
                connection = await _connectionPool.GetConnectionAsync(issuerBank);

                // Step 2: Build and send the ISO-8583 message
                byte[] requestBytes = _parser.Build(request);

                // Send with configured wire format (length header style)
                string wireFormat = issuerBank.WireFormat?.ToUpperInvariant() ?? "BIN2";
                switch (wireFormat)
                {
                    case "ASCII4":
                        byte[] ascii4Header = System.Text.Encoding.ASCII.GetBytes(requestBytes.Length.ToString("D4"));
                        await connection.Stream.WriteAsync(ascii4Header, 0, 4);
                        break;
                    case "BIN4":
                        byte[] bin4Header = new byte[4];
                        bin4Header[0] = (byte)(requestBytes.Length >> 24);
                        bin4Header[1] = (byte)(requestBytes.Length >> 16);
                        bin4Header[2] = (byte)(requestBytes.Length >> 8);
                        bin4Header[3] = (byte)(requestBytes.Length & 0xFF);
                        await connection.Stream.WriteAsync(bin4Header, 0, 4);
                        break;
                    case "NONE":
                        // No length header — raw MTI start
                        break;
                    case "BIN2":
                    default:
                        byte[] bin2Header = new byte[2];
                        bin2Header[0] = (byte)(requestBytes.Length >> 8);
                        bin2Header[1] = (byte)(requestBytes.Length & 0xFF);
                        await connection.Stream.WriteAsync(bin2Header, 0, 2);
                        break;
                }

                // Log request before forwarding to ISS
                MessageLogger.LogMessage(sessionId, "ISS forward", request);

                // Send to ISS (Async)
                await connection.Stream.WriteAsync(requestBytes, 0, requestBytes.Length);
                await connection.Stream.FlushAsync();

                SwitchLogger.Info("[{SessionId}] [ISS-SEND] {MTI} | TRN: {TRN}", sessionId, request.MessageType, request.GetTRN() ?? "N/A");

                // Step 3: Receive response from ISS using configured wire format
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(issuerBank.Timeout > 0 ? issuerBank.Timeout : 30000);

                int responseLength;
                byte[] responseBytes;

                try
                {
                    switch (wireFormat)
                    {
                        case "ASCII4":
                        {
                            byte[] hdr = new byte[4];
                            if (!await NetworkStreamHelper.TryReadExactAsync(connection.Stream, hdr, 0, 4, cts.Token))
                                throw new System.IO.IOException("Failed to read 4-byte ASCII length header");
                            string lenStr = System.Text.Encoding.ASCII.GetString(hdr);
                            if (!int.TryParse(lenStr, out responseLength) || responseLength <= 0 || responseLength > 65535)
                                throw new System.IO.IOException($"Invalid ASCII4 length: {lenStr}");
                            responseBytes = new byte[responseLength];
                            if (!await NetworkStreamHelper.TryReadExactAsync(connection.Stream, responseBytes, 0, responseLength, cts.Token))
                                throw new System.IO.IOException("Failed to read complete response");
                            break;
                        }
                        case "BIN4":
                        {
                            byte[] hdr = new byte[4];
                            if (!await NetworkStreamHelper.TryReadExactAsync(connection.Stream, hdr, 0, 4, cts.Token))
                                throw new System.IO.IOException("Failed to read 4-byte binary length header");
                            responseLength = (hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
                            if (responseLength <= 0 || responseLength > 65535)
                                throw new System.IO.IOException($"Invalid BIN4 length: {responseLength}");
                            responseBytes = new byte[responseLength];
                            if (!await NetworkStreamHelper.TryReadExactAsync(connection.Stream, responseBytes, 0, responseLength, cts.Token))
                                throw new System.IO.IOException("Failed to read complete response");
                            break;
                        }
                        case "NONE":
                        {
                            // No length header — read until stream ends or timeout
                            using var buffer = new System.IO.MemoryStream();
                            byte[] chunk = new byte[4096];
                            int chunkRead;
                            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                            readCts.CancelAfter(2000);
                            try
                            {
                                while ((chunkRead = await connection.Stream.ReadAsync(chunk, 0, chunk.Length, readCts.Token)) > 0)
                                    buffer.Write(chunk, 0, chunkRead);
                            }
                            catch (OperationCanceledException) { /* Expected — end of stream */ }
                            responseBytes = buffer.ToArray();
                            responseLength = responseBytes.Length;
                            if (responseLength == 0) throw new System.IO.IOException("TS closed connection without response");
                            break;
                        }
                        case "BIN2":
                        default:
                        {
                            byte[] hdr = new byte[2];
                            if (!await NetworkStreamHelper.TryReadExactAsync(connection.Stream, hdr, 0, 2, cts.Token))
                                throw new System.IO.IOException("Failed to read 2-byte binary length header");
                            responseLength = (hdr[0] << 8) | hdr[1];
                            if (responseLength <= 0 || responseLength > 65535)
                                throw new System.IO.IOException($"Invalid BIN2 length: {responseLength}");
                            responseBytes = new byte[responseLength];
                            if (!await NetworkStreamHelper.TryReadExactAsync(connection.Stream, responseBytes, 0, responseLength, cts.Token))
                                throw new System.IO.IOException("Failed to read complete response");
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    SwitchLogger.Info($"[{sessionId}] [ISS-ERROR] Timeout reading from TS ({wireFormat})");
                    connection.MarkAsFailed();
                    throw new TimeoutException($"Timeout waiting for response (wire format: {wireFormat})");
                }
                catch (System.IO.IOException ex)
                {
                    SwitchLogger.Info($"[{sessionId}] [ISS-ERROR] Error reading from TS: {ex.Message}");
                    connection.MarkAsFailed();
                    throw;
                }

                // Step 4: Parse the response
                IsoMessage response = _parser.Parse(responseBytes);
                string rc = response.GetResponseCode() ?? "96";

                // Log response received from ISS
                MessageLogger.LogMessage(sessionId, "ISS received", response);
                string respRc = response.GetResponseCode() ?? "00";
                string respTrn = response.GetTRN() ?? request.GetTRN() ?? "N/A";
                SwitchLogger.Info($"[{sessionId}] [ISS-RECV] {response.MessageType} | TRN: {respTrn} | RC: {respRc}");

                if (rc == "30")
                {
                    SwitchLogger.Info($"[{sessionId}] [ISS-ALERT] Format Error (RC 30) received!");
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
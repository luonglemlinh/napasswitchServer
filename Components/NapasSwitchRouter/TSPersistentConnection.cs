using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;
using core.Configuration;
using core.Models;
using core.Models.Configuration;
using core.ISO8583;
using core.Security;

namespace router
{
    /// <summary>
    /// Manages a persistent connection to the Transaction Switch (TS)
    /// - Full Duplex: Listens for incoming requests (Echo) while allowing outbound transactions.
    /// - Maintains a single long-lived connection.
    /// - Sends periodic heartbeat (0800) messages.
    /// - Auto-reconnects on failure.
    /// </summary>
    public class TSPersistentConnection : IDisposable
    {
        private readonly IssuerBankConfig _tsConfig;
        private readonly IsoParser _parser;
        private readonly object _writeLock = new object();
        
        private TcpClient? _client;
        private NetworkStream? _stream;
        private Timer? _heartbeatTimer;
        private CancellationTokenSource? _connectionCts;
        
        private bool _isConnected;
        private bool _disposed;
        private int _heartbeatIntervalMs;
        private int _stan = 1;

        // Track pending requests by STAN (DE11) to correlate responses
        private readonly ConcurrentDictionary<string, TaskCompletionSource<IsoMessage>> _pendingResponses = new();

        public bool IsConnected => _isConnected && _client?.Connected == true;
        public string TSName => _tsConfig.IssuerName;

        public TSPersistentConnection(IssuerBankConfig tsConfig, int heartbeatIntervalMs = 75000)
        {
            _tsConfig = tsConfig ?? throw new ArgumentNullException(nameof(tsConfig));
            // Uses default NapasSchema (Fixed/Variable DE32)
            _parser = new IsoParser();
            _parser.UseHexBitmap = true; // Tutor's TS requires ASCII Hex Bitmap
            _heartbeatIntervalMs = heartbeatIntervalMs;
        }

        /// <summary>
        /// Connect to TS, start listener, and send sign-on message
        /// </summary>
        public async Task<bool> ConnectAsync()
        {
            if (IsConnected) return true;

            try
            {
                Console.WriteLine($"[TS-CONN] Connecting to {_tsConfig.IssuerName} at {_tsConfig.Host}:{_tsConfig.Port}...");

                _client = new TcpClient();
                await _client.ConnectAsync(_tsConfig.Host, _tsConfig.Port);
                _stream = _client.GetStream();
                
                // Note: Don't set ReadTimeout for infinite listener loop
                _stream.WriteTimeout = _tsConfig.Timeout;

                _connectionCts = new CancellationTokenSource();
                _isConnected = true;

                Console.WriteLine($"[TS-CONN] TCP connection established to {_tsConfig.Host}:{_tsConfig.Port}");

                // Start background receive loop
                _ = ReceiveLoopAsync(_connectionCts.Token);

                // Send sign-on message (0800)
                bool signOnSuccess = await SendSignOnAsync();
                
                if (signOnSuccess)
                {
                    // Start heartbeat timer
                    StartHeartbeat();
                    Console.WriteLine($"[TS-CONN] Successfully connected and signed on to {_tsConfig.IssuerName}");
                    return true;
                }
                else
                {
                    Console.WriteLine($"[TS-CONN] Sign-on failed, closing connection");
                    Disconnect();
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TS-CONN] Connection failed: {ex.Message}");
                Disconnect();
                return false;
            }
        }

        /// <summary>
        /// Continuous background loop to read incoming messages
        /// </summary>
        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            Console.WriteLine("[TS-RECV] Started background receive loop");
            var buffer = new byte[8192];

            try
            {
                while (!token.IsCancellationRequested && _stream != null)
                {
                    // 1. Read 4-byte Length Header
                    byte[] lenBytes = new byte[4];
                    int bytesRead = await ReadExactAsync(_stream, lenBytes, 0, 4, token);
                    if (bytesRead == 0) break; // Socket closed

                    string lenStr = System.Text.Encoding.ASCII.GetString(lenBytes);
                    if (!int.TryParse(lenStr, out int msgLen))
                    {
                        Console.WriteLine($"[TS-RECV] Invalid length header: {lenStr}");
                        break;
                    }

                    // 2. Read Payload
                    byte[] payload = new byte[msgLen];
                    bytesRead = await ReadExactAsync(_stream, payload, 0, msgLen, token);
                    if (bytesRead != msgLen) break;

                    // 3. Process Message
                    ProcessIncomingPayload(payload);
                }
            }
            catch (OperationCanceledException) { /* Graceful shutdown */ }
            catch (Exception ex)
            {
                Console.WriteLine($"[TS-RECV] Error in receive loop: {ex.Message}");
            }
            finally
            {
                Console.WriteLine("[TS-RECV] Receive loop stopped");
                if (!token.IsCancellationRequested) Disconnect();
            }
        }

        private async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, token);
                if (read == 0) return 0;
                totalRead += read;
            }
            return totalRead;
        }

        private void ProcessIncomingPayload(byte[] rawData)
        {
            try
            {
                 var msg = _parser.Parse(rawData);
                 string maskedPan = SecureDataHandler.MaskPAN(msg.GetField(2));
                 string rc = msg.GetResponseCode();
                 Console.WriteLine($"[TS-RECV] MTI: {msg.MessageType} | PAN: {maskedPan} | STAN: {msg.GetField(11)} | RC: {rc}");
                 
                 if (rc == "30")
                 {
                     Console.WriteLine("[TS-ALERT] Format Error (RC 30) received from TS! This often means DE32 (Acquirer ID) or DE33 (Forwarding ID) is invalid for this routing.");
                 }

                 HandleParsedMessage(msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TS-RECV] Failed to parse incoming message: {ex.Message}");
            }
        }

        private void HandleParsedMessage(IsoMessage msg)
        {
            string mti = msg.MessageType;
            string stan = msg.Fields.ContainsKey(11) ? msg.Fields[11] : "000000";

            Console.WriteLine($"[TS-RECV] Received MTI={mti} STAN={stan}");

            if (mti == "0800")
            {
                // Incoming Echo Request -> Send 0810 Response
                Console.WriteLine($"[TS-RECV] Handling Check/Heartbeat Request from TS");
                _ = SendEchoResponseAsync(msg);
            }
            else if (IsResponseMTI(mti))
            {
                // Response to our request -> Find TCS and complete it
                if (_pendingResponses.TryRemove(stan, out var tcs))
                {
                    tcs.TrySetResult(msg);
                }
                else
                {
                    Console.WriteLine($"[TS-RECV] Warning: Unmatched response received for STAN={stan}");
                }
            }
        }

        private bool IsResponseMTI(string mti)
        {
            // NAPAS: 0200/0210, 0400/0410, 0800/0810.
            // So if 3rd char is '1', it's a response.
            return mti.Length == 4 && mti[2] == '1';
        }

        private async Task SendEchoResponseAsync(IsoMessage request)
        {
            try
            {
                var response = new IsoMessage { MessageType = "0810" };
                
                // Copy essential fields
                if (request.Fields.ContainsKey(7)) response.SetField(7, request.Fields[7]);
                if (request.Fields.ContainsKey(11)) response.SetField(11, request.Fields[11]); // Echo STAN
                if (request.Fields.ContainsKey(70)) response.SetField(70, request.Fields[70]); // Network Info Code

                // Set Response Code 00 (Success)
                response.SetField(39, "00");

                Console.WriteLine($"[TS-AUTO] Sending Echo Response (0810) for STAN={response.Fields[11]}");
                await SendMessageInternalAsync(response, "AUTO-ECHO", isResponse: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TS-AUTO] Failed to send echo response: {ex.Message}");
            }
        }

        /// <summary>
        /// Send sign-on (0800) message to TS
        /// </summary>
        private async Task<bool> SendSignOnAsync()
        {
            var signOnMsg = BuildNetworkMessage("001"); // 001 = Sign-on
            Console.WriteLine($"[TS-CONN] Sending sign-on (0800)...");

            var response = await SendRequestAsync(signOnMsg, "SIGN-ON");
            
            if (response != null)
            {
                string rc = response.GetResponseCode() ?? "96";
                Console.WriteLine($"[TS-CONN] Sign-on response: RC={rc}");
                return rc == "00";
            }
            return false;
        }

        private void StartHeartbeat()
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = new Timer(async _ => await SendHeartbeatAsync(), null, _heartbeatIntervalMs, _heartbeatIntervalMs);
            Console.WriteLine($"[TS-CONN] Heartbeat started (every {_heartbeatIntervalMs / 1000}s)");
        }

        private async Task SendHeartbeatAsync()
        {
            if (!IsConnected)
            {
                Console.WriteLine($"[TS-HEARTBEAT] Connection lost, attempting reconnect...");
                await ConnectAsync();
                return;
            }

            var heartbeatMsg = BuildNetworkMessage("301"); // 301 = Echo test
            try
            {
                var response = await SendRequestAsync(heartbeatMsg, "HEARTBEAT");
                if (response == null || response.GetResponseCode() != "00")
                {
                     Console.WriteLine($"[TS-HEARTBEAT] Failed (RC={response?.GetResponseCode() ?? "Timeout"}). Reconnecting...");
                     await ReconnectAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TS-HEARTBEAT] Error: {ex.Message}");
                await ReconnectAsync();
            }
        }

        private IsoMessage BuildNetworkMessage(string networkCode)
        {
            var now = DateTime.Now;
            var message = new IsoMessage { MessageType = "0800" };
            
            message.SetField(7, now.ToString("MMddHHmmss"));
            message.SetField(11, Interlocked.Increment(ref _stan).ToString("D6"));
            
            // DE32: Acquiring Institution Identification Code (NAPAS Requirement)
            // Specification: n..11, LLVAR encoding
            // - This field is REQUIRED in all messages for transaction routing
            // - Contains Acquirer's ID number (typically 6-digit BIN code)
            // - Encoded as: [2-byte length][variable data]
            // - Length field: Zero-padded ASCII (e.g., "06" for 6 digits)
            // Example: Acquirer ID "970400" → Wire format "06970400"
            //          where "06" indicates 6 digits follow, then "970400" is the actual ID
            string acquirerId = _tsConfig.IssuerCode ?? "970488";
            
            // Validate acquirer ID format (should be 6-11 numeric digits per NAPAS)
            if (string.IsNullOrEmpty(acquirerId) || acquirerId.Length < 6 || acquirerId.Length > 11)
            {
                Console.WriteLine($"[TS-WARN] Invalid Acquirer ID '{acquirerId}', using default '970488'");
                acquirerId = "970400";
            }
            
            message.SetField(32, acquirerId);

            message.SetField(70, networkCode);
            return message;
        }

        public async Task<IsoMessage?> ForwardTransactionAsync(IsoMessage request, string sessionId)
        {
            if (!IsConnected) await ConnectAsync();
            
            // Log all fields being forwarded to TS for debugging
            Console.WriteLine($"[{sessionId}] [TS-FWD] Forwarding to TS:");
            foreach (var field in request.Fields.OrderBy(f => f.Key))
            {
                string val = field.Value;
                if (field.Key == 2) val = SecureDataHandler.MaskPAN(val);
                if (field.Key == 35) val = "MASKED"; // Simplified masking for TRN logs
                
                Console.WriteLine($"  DE{field.Key}: {val}");
            }
            
            try
            {
                return await SendRequestAsync(request, sessionId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{sessionId}] [TS-FWD] Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Sends a request and waits for correlation response
        /// </summary>
        private async Task<IsoMessage?> SendRequestAsync(IsoMessage request, string sessionId)
        {
             string stan = request.Fields.ContainsKey(11) ? request.Fields[11] : "000000";
             var tcs = new TaskCompletionSource<IsoMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
             
             _pendingResponses[stan] = tcs;

             // Send
             await SendMessageInternalAsync(request, sessionId, isResponse: false);

             // Wait for response with timeout
             var timeoutTask = Task.Delay(_tsConfig.Timeout);
             var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

             if (completedTask == tcs.Task)
             {
                 return await tcs.Task;
             }
             else
             {
                 _pendingResponses.TryRemove(stan, out _);
                 Console.WriteLine($"[{sessionId}] [TS-SEND] Timeout waiting for response (STAN={stan})");
                 return null;
             }
        }

        private async Task SendMessageInternalAsync(IsoMessage message, string sessionId, bool isResponse)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected");

            // NAPAS protocol: [4-byte ASCII length][ISO message] (Header string removed based on H2H spec)
            byte[] isoBytes = _parser.Build(message);
            string lengthStr = isoBytes.Length.ToString("D4");
            byte[] lengthHeader = System.Text.Encoding.ASCII.GetBytes(lengthStr);

            byte[] fullMessage = new byte[4 + isoBytes.Length];
            Array.Copy(lengthHeader, 0, fullMessage, 0, 4);
            Array.Copy(isoBytes, 0, fullMessage, 4, isoBytes.Length);

            if (!isResponse)
            {
                Console.WriteLine($"[{sessionId}] [TS-SEND] Sending {message.MessageType} (STAN={message.Fields.GetValueOrDefault(11)})");
                // Note: full HEX dump removed for security; individual fields are logged in ForwardTransactionAsync
            }

            lock (_writeLock)
            {
                _stream.Write(fullMessage, 0, fullMessage.Length);
                _stream.Flush();
            }
            
            await Task.CompletedTask; // Keep signature async-compatible
        }

        private async Task ReconnectAsync()
        {
            Disconnect();
            await Task.Delay(1000);
            await ConnectAsync();
        }

        public void Disconnect()
        {
            lock (_writeLock)
            {
                _isConnected = false;
                _connectionCts?.Cancel();
                
                try { _stream?.Close(); } catch { }
                try { _client?.Close(); } catch { }
                
                _client = null;
                _stream = null;
            }
             Console.WriteLine($"[TS-CONN] Disconnected from {_tsConfig.IssuerName}");
        }

        public async Task SignOffAndDisconnectAsync()
        {
            // Optional: Implement proper sign off
            Disconnect(); 
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _heartbeatTimer?.Dispose();
            Disconnect();
        }
    }
}

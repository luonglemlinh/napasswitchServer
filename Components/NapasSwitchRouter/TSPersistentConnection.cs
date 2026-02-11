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
using core.Helpers;

namespace router
{
    public class TSPersistentConnection : IDisposable
    {
        private readonly IssuerBankConfig _tsConfig;
        private readonly IsoParser _parser;
        private readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1);
        
        private TcpClient? _client;
        private NetworkStream? _stream;
        private Timer? _heartbeatTimer;
        private CancellationTokenSource? _connectionCts;
        
        private bool _isConnected;
        private bool _disposed;
        private int _heartbeatIntervalMs;
        private int _stan = 0;

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
                MessageLogger.LogConnectionEvent("TS-CONN", $"Connecting to {_tsConfig.IssuerName} at {_tsConfig.Host}:{_tsConfig.Port}...");

                _client = new TcpClient();
                
                // Add connection timeout (5 seconds)
                using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _client.ConnectAsync(_tsConfig.Host, _tsConfig.Port, connectCts.Token);
                
                // Configure TCP Keep-Alive (Windows specific)
                ConfigureTcpKeepAlive(_client.Client);
                
                _stream = _client.GetStream();
                
                // Note: Don't set ReadTimeout for infinite listener loop
                _stream.WriteTimeout = _tsConfig.Timeout;

                // Dispose old CTS if exists
                _connectionCts?.Dispose();
                _connectionCts = new CancellationTokenSource();
                _isConnected = true;

                MessageLogger.LogConnectionEvent("TS-CONN", $"TCP connection established to {_tsConfig.Host}:{_tsConfig.Port}");

                // Start background receive loop
                _ = ReceiveLoopAsync(_connectionCts.Token);

                // Send sign-on message (0800)
                bool signOnSuccess = await SendSignOnAsync();
                
                if (signOnSuccess)
                {
                    MessageLogger.LogConnectionEvent("TS-CONN", $"Successfully connected and signed on to {_tsConfig.IssuerName}");
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
        /// Attach an existing TCP client (Passive Mode)
        /// </summary>
        public async Task<bool> AttachClientAsync(TcpClient client)
        {
            if (IsConnected) Disconnect();

            try
            {
                _client = client;
                _stream = _client.GetStream();
                _stream.WriteTimeout = _tsConfig.Timeout;
                
                // Configure Keep-Alive
                ConfigureTcpKeepAlive(_client.Client);

                // Dispose old CTS if exists
                _connectionCts?.Dispose();
                _connectionCts = new CancellationTokenSource();
                _isConnected = true;
                
                string remoteEp = _client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
                MessageLogger.LogConnectionEvent("TS-PASSIVE", $"Accepted connection from {remoteEp} for {_tsConfig.IssuerName}");

                // Start background receive loop
                _ = ReceiveLoopAsync(_connectionCts.Token);

                // Send sign-on message (0800) immediately
                // In Passive mode, WE are the Server, but WE still send 0800 to Sign-on to the Issuer?
                // Request says: "Start up -> Listen -> ISS connects -> Send 0800"
                // So yes, we initiate the 0800.
                bool signOnSuccess = await SendSignOnAsync();
                
                if (signOnSuccess)
                {
                    MessageLogger.LogConnectionEvent("TS-PASSIVE", $"Successfully signed on to {_tsConfig.IssuerName}");
                    StartHeartbeat(); // Optional: Start heartbeat if we want to keep it alive from our side
                    return true;
                }
                else
                {
                    Console.WriteLine($"[TS-PASSIVE] Sign-on failed, closing connection");
                    Disconnect();
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TS-PASSIVE] Failed to attach client: {ex.Message}");
                Disconnect();
                return false;
            }
        }

        /// <summary>
        /// Continuous background loop to read incoming messages
        /// </summary>
        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            MessageLogger.LogConnectionEvent("TS-RECV", "Started background receive loop");
            var buffer = new byte[8192];

            try
            {
                while (!token.IsCancellationRequested && _stream != null)
                {
                    // 1. Read 4-byte Length Header
                    byte[] lenBytes = new byte[4];
                    int bytesRead = await NetworkStreamHelper.ReadExactAsync(_stream, lenBytes, 0, 4, token);
                    if (bytesRead == 0) 
                    {
                        Console.WriteLine("[TS-RECV] Remote side closed connection (0 bytes read)");
                        break; 
                    }

                    string lenStr = System.Text.Encoding.ASCII.GetString(lenBytes);
                    if (!int.TryParse(lenStr, out int msgLen) || msgLen <= 0 || msgLen > 9999)
                    {
                        Console.WriteLine($"[TS-RECV] Invalid or out-of-range length header: {lenStr}");
                        break;
                    }

                    // 2. Read Payload
                    byte[] payload = new byte[msgLen];
                    bytesRead = await NetworkStreamHelper.ReadExactAsync(_stream, payload, 0, msgLen, token);
                    if (bytesRead != msgLen) 
                    {
                        Console.WriteLine($"[TS-RECV] Connection closed mid-message (expected {msgLen}, got {bytesRead})");
                        break;
                    }

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
                // Console.WriteLine("[TS-RECV] Receive loop stopped"); // Reduced noise
                if (!token.IsCancellationRequested) Disconnect();
            }
        }

        private void ProcessIncomingPayload(byte[] rawData)
        {
            try
            {
                var msg = _parser.Parse(rawData);
                bool isResponse = MtiHelper.IsResponse(msg.MessageType);
                string rc = isResponse ? (msg.GetResponseCode() ?? "96") : "N/A";
                // Silence 0810 success logs; always log requests and non-00 responses
                if (!MtiHelper.IsNetworkManagement(msg.MessageType) || (isResponse && rc != "00"))
                {
                    Console.WriteLine($"[TS-RECV] {msg.MessageType} | TRN: {msg.GetTRN() ?? "N/A"} | RC: {rc}");
                }
                  
                  // Full message dump for debugging (TS to Switch)
                  // Log to file instead of console log spam
                  if (!MtiHelper.IsNetworkManagement(msg.MessageType))
                  {
                      MessageLogger.LogMessage("TS-PERSISTENT", "ISS received", msg);
                  }

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

            if (!MtiHelper.IsNetworkManagement(mti))
            {
                Console.WriteLine($"[TS-RECV] Received MTI={mti} STAN={stan}");
            }

            if (mti == MtiHelper.NetworkManagementRequest)
            {
                // Echo request from remote TS - send 0810 response
                _ = SendEchoResponseAsync(msg);
            }
            else if (MtiHelper.IsResponse(mti))
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

        private async Task SendEchoResponseAsync(IsoMessage request)
        {
            try
            {
                var response = new IsoMessage { MessageType = MtiHelper.NetworkManagementResponse };
                
                // Copy essential fields
                if (request.Fields.ContainsKey(7)) response.SetField(7, request.Fields[7]);
                if (request.Fields.ContainsKey(11)) response.SetField(11, request.Fields[11]); // Echo STAN
                if (request.Fields.ContainsKey(70)) response.SetField(70, request.Fields[70]); // Network Info Code

                // Set Response Code 00 (Success)
                response.SetField(39, "00");

                // Console.WriteLine($"[TS-AUTO] Sending Echo Response (0810) for STAN={response.Fields[11]}");
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
            MessageLogger.LogConnectionEvent("TS-CONN", "Sending sign-on (0800)...");

            var response = await SendRequestAsync(signOnMsg, "SIGN-ON");
            
            if (response != null)
            {
                string rc = response.GetResponseCode() ?? "96";
                MessageLogger.LogConnectionEvent("TS-CONN", $"Sign-on response: RC={rc}");
                return rc == "00";
            }
            return false;
        }

        private void StartHeartbeat()
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = new Timer(async _ => await SendHeartbeatAsync(), null, _heartbeatIntervalMs, _heartbeatIntervalMs);
            MessageLogger.LogConnectionEvent("TS-CONN", $"Heartbeat started (every {_heartbeatIntervalMs / 1000}s)");
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
            var message = new IsoMessage { MessageType = MtiHelper.NetworkManagementRequest };
            
            message.SetField(7, now.ToString("MMddHHmmss"));
            
            // Get next STAN with wrapping at 999999
            int nextStan = Interlocked.Increment(ref _stan);
            if (nextStan > 999999)
            {
                // Reset to 1 if we exceed 6 digits
                Interlocked.CompareExchange(ref _stan, 1, nextStan);
                nextStan = 1;
            }
            message.SetField(11, nextStan.ToString("D6"));
            
            // DE32: Acquiring Institution Identification Code (NAPAS Requirement)
            // Specification: n..11, LLVAR encoding
            // - This field is REQUIRED in all messages for transaction routing
            // - Contains Acquirer's ID number (typically 6-digit BIN code)
            // - Encoded as: [2-byte length][variable data]
            // - Length field: Zero-padded ASCII (e.g., "06" for 6 digits)
            // Example: Acquirer ID "970418" → Wire format "06970418"
            //          where "06" indicates 6 digits follow, then "970418" is the actual ID
            // DE32: Acquiring Institution Identification Code
            // Use IssuerCode if numeric, otherwise try first BIN, else default.
            string acquirerId = _tsConfig.IssuerCode;

            if (string.IsNullOrEmpty(acquirerId) || !acquirerId.All(char.IsDigit))
            {
                // Try to use the first configured BIN for this bank
                var firstBin = _tsConfig.AllBins.FirstOrDefault();
                if (!string.IsNullOrEmpty(firstBin))
                {
                    acquirerId = firstBin;
                }
                else
                {
                    acquirerId = "970418"; // Default Default
                }
            }
            
            // Final validation length (msg must be 6-11 digits)
            if (acquirerId.Length < 6 || acquirerId.Length > 11) acquirerId = "970418";

            message.SetField(32, acquirerId);

            message.SetField(70, networkCode);
            return message;
        }

        public async Task<IsoMessage?> ForwardTransactionAsync(IsoMessage request, string sessionId)
        {
            if (!IsConnected) await ConnectAsync();
            
            // Log message before forwarding to TS
            MessageLogger.LogMessage(sessionId, "ISS forward", request);
            Console.WriteLine($"[{sessionId}] [TS-FWD] {request.MessageType} | TRN: {request.GetTRN() ?? "N/A"}");
            
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
                if (!MtiHelper.IsNetworkManagement(message.MessageType))
                {
                    Console.WriteLine($"[{sessionId}] [TS-SEND] Sending {message.MessageType} (STAN={message.Fields.GetValueOrDefault(11)})");
                }
                // Note: full HEX dump removed for security; individual fields are logged in ForwardTransactionAsync
            }

            // Use SemaphoreSlim for async-safe write operation
            await _writeSemaphore.WaitAsync();
            try
            {
                _stream.Write(fullMessage, 0, fullMessage.Length);
                _stream.Flush();
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }

        private int _reconnectAttempts;
        private const int MaxReconnectDelayMs = 60000;

        private async Task ReconnectAsync()
        {
            Disconnect();
            int delay = Math.Min(1000 * (1 << _reconnectAttempts), MaxReconnectDelayMs);
            Console.WriteLine($"[TS-CONN] Reconnecting to {_tsConfig.IssuerName} in {delay}ms (attempt {_reconnectAttempts + 1})...");
            await Task.Delay(delay);
            bool success = await ConnectAsync();
            if (success)
                _reconnectAttempts = 0;
            else
                _reconnectAttempts++;
        }

        public void Disconnect()
        {
            _isConnected = false;
            _connectionCts?.Cancel();
            
            // Cancel all pending responses
            foreach (var kvp in _pendingResponses)
            {
                kvp.Value.TrySetCanceled();
            }
            _pendingResponses.Clear();
            
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            
            _client = null;
            _stream = null;
            
            Console.WriteLine($"[TS-CONN] Disconnected from {_tsConfig.IssuerName}");
        }

        public async Task SignOffAndDisconnectAsync()
        {
            Disconnect(); 
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _heartbeatTimer?.Dispose();
            Disconnect();
            _connectionCts?.Dispose();
            _writeSemaphore?.Dispose();
        }
        private void ConfigureTcpKeepAlive(Socket socket)
        {
            try
            {
                // TCP Keep-Alive settings for Windows
                // Structure: [on/off (4 bytes)][keepalivetime (4 bytes)][keepaliveinterval (4 bytes)]
                // Time/Interval are in milliseconds
                
                byte[] inOptionValues = new byte[12];
                
                // On/Off: 1 (Enabled)
                BitConverter.GetBytes((uint)1).CopyTo(inOptionValues, 0);
                
                // KeepAliveTime: 60,000 ms (60 seconds) - Time before first keep-alive packet
                BitConverter.GetBytes((uint)60000).CopyTo(inOptionValues, 4);
                
                // KeepAliveInterval: 1,000 ms (1 second) - Interval between retries
                BitConverter.GetBytes((uint)1000).CopyTo(inOptionValues, 8);

                socket.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
                
                MessageLogger.LogConnectionEvent("TS-CONN", "TCP Keep-Alive configured: Idle=60s, Interval=1s");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TS-WARN] Failed to configure TCP Keep-Alive: {ex.Message}");
            }
        }
    }
}

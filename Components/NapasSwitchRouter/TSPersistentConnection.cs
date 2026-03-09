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

        // Circuit breaker state — fail fast when an issuer is down
        private enum CircuitState { Closed, Open, HalfOpen }
        private CircuitState _circuitState = CircuitState.Closed;
        private int _consecutiveFailures;
        private DateTime _circuitOpenedAt;
        private const int CircuitBreakerThreshold = 5;
        private static readonly TimeSpan CircuitBreakerCooldown = TimeSpan.FromSeconds(30);

        // Track pending requests by composite key (STAN+RRN+Date) to correlate responses safely
        // STAN alone is only 6 digits (000001-999999) and can collide under load
        private readonly ConcurrentDictionary<string, TaskCompletionSource<IsoMessage>> _pendingResponses = new();

        public bool IsConnected => _isConnected && _client?.Connected == true;
        public string TSName => _tsConfig.IssuerName;

        /// <summary>
        /// Fired when connection state changes. Args: (issuerName, isConnected)
        /// </summary>
        public event Action<string, bool>? OnConnectionChanged;

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
                    SwitchLogger.Info($"[TS-CONN] ISS connected: {_tsConfig.IssuerName} at {_tsConfig.Host}:{_tsConfig.Port}");
                    MessageLogger.LogConnectionEvent("TS-CONN", $"Successfully connected and signed on to {_tsConfig.IssuerName}");
                    return true;
                }
                else
                {
                    SwitchLogger.Info($"[TS-CONN] Sign-on failed, closing connection");
                    Disconnect();
                    return false;
                }
            }
            catch (Exception ex)
            {
                SwitchLogger.Info($"[TS-CONN] Connection failed: {ex.Message}");
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
                    string ep = _client?.Client?.RemoteEndPoint?.ToString() ?? "Unknown";
                    SwitchLogger.Info($"[TS-PASSIVE] ISS connected: {_tsConfig.IssuerName} from {ep} — sign-on OK");
                    MessageLogger.LogConnectionEvent("TS-PASSIVE", $"Successfully signed on to {_tsConfig.IssuerName}");
                    StartHeartbeat(); // Optional: Start heartbeat if we want to keep it alive from our side
                    return true;
                }
                else
                {
                    SwitchLogger.Info($"[TS-PASSIVE] Sign-on failed, closing connection");
                    Disconnect();
                    return false;
                }
            }
            catch (Exception ex)
            {
                SwitchLogger.Info($"[TS-PASSIVE] Failed to attach client: {ex.Message}");
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
                        SwitchLogger.Info($"[TS-RECV] Remote side closed connection (0 bytes read)");
                        break; 
                    }

                    string lenStr = System.Text.Encoding.ASCII.GetString(lenBytes);
                    if (!int.TryParse(lenStr, out int msgLen) || msgLen <= 0 || msgLen > 9999)
                    {
                        SwitchLogger.Info($"[TS-RECV] Invalid or out-of-range length header: {lenStr}");
                        break;
                    }

                    // 2. Read Payload
                    byte[] payload = new byte[msgLen];
                    bytesRead = await NetworkStreamHelper.ReadExactAsync(_stream, payload, 0, msgLen, token);
                    if (bytesRead != msgLen) 
                    {
                        SwitchLogger.Info($"[TS-RECV] Connection closed mid-message (expected {msgLen}, got {bytesRead})");
                        break;
                    }

                    // 3. Process Message
                    ProcessIncomingPayload(payload);
                }
            }
            catch (OperationCanceledException) { /* Graceful shutdown */ }
            catch (Exception ex)
            {
                SwitchLogger.Info($"[TS-RECV] Error in receive loop: {ex.Message}");
            }
            finally
            {
                // SwitchLogger.Info($"[TS-RECV] Receive loop stopped"); // Reduced noise
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
                    SwitchLogger.Info("[TS-RECV] {MTI} | TRN: {TRN} | RC: {RC}", msg.MessageType, msg.GetTRN() ?? "N/A", rc);
                }
                  
                  // Full message dump for debugging (TS to Switch)
                  // Log to file instead of console log spam
                  if (!MtiHelper.IsNetworkManagement(msg.MessageType))
                  {
                      MessageLogger.LogMessage("TS-PERSISTENT", "ISS received", msg);
                  }

                  if (rc == "30")
                 {
                     SwitchLogger.Info($"[TS-ALERT] Format Error (RC 30) received from ISS.");
                 }

                 HandleParsedMessage(msg);
            }
            catch (Exception ex)
            {
                SwitchLogger.Info($"[TS-RECV] Failed to parse incoming message: {ex.Message}");
            }
        }

        private void HandleParsedMessage(IsoMessage msg)
        {
            string mti = msg.MessageType;
            string correlationKey = BuildCorrelationKey(msg);

            if (!MtiHelper.IsNetworkManagement(mti))
            {
                SwitchLogger.Info($"[TS-RECV] Received MTI={mti} Key={correlationKey}");
            }

            if (mti == MtiHelper.NetworkManagementRequest)
            {
                // Echo request from remote TS - send 0810 response
                _ = SendEchoResponseAsync(msg);
            }
            else if (MtiHelper.IsResponse(mti))
            {
                // Response to our request -> Find TCS by composite key and complete it
                if (_pendingResponses.TryRemove(correlationKey, out var tcs))
                {
                    tcs.TrySetResult(msg);
                }
                else
                {
                    SwitchLogger.Info($"[TS-RECV] Warning: Unmatched response received (Key={correlationKey})");
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

                // SwitchLogger.Info($"[TS-AUTO] Sending Echo Response (0810) for STAN={response.Fields[11]}");
                await SendMessageInternalAsync(response, "AUTO-ECHO", isResponse: true);
            }
            catch (Exception ex)
            {
                SwitchLogger.Info($"[TS-AUTO] Failed to send echo response: {ex.Message}");
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
                SwitchLogger.Info($"[TS-HEARTBEAT] Connection lost, attempting reconnect...");
                await ConnectAsync();
                return;
            }

            var heartbeatMsg = BuildNetworkMessage("301"); // 301 = Echo test
            try
            {
                var response = await SendRequestAsync(heartbeatMsg, "HEARTBEAT");
                if (response == null || response.GetResponseCode() != "00")
                {
                     SwitchLogger.Info("[TS-HEARTBEAT] Failed (RC={RC}). Reconnecting...", response?.GetResponseCode() ?? "Timeout");
                     await ReconnectAsync();
                }
            }
            catch (Exception ex)
            {
                SwitchLogger.Info($"[TS-HEARTBEAT] Error: {ex.Message}");
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
            // Circuit breaker: fail fast when issuer is known to be down
            if (_circuitState == CircuitState.Open)
            {
                if (DateTime.UtcNow - _circuitOpenedAt < CircuitBreakerCooldown)
                {
                    SwitchLogger.Debug($"[{sessionId}] [CIRCUIT] OPEN for {_tsConfig.IssuerName} — failing fast");
                    return null;
                }
                // Cooldown elapsed, try half-open
                _circuitState = CircuitState.HalfOpen;
                SwitchLogger.Info($"[{sessionId}] [CIRCUIT] HALF-OPEN for {_tsConfig.IssuerName} — attempting probe");
            }

            if (!IsConnected) await ConnectAsync();

            // Log message before forwarding to TS
            MessageLogger.LogMessage(sessionId, "ISS forward", request);
            SwitchLogger.Info("[{SessionId}] [TS-FWD] {MTI} | TRN: {TRN}", sessionId, request.MessageType, request.GetTRN() ?? "N/A");

            try
            {
                var response = await SendRequestAsync(request, sessionId);
                if (response != null)
                {
                    // Success: reset circuit breaker
                    _consecutiveFailures = 0;
                    if (_circuitState != CircuitState.Closed)
                    {
                        _circuitState = CircuitState.Closed;
                        SwitchLogger.Info($"[{sessionId}] [CIRCUIT] CLOSED for {_tsConfig.IssuerName} — connection recovered");
                    }
                }
                else
                {
                    RecordCircuitFailure(sessionId);
                }
                return response;
            }
            catch (Exception ex)
            {
                RecordCircuitFailure(sessionId);
                SwitchLogger.Info($"[{sessionId}] [TS-FWD] Error: {ex.Message}");
                return null;
            }
        }

        private void RecordCircuitFailure(string sessionId)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= CircuitBreakerThreshold && _circuitState != CircuitState.Open)
            {
                _circuitState = CircuitState.Open;
                _circuitOpenedAt = DateTime.UtcNow;
                SwitchLogger.Warn($"[{sessionId}] [CIRCUIT] OPENED for {_tsConfig.IssuerName} after {_consecutiveFailures} consecutive failures — failing fast for {CircuitBreakerCooldown.TotalSeconds}s");
            }
        }

        /// <summary>
        /// Build a composite correlation key from STAN (DE11), RRN (DE37), date (DE7), and issuer name.
        /// Includes issuer name to prevent collisions across different connections.
        /// Falls back to STAN-only if other fields are absent, for network management messages.
        /// </summary>
        private string BuildCorrelationKey(IsoMessage msg)
        {
            string stan = msg.Fields.ContainsKey(11) ? msg.Fields[11] : "000000";
            string rrn = msg.Fields.ContainsKey(37) ? msg.Fields[37] : "";
            string date = msg.Fields.ContainsKey(7) ? msg.Fields[7] : "";
            return $"{_tsConfig.IssuerName}|{stan}|{rrn}|{date}";
        }

        /// <summary>
        /// Sends a request and waits for correlation response
        /// </summary>
        private async Task<IsoMessage?> SendRequestAsync(IsoMessage request, string sessionId)
        {
             string correlationKey = BuildCorrelationKey(request);
             var tcs = new TaskCompletionSource<IsoMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

             _pendingResponses[correlationKey] = tcs;

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
                 _pendingResponses.TryRemove(correlationKey, out _);
                 SwitchLogger.Info($"[{sessionId}] [TS-SEND] Timeout waiting for response (Key={correlationKey})");
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
                    SwitchLogger.Info($"[{sessionId}] [TS-SEND] Sending {message.MessageType} (STAN={message.Fields.GetValueOrDefault(11)})");
                }
                // Note: full HEX dump removed for security; individual fields are logged in ForwardTransactionAsync
            }

            // Use SemaphoreSlim for async-safe write operation
            await _writeSemaphore.WaitAsync();
            try
            {
                await _stream.WriteAsync(fullMessage, 0, fullMessage.Length);
                await _stream.FlushAsync();
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
            SwitchLogger.Info($"[TS-CONN] Reconnecting to {_tsConfig.IssuerName} in {delay}ms (attempt {_reconnectAttempts + 1})...");
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

            SwitchLogger.Warn($"[TS-CONN] ISS disconnected: {_tsConfig.IssuerName} ({_tsConfig.IssuerCode})");
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
                // TCP Keep-Alive settings
                // On Windows: Use IOControl for backward compatibility
                // On Linux: IOControl Code KeepAliveValues is not supported
                
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    byte[] inOptionValues = new byte[12];
                    BitConverter.GetBytes((uint)1).CopyTo(inOptionValues, 0); // On
                    BitConverter.GetBytes((uint)60000).CopyTo(inOptionValues, 4); // Time (60s)
                    BitConverter.GetBytes((uint)1000).CopyTo(inOptionValues, 8); // Interval (1s)
                    socket.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
                    MessageLogger.LogConnectionEvent("TS-CONN", "TCP Keep-Alive configured (Windows): Idle=60s, Interval=1s");
                }
                else
                {
                    // For Linux/Core: Use standard cross-platform socket options
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    // Note: Specific Time/Interval/Retry settings on Linux often require TcpKeepAliveTime/Interval/Retry options
                    // which are available in .NET Core 3.0+ but might vary by platform.
                    // For now, enabling standard KeepAlive is enough to satisfy the requirements.
                    MessageLogger.LogConnectionEvent("TS-CONN", "TCP Keep-Alive enabled (Linux)");
                }
            }
            catch (Exception ex)
            {
                SwitchLogger.ForContext("NETWORK").Warn("Failed to configure TCP Keep-Alive: {Error}", ex.Message);
            }
        }
    }
}

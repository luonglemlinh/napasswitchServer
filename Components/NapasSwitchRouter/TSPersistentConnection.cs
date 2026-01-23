using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using core.Configuration;
using core.Models;
using core.Models.Configuration;
using core.ISO8583;

namespace router
{
    /// <summary>
    /// Manages a persistent connection to the Transaction Switch (TS)
    /// - Maintains a single long-lived connection
    /// - Sends periodic heartbeat (0800) messages
    /// - Forwards all transactions through this connection
    /// - Auto-reconnects on failure
    /// </summary>
    public class TSPersistentConnection : IDisposable
    {
        private readonly IssuerBankConfig _tsConfig;
        private readonly IsoParser _parser;
        private readonly object _connectionLock = new object();
        
        private TcpClient? _client;
        private NetworkStream? _stream;
        private Timer? _heartbeatTimer;
        private bool _isConnected;
        private bool _disposed;
        private int _heartbeatIntervalMs;
        private int _stan = 1;

        public bool IsConnected => _isConnected && _client?.Connected == true;
        public string TSName => _tsConfig.IssuerName;

        public TSPersistentConnection(IssuerBankConfig tsConfig, int heartbeatIntervalMs = 30000)
        {
            _tsConfig = tsConfig ?? throw new ArgumentNullException(nameof(tsConfig));
            _parser = new IsoParser();
            _heartbeatIntervalMs = heartbeatIntervalMs;
        }

        /// <summary>
        /// Connect to TS and send sign-on message
        /// </summary>
        public async Task<bool> ConnectAsync()
        {
            lock (_connectionLock)
            {
                if (_isConnected && _client?.Connected == true)
                    return true;
            }

            try
            {
                Console.WriteLine($"[TS-CONN] Connecting to {_tsConfig.IssuerName} at {_tsConfig.Host}:{_tsConfig.Port}...");

                _client = new TcpClient();
                await _client.ConnectAsync(_tsConfig.Host, _tsConfig.Port);
                _stream = _client.GetStream();
                _stream.ReadTimeout = _tsConfig.Timeout;
                _stream.WriteTimeout = _tsConfig.Timeout;

                Console.WriteLine($"[TS-CONN] TCP connection established to {_tsConfig.Host}:{_tsConfig.Port}");

                // Send sign-on message (0800)
                bool signOnSuccess = await SendSignOnAsync();
                
                if (signOnSuccess)
                {
                    _isConnected = true;
                    
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
        /// Send sign-on (0800) message to TS
        /// </summary>
        private async Task<bool> SendSignOnAsync()
        {
            try
            {
                var signOnMsg = BuildNetworkMessage("001"); // 001 = Sign-on
                
                Console.WriteLine($"[TS-CONN] Sending sign-on (0800) message...");
                
                var response = await SendMessageInternalAsync(signOnMsg, "SIGN-ON");
                
                if (response != null)
                {
                    string rc = response.GetResponseCode() ?? "96";
                    Console.WriteLine($"[TS-CONN] Sign-on response: RC={rc}");
                    return rc == "00";
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TS-CONN] Sign-on error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Start periodic heartbeat timer
        /// </summary>
        private void StartHeartbeat()
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = new Timer(
                async _ => await SendHeartbeatAsync(),
                null,
                _heartbeatIntervalMs,
                _heartbeatIntervalMs
            );
            Console.WriteLine($"[TS-CONN] Heartbeat started (every {_heartbeatIntervalMs / 1000}s)");
        }

        /// <summary>
        /// Send heartbeat (0800) message to keep connection alive
        /// </summary>
        private async Task SendHeartbeatAsync()
        {
            if (!IsConnected)
            {
                Console.WriteLine($"[TS-HEARTBEAT] Connection lost, attempting reconnect...");
                await ConnectAsync();
                return;
            }

            try
            {
                var heartbeatMsg = BuildNetworkMessage("301"); // 301 = Echo test
                
                Console.WriteLine($"[TS-HEARTBEAT] Sending heartbeat (0800)...");
                
                var response = await SendMessageInternalAsync(heartbeatMsg, "HEARTBEAT");
                
                if (response != null)
                {
                    string rc = response.GetResponseCode() ?? "96";
                    Console.WriteLine($"[TS-HEARTBEAT] Response: RC={rc}");
                    
                    if (rc != "00")
                    {
                        Console.WriteLine($"[TS-HEARTBEAT] Abnormal response, reconnecting...");
                        await ReconnectAsync();
                    }
                }
                else
                {
                    Console.WriteLine($"[TS-HEARTBEAT] No response, reconnecting...");
                    await ReconnectAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TS-HEARTBEAT] Error: {ex.Message}, reconnecting...");
                await ReconnectAsync();
            }
        }

        /// <summary>
        /// Build network management message (0800)
        /// Format: NAPASBASE.ISO + MTI + Bitmap + Fields (F7, F11, F32, F70)
        /// </summary>
        private IsoMessage BuildNetworkMessage(string networkCode)
        {
            var now = DateTime.Now;
            var message = new IsoMessage
            {
                MessageType = "0800"
            };



            // DE7: Transmission DateTime - MMddHHmmss
            message.SetField(7, now.ToString("MMddHHmmss"));

            // DE11: STAN - 6 digits
            message.SetField(11, Interlocked.Increment(ref _stan).ToString("D6"));

            // DE32: Acquirer ID - LLVAR format (length prefix + value)
            string acquirerId = _tsConfig.IssuerCode ?? "970488";
            message.SetField(32, acquirerId);

            // DE70: Network Management Information Code
            message.SetField(70, networkCode);

            return message;
        }

        /// <summary>
        /// Forward a transaction message to TS and wait for response
        /// </summary>
        public async Task<IsoMessage?> ForwardTransactionAsync(IsoMessage request, string sessionId)
        {
            if (!IsConnected)
            {
                Console.WriteLine($"[{sessionId}] [TS-FWD] Not connected, attempting connect...");
                bool connected = await ConnectAsync();
                if (!connected)
                {
                    Console.WriteLine($"[{sessionId}] [TS-FWD] Failed to connect to TS");
                    return null;
                }
            }

            try
            {
                return await SendMessageInternalAsync(request, sessionId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{sessionId}] [TS-FWD] Error: {ex.Message}");
                
                // Try reconnect and retry once
                Console.WriteLine($"[{sessionId}] [TS-FWD] Attempting reconnect and retry...");
                await ReconnectAsync();
                
                if (IsConnected)
                {
                    try
                    {
                        return await SendMessageInternalAsync(request, sessionId);
                    }
                    catch (Exception retryEx)
                    {
                        Console.WriteLine($"[{sessionId}] [TS-FWD] Retry failed: {retryEx.Message}");
                    }
                }
                
                return null;
            }
        }

        /// <summary>
        /// Internal method to send message and receive response
        /// Format: [2-byte length][NAPASBASE.ISO][ISO message]
        /// </summary>
        private async Task<IsoMessage?> SendMessageInternalAsync(IsoMessage request, string sessionId)
        {
            if (_stream == null)
                throw new InvalidOperationException("Not connected to TS");

            // NAPAS protocol: [4-byte ASCII length]["NAPASBASE.ISO"][ISO message]
            const string NAPAS_HEADER = "NAPASBASE.ISO";
            byte[] napasHeaderBytes = System.Text.Encoding.ASCII.GetBytes(NAPAS_HEADER);

            // Build ISO message
            byte[] isoBytes = _parser.Build(request);

            // Total payload = NAPAS header + ISO message
            int totalLength = napasHeaderBytes.Length + isoBytes.Length;

            // CRITICAL: 4-byte ASCII length header (NOT 2-byte binary!)
            string lengthStr = totalLength.ToString("D4"); // "0063", "0065", etc.
            byte[] lengthHeader = System.Text.Encoding.ASCII.GetBytes(lengthStr);

            // Full message: [4-byte ASCII length][NAPAS header][ISO message]
            byte[] fullMessage = new byte[4 + totalLength];
            Array.Copy(lengthHeader, 0, fullMessage, 0, 4);
            Array.Copy(napasHeaderBytes, 0, fullMessage, 4, napasHeaderBytes.Length);
            Array.Copy(isoBytes, 0, fullMessage, 4 + napasHeaderBytes.Length, isoBytes.Length);

            Console.WriteLine($"[{sessionId}] [TS-SEND] Total: {fullMessage.Length} bytes");
            Console.WriteLine($"[{sessionId}] [TS-SEND] Length Header (ASCII): {lengthStr}");
            Console.WriteLine($"[{sessionId}] [TS-SEND] NAPAS Header: {NAPAS_HEADER}");
            Console.WriteLine($"[{sessionId}] [TS-SEND] ISO Payload: {isoBytes.Length} bytes");

            lock (_connectionLock)
            {
                _stream.Write(fullMessage, 0, fullMessage.Length);
                _stream.Flush();
            }

            // Read response: [4-byte ASCII length][NAPAS header][ISO message]
            byte[] respLengthBytes = new byte[4];
            int bytesRead = await _stream.ReadAsync(respLengthBytes, 0, 4);

            if (bytesRead == 0)
            {
                Console.WriteLine($"[{sessionId}] [TS-RECV] Connection closed by TS");
                _isConnected = false;
                return null;
            }

            string respLengthStr = System.Text.Encoding.ASCII.GetString(respLengthBytes);
            if (!int.TryParse(respLengthStr, out int responseLength))
            {
                Console.WriteLine($"[{sessionId}] [TS-RECV] Invalid length header: {respLengthStr}");
                _isConnected = false;
                return null;
            }

            Console.WriteLine($"[{sessionId}] [TS-RECV] Expecting {responseLength} bytes");

            byte[] responseBytes = new byte[responseLength];
            int totalRead = 0;
            while (totalRead < responseLength)
            {
                bytesRead = await _stream.ReadAsync(responseBytes, totalRead, responseLength - totalRead);
                if (bytesRead == 0)
                {
                    Console.WriteLine($"[{sessionId}] [TS-RECV] Connection closed while reading");
                    _isConnected = false;
                    return null;
                }
                totalRead += bytesRead;
            }

            Console.WriteLine($"[{sessionId}] [TS-RECV] Received {totalRead} bytes");

            // Skip NAPAS header (13 bytes) if present
            const int NAPAS_HEADER_LENGTH = 13;
            byte[] isoResponseBytes;

            if (responseLength > NAPAS_HEADER_LENGTH)
            {
                string possibleHeader = System.Text.Encoding.ASCII.GetString(responseBytes, 0, NAPAS_HEADER_LENGTH);
                if (possibleHeader == NAPAS_HEADER)
                {
                    Console.WriteLine($"[{sessionId}] [TS-RECV] Skipping NAPAS header");
                    isoResponseBytes = new byte[responseLength - NAPAS_HEADER_LENGTH];
                    Array.Copy(responseBytes, NAPAS_HEADER_LENGTH, isoResponseBytes, 0, isoResponseBytes.Length);
                }
                else
                {
                    isoResponseBytes = responseBytes;
                }
            }
            else
            {
                isoResponseBytes = responseBytes;
            }

            return _parser.Parse(isoResponseBytes);
        }



        /// <summary>
        /// Reconnect to TS
        /// </summary>
        private async Task ReconnectAsync()
        {
            Disconnect();
            await Task.Delay(1000); // Wait 1 second before reconnect
            await ConnectAsync();
        }

        /// <summary>
        /// Disconnect from TS
        /// </summary>
        public void Disconnect()
        {
            lock (_connectionLock)
            {
                _isConnected = false;
                
                try { _stream?.Close(); } catch { }
                try { _client?.Close(); } catch { }
                
                _stream = null;
                _client = null;
            }
            
            Console.WriteLine($"[TS-CONN] Disconnected from {_tsConfig.IssuerName}");
        }

        /// <summary>
        /// Send sign-off and disconnect gracefully
        /// </summary>
        public async Task SignOffAndDisconnectAsync()
        {
            if (IsConnected)
            {
                try
                {
                    var signOffMsg = BuildNetworkMessage("002"); // 002 = Sign-off
                    Console.WriteLine($"[TS-CONN] Sending sign-off (0800)...");
                    await SendMessageInternalAsync(signOffMsg, "SIGN-OFF");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TS-CONN] Sign-off error: {ex.Message}");
                }
            }
            
            Disconnect();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _heartbeatTimer?.Dispose();
            Disconnect();
            
            Console.WriteLine($"[TS-CONN] TSPersistentConnection disposed");
        }
    }
}

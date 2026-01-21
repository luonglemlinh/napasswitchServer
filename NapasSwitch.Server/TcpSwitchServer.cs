using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using core.Configuration;
using core.Models;
using core.ISO8583;
using core.Security;
using network.Validation;
using router;
using data;
using System.Data.SqlClient;

namespace server
{
    /// Multi-threaded TCP server that listens for incoming ISO-8583 messages
    
    public class TcpSwitchServer : IDisposable
    {
        private TcpListener? _listener;
        private bool _isRunning;
        private readonly int _port;
        private readonly IsoParser _parser;
        private readonly IssuerConnector _issuerConnector;
        private readonly TransactionLogger? _transactionLogger;
        private readonly TransactionStateMachine _stateMachine;
        private readonly SecureDataHandler _securityProvider;
        private readonly PendingTransactionStore? _pendingStore;
        private readonly ResponseCorrelationValidator _correlationValidator;
        private bool _disposed;
        private Timer? _statsTimer;
        private Timer? _poolHealthTimer;
        private Timer? _cleanupTimer;

        // Thread-safe collection to track active connections
        // ConcurrentDictionary = multiple threads can access it safely!
        private readonly ConcurrentDictionary<string, ClientSession> _activeSessions;
        private readonly NapasDataElementValidator _validator;

        // Update the constructor
        public TcpSwitchServer(int port = 8583, string dbConnectionString = "", bool enableLogging = true)
        {
            _port = port;
            _parser = new IsoParser();
            _issuerConnector = new IssuerConnector();
            _activeSessions = new ConcurrentDictionary<string, ClientSession>();
            _stateMachine = new TransactionStateMachine(transactionTimeoutSeconds: 30);
            _securityProvider = new SecureDataHandler(new SoftwareHsmStub());
            _correlationValidator = new ResponseCorrelationValidator();
            
            _stateMachine.OnTransactionTimeout += OnTransactionTimeout;
            _stateMachine.OnReversalRequired += OnReversalRequired;
            
            string configPath = FindValidationConfigPath();
            _validator = new NapasDataElementValidator(configPath);
            
            // Initialize transaction logger
            if (enableLogging && !string.IsNullOrEmpty(dbConnectionString))
            {
                _transactionLogger = new TransactionLogger(dbConnectionString, enableLogging);
                _pendingStore = new PendingTransactionStore(dbConnectionString, expirationMinutes: 5);
                Console.WriteLine("[INIT] Transaction logger and pending store initialized");
            }
            else
            {
                _transactionLogger = null;
                _pendingStore = null;
                Console.WriteLine("[INIT] Transaction logger disabled");
            }

            StartBackgroundMonitoring();
        }
        
        private string FindValidationConfigPath()
        {
            var searchPaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "NapasValidationConfig.xml"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Config", "NapasValidationConfig.xml"),
            };
            
            foreach (var path in searchPaths)
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    Console.WriteLine($"[INIT] Found validation config at: {fullPath}");
                    return fullPath;
                }
            }
            
            throw new FileNotFoundException(
                "NAPAS validation configuration file not found. Searched paths:\n" + 
                string.Join("\n", searchPaths.Select(p => $"  - {Path.GetFullPath(p)}")));
        }
        
        private void StartBackgroundMonitoring()
        {
            _statsTimer = new Timer(LogServerStats, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _poolHealthTimer = new Timer(LogPoolHealth, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            
            if (_pendingStore != null)
            {
                _cleanupTimer = new Timer(async _ => await _pendingStore.CleanupExpiredAsync(), 
                    null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            }
            
            Console.WriteLine("[MONITORING] Background stats logging enabled (every 30s)");
        }
        
        private void LogServerStats(object? state)
        {
            var stats = GetStats();
            Console.WriteLine($"[STATS] Active: {stats.ActiveConnections} | Total Msgs: {stats.TotalMessageCount}");
        }
        
        private void LogPoolHealth(object? state)
        {
            var poolStats = _issuerConnector.GetPoolStats();
            Console.WriteLine($"[POOL] Total: {poolStats.TotalPooledConnections} | Active: {poolStats.TotalActivedConnections} | Pools: {poolStats.PoolCount}");
        }
        
        private void OnTransactionTimeout(TransactionContext context)
        {
            Console.WriteLine($"[TIMEOUT] Transaction {context.TransactionId} timed out after {context.GetProcessingTime()?.TotalMilliseconds}ms");
        }
        
        private void OnReversalRequired(TransactionContext context)
        {
            Console.WriteLine($"[REVERSAL] Auto-reversal required for transaction {context.TransactionId}");
        }

        
        /// Start the server and begin accepting connections
        
        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _isRunning = true;

            Console.WriteLine("╔═══════════════════════════════════════════════════╗");
            Console.WriteLine("║       NAPAS SWITCH SERVER STARTED                 ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════╝");
            Console.WriteLine($" Listening on port: {_port}");
            Console.WriteLine($" Started at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($" Configuration: {ConfigurationLoader.Instance.GetStats()}");
            Console.WriteLine("\n Waiting for client connections...\n");

            // Main accept loop - keeps accepting new connections
            while (_isRunning)
            {
                try
                {
                    // BLOCKING CALL - waits here until a client connects
                    TcpClient client = _listener.AcceptTcpClient();

                    // KEY CONCEPT: Instead of handling the client HERE,
                    // we give it to the ThreadPool to handle in a separate thread!
                    // This allows us to immediately return and accept the next connection
                    ThreadPool.QueueUserWorkItem(HandleClient, client);

                    Console.WriteLine($" New connection accepted. Active sessions: {_activeSessions.Count}");
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Console.WriteLine($" Error accepting connection: {ex.Message}");
                }
            }
        }


        /// This method runs in a SEPARATE THREAD for each connected client!

        private void HandleClient(object? obj)
        {
            if (obj is not TcpClient client) return;

            // Generate unique session ID for this connection
            string sessionId = Guid.NewGuid().ToString("N")[..8];
            NetworkStream? stream = null;

            try
            {
                stream = client.GetStream();
                string clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";

                // Create session tracking object
                var session = new ClientSession
                {
                    SessionId = sessionId,
                    ClientEndpoint = clientEndpoint,
                    ConnectedAt = DateTime.Now
                };

                _activeSessions.TryAdd(sessionId, session);

                Console.WriteLine($" [{sessionId}] Client connected from {clientEndpoint}");

                // Set timeouts (30 seconds)
                stream.ReadTimeout = 30000;
                stream.WriteTimeout = 30000;

                // Message processing loop - keep reading messages from this client
                while (client.Connected && _isRunning)
                {
                    // Step 1: Read message length header
                    // Default: 2-byte big-endian length.
                    // Some clients use 4-byte big-endian length; we attempt a safe fallback if the 2-byte value is invalid.
                    byte[] lengthBytes = ReadExactOrNull(stream, 2);
                    if (lengthBytes == null) break; // Client disconnected

                    int messageLength = (lengthBytes[0] << 8) | lengthBytes[1];
                    string lengthHex = BitConverter.ToString(lengthBytes);

                    const int maxPayloadLength = 65535;
                    if (messageLength <= 0 || messageLength > maxPayloadLength)
                    {
                        // Fallback: treat the first 2 bytes as the high-order bytes of a 4-byte big-endian length.
                        byte[] remainingLenBytes = ReadExactOrNull(stream, 2);
                        if (remainingLenBytes == null) break;

                        byte[] len4 = new byte[4]
                        {
                            lengthBytes[0],
                            lengthBytes[1],
                            remainingLenBytes[0],
                            remainingLenBytes[1]
                        };

                        int len32 = (len4[0] << 24) | (len4[1] << 16) | (len4[2] << 8) | len4[3];
                        string len4Hex = BitConverter.ToString(len4);

                        // If 4-byte length is still invalid, close the connection to avoid desync.
                        if (len32 <= 0 || len32 > maxPayloadLength)
                        {
                            Console.WriteLine($"  [{sessionId}] Invalid message length (2B={messageLength}, hex={lengthHex}; 4B={len32}, hex={len4Hex})");
                            break;
                        }

                        messageLength = len32;
                        Console.WriteLine($"  [{sessionId}] Detected 4-byte length header. len={messageLength}, hex={len4Hex}");
                    }

                    // Step 2: Read the actual ISO-8583 message
                    byte[]? messageBytes = ReadExactOrNull(stream, messageLength);
                    if (messageBytes == null)
                    {
                        Console.WriteLine($"  [{sessionId}] Incomplete message (expected {messageLength} bytes)");
                        break;
                    }

                    Console.WriteLine($" [{sessionId}] Received {messageLength} bytes");

                    PrintRawMessage(messageBytes, sessionId);

                    byte[] isoPayload = messageBytes;
                    int headerLen = 0;

                    // Try: assume 5-byte TPDU header (common ISO8583 framing)
                    if (messageLength > 5)
                    {
                        // Check if bytes 5-8 look like a valid ISO MTI (4 ASCII digits)
                        bool looksLikeIsoAtOffset5 = messageLength >= 9 &&
                            IsAsciiDigit(messageBytes[5]) &&
                            IsAsciiDigit(messageBytes[6]) &&
                            IsAsciiDigit(messageBytes[7]) &&
                            IsAsciiDigit(messageBytes[8]);

                        if (looksLikeIsoAtOffset5)
                        {
                            headerLen = 5;
                            isoPayload = messageBytes[5..];
                            Console.WriteLine($" [{sessionId}] Detected 5-byte TPDU header. ISO payload starts at byte 5 ({isoPayload.Length} bytes).");
                            Console.WriteLine($" [{sessionId}] First 20 bytes of ISO: {BitConverter.ToString(isoPayload, 0, Math.Min(20, isoPayload.Length))}");
                        }
                    }

                    // Step 3: Process the message and get response
                    byte[]? responseBytes = ProcessMessage(isoPayload, sessionId);

                    // Step 4: Send response back to client
                    if (responseBytes != null && responseBytes.Length > 0)
                    {
                        // Write length header
                        byte[] responseLengthBytes = new byte[2];
                        responseLengthBytes[0] = (byte)(responseBytes.Length >> 8);
                        responseLengthBytes[1] = (byte)(responseBytes.Length & 0xFF);

                        stream.Write(responseLengthBytes, 0, 2);
                        stream.Write(responseBytes, 0, responseBytes.Length);
                        stream.Flush();

                        Console.WriteLine($" [{sessionId}] Sent {responseBytes.Length} bytes response\n");
                    }

                    // Update session stats
                    session.MessageCount++;
                    session.LastActivity = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" [{sessionId}] Error: {ex.Message}");
            }
            finally
            {
                // Cleanup
                stream?.Close();
                client?.Close();
                _activeSessions.TryRemove(sessionId, out _);

                Console.WriteLine($" [{sessionId}] Client disconnected. Active sessions: {_activeSessions.Count}\n");
            }
        }

        private static byte[]? ReadExactOrNull(NetworkStream stream, int length)
        {
            if (length <= 0) return Array.Empty<byte>();

            byte[] buffer = new byte[length];
            int totalRead = 0;
            while (totalRead < length)
            {
                int read = stream.Read(buffer, totalRead, length - totalRead);
                if (read <= 0) return null;
                totalRead += read;
            }

            return buffer;
        }

        private static void PrintRawMessage(byte[] messageBytes, string sessionId)
        {
            Console.WriteLine($" [{sessionId}] Raw Message (HEX):");
            string hexString = BitConverter.ToString(messageBytes).Replace("-", " ");
            Console.WriteLine($"   {hexString}");

            string asciiString = new string(messageBytes.Select(b => b >= 32 && b <= 126 ? (char)b : '.').ToArray());
            Console.WriteLine($" [{sessionId}] Raw Message (ASCII):");
            Console.WriteLine($"   {asciiString}");
        }

        private static bool IsAsciiDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';

        private static (byte[] payload, int headerLength) ExtractIsoPayload(byte[] received)
        {
            // Heuristic: find the first 4 ASCII digits which look like an MTI (e.g. 0200/0210/0400/0800).
            // If present after a binary/TPDU-style header, strip everything before MTI.
            if (received.Length < 4) return (received, 0);

            static bool IsDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';
            static bool LooksLikeMti(ReadOnlySpan<byte> s)
            {
                if (s.Length < 4) return false;
                if (!(IsDigit(s[0]) && IsDigit(s[1]) && IsDigit(s[2]) && IsDigit(s[3]))) return false;
                // Common MTIs in this switch.
                if (s[0] != (byte)'0') return false;
                return s[1] == (byte)'1' || s[1] == (byte)'2' || s[1] == (byte)'4' || s[1] == (byte)'8';
            }

            for (int i = 0; i <= received.Length - 4; i++)
            {
                if (LooksLikeMti(received.AsSpan(i, 4)))
                {
                    if (i == 0) return (received, 0);
                    return (received[i..], i);
                }
            }

            // If we can't find an MTI, don't strip anything.
            return (received, 0);
        }

        
        /// Process an incoming ISO-8583 message
        /// This is where the routing magic happens!
        
        private byte[]? ProcessMessage(byte[] messageBytes, string sessionId)
        {
            var stopwatch = Stopwatch.StartNew();
            TransactionContext? txnContext = null;
            string? txnId = null;
            
            try
            {
                // Decode ASCII-hex payload if client sent textual hex
                if (LooksLikeHexAscii(messageBytes))
                {
                    string hex = System.Text.Encoding.ASCII.GetString(messageBytes);
                    messageBytes = HexToBytesSafe(hex);
                    Console.WriteLine($" [{sessionId}] Detected ASCII-hex payload. Decoded to {messageBytes.Length} bytes.");
                }

                IsoMessage request;
                try {
                    request = _parser.Parse(messageBytes);
                } catch (Exception ex) {
                    Console.WriteLine($"[{sessionId}] Parse error: {ex.Message}");
                    var resp = CreateErrorResponse(new IsoMessage { MessageType = "0200" }, "30");
                    return _parser.Build(resp);
                }

                txnId = $"{request.GetField(11)}_{DateTime.UtcNow.Ticks}";
                txnContext = _stateMachine.CreateTransaction(sessionId, request);

                // Normalize local time/date fields (DE12/DE13) to Vietnam time if missing/invalid
                NormalizeLocalDateTimeFields(request);

                var validationResult = _validator.ValidateMessage(request);
                if (!validationResult.IsValid)
                {
                    Console.WriteLine($" [{sessionId}] VALIDATION FAILED:");
                    Console.WriteLine(validationResult.GetSummary());
                    
                    txnContext.TryTransitionTo(TransactionState.ValidationFailed, 
                        validationResult.GetFirstErrorCode(), 
                        "Message validation failed");
                    
                    var errorResponse = CreateErrorResponse(request, validationResult.GetFirstErrorCode());
                    return _parser.Build(errorResponse);
                }
                
                txnContext.TryTransitionTo(TransactionState.Validated);
                
                Console.WriteLine($" [{sessionId}] Validation PASSED");
                Console.WriteLine($" [{sessionId}] Message Details:");
                Console.WriteLine($"   MTI: {request.MessageType}");
                Console.WriteLine($"   DE2 (PAN): {SecureDataHandler.MaskPAN(request.GetField(2))}");
                Console.WriteLine($"   DE3 (Proc Code): {request.GetField(3)}");
                Console.WriteLine($"   DE4 (Amount): {request.GetField(4)}");
                Console.WriteLine($"   DE11 (STAN): {request.GetField(11)}");

                string? clearPan = request.GetField(2);
                string? encryptedPan = null;
                if (!string.IsNullOrEmpty(clearPan))
                {
                    encryptedPan = _securityProvider.EncryptPAN(clearPan);
                }

                if (_pendingStore != null && (request.MessageType == "0200" || request.MessageType == "0400"))
                {
                    var requestCopy = new IsoMessage { MessageType = request.MessageType };
                    foreach (var field in request.Fields)
                    {
                        requestCopy.SetField(field.Key, field.Key == 2 && encryptedPan != null ? encryptedPan : field.Value);
                    }
                    _ = _pendingStore.StoreRequestAsync(txnId, sessionId, requestCopy, messageBytes);
                }

                if (_transactionLogger != null)
                {
                    var logRequest = new IsoMessage { MessageType = request.MessageType };
                    foreach (var field in request.Fields)
                    {
                        logRequest.SetField(field.Key, field.Key == 2 && encryptedPan != null ? encryptedPan : field.Value);
                    }
                    _ = _transactionLogger.LogRequestAsync(logRequest, sessionId, "INBOUND");
                }

                IsoMessage response = request.MessageType switch
                {
                    "0200" => HandleAuthorizationRequest(request, sessionId, txnContext),
                    "0400" => HandleReversalRequest(request, sessionId, txnContext),
                    "0800" => HandleNetworkManagement(request, sessionId),
                    _ => CreateErrorResponse(request, "12")
                };

                if (_pendingStore != null && txnId != null && (request.MessageType == "0200" || request.MessageType == "0400"))
                {
                    var pendingTxn = _pendingStore.GetRequestAsync(txnId).GetAwaiter().GetResult();
                    
                    if (pendingTxn != null)
                    {
                        var correlationResult = _correlationValidator.ValidateResponseMatchesRequest(request, response);
                        
                        if (!correlationResult.IsValid)
                        {
                            Console.WriteLine($"[CORRELATION-ERROR] Response does not match request:");
                            Console.WriteLine(correlationResult.GetSummary());
                            
                            _pendingStore.MarkAsMismatchAsync(txnId, "Correlation validation failed").GetAwaiter().GetResult();
                            
                            txnContext.TryTransitionTo(TransactionState.SystemError, "30", "Response correlation failed");
                            response = CreateErrorResponse(request, "30");
                        }
                        else
                        {
                            _pendingStore.MarkAsMatchedAsync(txnId, response.GetField(39) ?? "96").GetAwaiter().GetResult();
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[CORRELATION-WARN] No pending transaction found for {txnId}");
                    }
                }

                txnContext.Response = response;
                txnContext.TryTransitionTo(TransactionState.Completed);

                var responseValidation = _validator.ValidateMessage(response);
                if (!responseValidation.IsValid)
                {
                    Console.WriteLine($" [{sessionId}] WARNING: Response validation failed:");
                    Console.WriteLine(responseValidation.GetSummary());
                }

                byte[] responseBytes = _parser.Build(response);

                stopwatch.Stop();
                int processingTimeMs = (int)stopwatch.ElapsedMilliseconds;

                if (_transactionLogger != null)
                {
                    _ = _transactionLogger.LogTransactionAsync(request, response, sessionId, processingTimeMs, "COMPLETE");
                }

                string rcDesc = ConfigurationLoader.Instance.GetResponseDescription(response.GetField(39) ?? "96");
                Console.WriteLine($" [{sessionId}] Response: {response.MessageType} | DE39 (RC): {response.GetField(39)} - {rcDesc} | Time: {processingTimeMs}ms");

                return responseBytes;   
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Console.WriteLine($" [{sessionId}] Processing error: {ex.Message}");
                
                if (txnContext != null)
                {
                    txnContext.TryTransitionTo(TransactionState.SystemError, "96", ex.Message);
                }
                
                return null;
            }
        }

     
        private IsoMessage HandleAuthorizationRequest(IsoMessage request, string sessionId, TransactionContext txnContext)
        {
            string? cardBIN = request.GetCardBIN();

            if (string.IsNullOrEmpty(cardBIN))
            {
                Console.WriteLine($"  [{sessionId}] No card BIN found");
                txnContext.TryTransitionTo(TransactionState.ValidationFailed, "14", "Invalid card BIN");
                return CreateErrorResponse(request, "14");
            }

            var issuerBank = ConfigurationLoader.Instance.GetIssuerByBIN(cardBIN);

            if (issuerBank == null)
            {
                Console.WriteLine($"  [{sessionId}] No issuer found for BIN: {cardBIN}");
                txnContext.TryTransitionTo(TransactionState.RoutingFailed, "15", "No such issuer");
                return CreateErrorResponse(request, "15");
            }

            Console.WriteLine($" [{sessionId}] Routing to ISS: {issuerBank.IssuerName} ({issuerBank.IssuerCode})");

            request.SetIssuerID(issuerBank.IssuerCode);
            
            txnContext.TryTransitionTo(TransactionState.RoutingToIssuer);
            txnContext.SentToIssuerAt = DateTime.UtcNow;

            IsoMessage response = _issuerConnector.ForwardToIssuer(request, issuerBank, sessionId);
            
            txnContext.ResponseReceivedAt = DateTime.UtcNow;
            txnContext.TryTransitionTo(TransactionState.ResponseReceived);

            return response;
        }

        
        /// Handle 0400 - Reversal Request (Void/Cancel)
        
        private IsoMessage HandleReversalRequest(IsoMessage request, string sessionId, TransactionContext txnContext)
        {
            Console.WriteLine($" [{sessionId}] Processing reversal request");
            
            txnContext.TryTransitionTo(TransactionState.ReversalPending);

            string? cardBIN = request.GetCardBIN();
            var issuerBank = ConfigurationLoader.Instance.GetIssuerByBIN(cardBIN ?? "");

            if (issuerBank == null)
            {
                Console.WriteLine($"  [{sessionId}] No issuer found");
                txnContext.TryTransitionTo(TransactionState.ReversalFailed, "15", "No such issuer");
                return CreateErrorResponse(request, "15");
            }

            Console.WriteLine($" [{sessionId}] Routing reversal to ISS: {issuerBank.IssuerName}");

            request.SetIssuerID(issuerBank.IssuerCode);

            IsoMessage response = _issuerConnector.ForwardToIssuer(request, issuerBank, sessionId);
            
            txnContext.TryTransitionTo(TransactionState.ReversalCompleted);

            return response;
        }

        
        /// Handle 0800 - Network Management (heartbeat, sign-on)
        
        private IsoMessage HandleNetworkManagement(IsoMessage request, string sessionId)
        {
            Console.WriteLine($" [{sessionId}] Network management message");

            var response = new IsoMessage
            {
                MessageType = "0810" // Network response
            };

            // Echo back key fields
            if (request.HasField(7)) response.SetField(7, request.GetField(7));
            if (request.HasField(11)) response.SetField(11, request.GetField(11));
            if (request.HasField(70)) response.SetField(70, request.GetField(70));

            response.SetResponseCode("00");
            return response;
        }

        
        /// Create a successful response (0210 or 0410)
        
        private IsoMessage CreateSuccessResponse(IsoMessage request, bool isReversal = false)
        {
            var response = new IsoMessage
            {
                MessageType = isReversal ? "0410" : "0210"
            };

            // Copy key fields from request
            foreach (var field in new[] { 2, 3, 4, 7, 11, 12, 13, 32, 37, 41, 42 })
            {
                if (request.HasField(field))
                    response.SetField(field, request.GetField(field));
            }

            response.SetResponseCode("00"); // Approved
            return response;
        }

        
        /// Create an error response with specific response code
        
        private IsoMessage CreateErrorResponse(IsoMessage request, string responseCode)
        {
            var response = new IsoMessage
            {
                MessageType = request.MessageType == "0200" ? "0210" :
                              request.MessageType == "0400" ? "0410" : "0210"
            };

            // Copy key fields
            foreach (var field in new[] { 2, 3, 4, 7, 11, 12, 13, 32, 37, 41, 42 })
            {
                if (request.HasField(field))
                    response.SetField(field, request.GetField(field));
            }

            response.SetResponseCode(responseCode);
            return response;
        }

        
        /// Mask PAN for security (show first 6 and last 4 digits only)
        
        private string MaskPAN(string? pan)
        {
            if (string.IsNullOrEmpty(pan) || pan.Length < 10)
                return "****";

            return $"{pan[..6]}****{pan[^4..]}";
        }

        private void NormalizeLocalDateTimeFields(IsoMessage msg)
        {
            var vnTz = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
            var nowVn = TimeZoneInfo.ConvertTime(DateTime.UtcNow, vnTz);

            EnsureNumericField(msg, 7, nowVn.ToString("MMddHHmmss"), 10); // Transmission date/time
            EnsureNumericField(msg, 12, nowVn.ToString("HHmmss"), 6); // Local time
            EnsureNumericField(msg, 13, nowVn.ToString("MMdd"), 4);   // Local date
        }

        private void EnsureNumericField(IsoMessage msg, int field, string fallback, int requiredLength)
        {
            string? val = msg.GetField(field);

            bool needsReplace = string.IsNullOrEmpty(val)
                || val!.Length != requiredLength
                || !val.All(char.IsDigit)
                || val.All(c => c == '0');

            if (needsReplace)
            {
                msg.SetField(field, fallback);
            }
        }

        
        /// Stop the server
        
        public void Stop()
        {
            _isRunning = false;
            _listener?.Stop();
            Console.WriteLine(" Server stopped");
        }

        
        /// Get current server statistics
        
        public ServerStats GetStats()
        {
            return new ServerStats
            {
                ActiveConnections = _activeSessions.Count,
                TotalMessageCount = _activeSessions.Values.Sum(s => s.MessageCount),
                UptimeSince = DateTime.Now // TODO: Track actual start time
            };
        }

        
        /// Dispose resources
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();

            _statsTimer?.Dispose();
            _poolHealthTimer?.Dispose();
            _cleanupTimer?.Dispose();
            _issuerConnector?.Dispose();
            _transactionLogger?.Dispose();
            _stateMachine?.Dispose();
            
            Console.WriteLine("[DISPOSE] TcpSwitchServer disposed");
        }

        private static bool LooksLikeHexAscii(byte[] data)
        {
            if (data.Length < 8 || data.Length % 2 != 0) return false;
            foreach (byte b in data)
            {
                bool isHexDigit = (b >= '0' && b <= '9') || (b >= 'A' && b <= 'F') || (b >= 'a' && b <= 'f');
                if (!isHexDigit) return false;
            }
            return true;
        }

        private static byte[] HexToBytesSafe(string hex)
        {
            int len = hex.Length;
            byte[] result = new byte[len / 2];
            for (int i = 0; i < len; i += 2)
            {
                result[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }
            return result;
        }
    }

    
    /// Tracks information about a connected client session
    
    public class ClientSession
    {
        public string SessionId { get; set; } = string.Empty;
        public string ClientEndpoint { get; set; } = string.Empty;
        public DateTime ConnectedAt { get; set; }
        public DateTime LastActivity { get; set; }
        public int MessageCount { get; set; }
    }

    
    /// Server statistics for monitoring
    
    public class ServerStats
    {
        public int ActiveConnections { get; set; }
        public int TotalMessageCount { get; set; }
        public DateTime UptimeSince { get; set; }
    }

    // Add this test method to verify database connection
    public static class DatabaseTester
    {
        public static bool TestConnection(string connectionString)
        {
            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    Console.WriteLine("[DB-TEST] Connection successful!");
                    
                    // Test if table exists
                    var cmd = new SqlCommand(
                        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TransactionLog'", 
                        connection);
                    
                    int tableCount = (int)cmd.ExecuteScalar();
                    
                    if (tableCount == 0)
                    {
                        Console.WriteLine("[DB-TEST] WARNING: TransactionLog table does not exist");
                        return false;
                    }
                    
                    Console.WriteLine("[DB-TEST] TransactionLog table exists");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB-TEST] Connection failed: {ex.Message}");
                return false;
            }
        }
    }
}
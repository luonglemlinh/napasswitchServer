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

    // Persistent connection to TS (Transaction Switch)
    private TSPersistentConnection? _tsConnection;

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
            
        // Initialize persistent TS connection (for default issuer)
        InitializeTSConnection();
            
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

    /// <summary>
    /// Initialize persistent connection to the default TS
    /// </summary>
    private void InitializeTSConnection()
    {
        // Find the default TS from configuration
        var allIssuers = ConfigurationLoader.Instance.GetAllIssuers();
        var defaultTS = allIssuers.FirstOrDefault(i => i.IsDefault);
            
        if (defaultTS != null)
        {
            Console.WriteLine($"[INIT] Found default TS: {defaultTS.IssuerName} at {defaultTS.Host}:{defaultTS.Port}");
            _tsConnection = new TSPersistentConnection(defaultTS, heartbeatIntervalMs: 30000);
            Console.WriteLine("[INIT] TS persistent connection manager created");
        }
        else
        {
            Console.WriteLine("[INIT] No default TS configured, using per-transaction connections");
            _tsConnection = null;
        }
    }

        /// <summary>
        /// Connect to TS 
        /// </summary>
        public async Task ConnectToTSAsync()
        {
            if (_tsConnection != null)
            {
                Console.WriteLine("[TS] Establishing persistent connection...");
                bool connected = await _tsConnection.ConnectAsync();
                if (connected)
                {
                    Console.WriteLine("[TS] ✓ Connected successfully!");
                }
                else
                {
                    Console.WriteLine("[TS] ✗ Failed to connect");
                }
            }
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
                    // we use Task.Run to handle it in a separate task!
                    // This allows us to immediately return and accept the next connection
                    _ = Task.Run(() => HandleClientAsync(client));

                    Console.WriteLine($" New connection accepted. Active sessions: {_activeSessions.Count}");
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Console.WriteLine($" Error accepting connection: {ex.Message}");
                }
            }
        }


        /// This method runs in a SEPARATE TASK for each connected client!

        private async Task HandleClientAsync(TcpClient client)
        {
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
                    // Client team says header is 4 bytes ASCII (e.g., "0123")
                    byte[] lengthBytes = ReadExactOrNull(stream, 4);
                    if (lengthBytes == null) break; // Client disconnected

                    int messageLength;
                    string lengthStr = System.Text.Encoding.ASCII.GetString(lengthBytes);

                    if (int.TryParse(lengthStr, out int asciiLen) && asciiLen > 0 && asciiLen < 65535)
                    {
                        messageLength = asciiLen;
                        Console.WriteLine($"  [{sessionId}] Detected 4-byte ASCII length header: {lengthStr} (len={messageLength})");
                    }
                    else
                    {
                        // Fallback: try to interpret the 4 bytes as Big-Endian binary (some clients might still use this)
                        int binLen4 = (lengthBytes[0] << 24) | (lengthBytes[1] << 16) | (lengthBytes[2] << 8) | lengthBytes[3];
                        
                        // Or try 2-byte binary (legacy) if the first 2 bytes were actually the length and we over-read
                        int binLen2 = (lengthBytes[0] << 8) | lengthBytes[1];

                        if (binLen4 > 0 && binLen4 < 65535)
                        {
                            messageLength = binLen4;
                            Console.WriteLine($"  [{sessionId}] Detected 4-byte binary length header. len={messageLength}");
                        }
                        else if (binLen2 > 0 && binLen2 < 65535)
                        {
                            messageLength = binLen2;
                            Console.WriteLine($"  [{sessionId}] Detected 2-byte binary length header (over-read 2 bytes). len={messageLength}");
                        }
                        else
                        {
                            Console.WriteLine($"  [{sessionId}] Invalid message length header: {BitConverter.ToString(lengthBytes)}");
                            break;
                        }
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

                    // TPDU detection disabled per user request
                    /*
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
                            isoPayload = messageBytes[5..];
                            Console.WriteLine($" [{sessionId}] Detected 5-byte TPDU header. ISO payload starts at byte 5 ({isoPayload.Length} bytes).");
                            Console.WriteLine($" [{sessionId}] First 20 bytes of ISO: {BitConverter.ToString(isoPayload, 0, Math.Min(20, isoPayload.Length))}");
                        }
                    }
                    */

                    // Step 3: Process the message and get response
                    byte[]? responseBytes = await ProcessMessageAsync(isoPayload, sessionId);

                    // Step 4: Send response back to client
                    if (responseBytes != null && responseBytes.Length > 0)
                    {
                        // Write length header (4-byte ASCII per NAPAS specification)
                        string respLengthStr = responseBytes.Length.ToString("D4");
                        byte[] lengthHeader = System.Text.Encoding.ASCII.GetBytes(respLengthStr);
 
                        stream.Write(lengthHeader, 0, 4);
                        stream.Write(responseBytes, 0, responseBytes.Length);
                        stream.Flush();
 
                        Console.WriteLine($" [{sessionId}] Sent {responseBytes.Length} bytes response (Length Header: {respLengthStr})\n");
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
        
        private async Task<byte[]?> ProcessMessageAsync(byte[] messageBytes, string sessionId)
        {
            var stopwatch = Stopwatch.StartNew();
            TransactionContext? txnContext = null;
            
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
                    
                    // Full message dump for debugging (Acquirer to Switch)
                    request.LogAllFields(sessionId, "ACQ-INBOUND");

                    // Normalize Track 2 (DE#35) for WAY4 compliance (Max 37, separator 'D', no 'F' padding, strip sentinels)
                    if (request.HasField(35))
                    {
                        string track2 = request.GetField(35)!;
                        
                        // 1. Strip sentinels if present (';', '?', and others per ISO 7813)
                        track2 = track2.Trim(';', '?', ' ');

                        // 2. Strip 'F' padding characters (often found in chip data)
                        track2 = track2.Replace("F", "").Replace("f", "");

                        // 3. Normalize all separators ('=' -> 'D')
                        // Per ISO-8583 DE35, 'D' (0x44) is the field separator
                        track2 = track2.Replace('=', 'D');

                        // 4. Truncate to 37 characters if it's too long (WAY4 limit)
                        if (track2.Length > 37)
                        {
                             track2 = track2.Substring(0, 37);
                        }
                        
                        request.SetField(35, track2);
                    }

                    // TESTING: Print F00 Header
                    if (!string.IsNullOrEmpty(request.Header))
                    {
                        Console.WriteLine($" [{sessionId}] F00: {request.Header}");
                    }
                } catch (Exception ex) {
                    Console.WriteLine($"[{sessionId}] Parse error: {ex.Message}");
                    var resp = CreateErrorResponse(new IsoMessage { MessageType = "0200" }, "30");
                    return _parser.Build(resp);
                }

                txnContext = _stateMachine.CreateTransaction(sessionId, request);

                // Normalize local time/date fields (DE12/DE13) to Vietnam time if missing/invalid
                NormalizeLocalDateTimeFields(request);

                // Guarantee mandatory Napas fields are present (Merchant ID, POS Mode, etc.)
                if (request.MessageType == "0200" || request.MessageType == "0400")
                {
                    EnsureMandatoryNapasFields(request, sessionId);
                }

                // --- DE63 (TRN) Generation Logic ---
                // Generated by the switch for new transactions
                if (request.MessageType == "0200")
                {
                    // Generate unique 16-char alphanumeric TRN per NAPAS style
                    // Example Style: AAcBhQE0XrjDugEJ
                    txnContext.TRN = GenerateAlphaNumericTRN(16);
                    request.SetTRN(txnContext.TRN);
                    Console.WriteLine($" [{sessionId}] Generated TRN (DE#63): {txnContext.TRN}");
                }
                else if (request.MessageType == "0400")
                {
                    // For reversals, ensure we use the original TRN if already in context
                    // Client usually sends TRN of original in reversal DE#63
                    string? incomingTrn = request.GetTRN();
                    if (!string.IsNullOrEmpty(incomingTrn))
                    {
                        txnContext.TRN = incomingTrn;
                    }
                }

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
                if (request.HasField(0))
                {
                    Console.WriteLine($"   000: {request.GetField(0)}");
                }
                Console.WriteLine($"   Type: {request.MessageType}");
                Console.WriteLine($"   002: {SecureDataHandler.MaskPAN(request.GetField(2))}");
                Console.WriteLine($"   003: {request.GetField(3)}");
                Console.WriteLine($"   004: {request.GetField(4)}");
                Console.WriteLine($"   007: {request.GetField(7)}");
                Console.WriteLine($"   011: {request.GetField(11)}");
                Console.WriteLine($"   012: {request.GetField(12)}");
                Console.WriteLine($"   013: {request.GetField(13)}");
                Console.WriteLine($"   014: {request.GetField(14)}");
                Console.WriteLine($"   022: {request.GetField(22)}");
                Console.WriteLine($"   025: {request.GetField(25)}");
                
                // DE32: Show actual value and explain LLVAR encoding
                string? de32Value = request.GetField(32);
                if (!string.IsNullOrEmpty(de32Value))
                {
                    Console.WriteLine($"   032: {de32Value} (LLVAR encoded as: {de32Value.Length:D2}{de32Value})");
                }
                
                Console.WriteLine($"   033: {request.GetField(33)}");
                Console.WriteLine($"   035: {request.GetField(35)}");
                Console.WriteLine($"   037: {request.GetField(37)}");
                Console.WriteLine($"   041: {request.GetField(41)}");
                Console.WriteLine($"   042: {request.GetField(42)}");
                Console.WriteLine($"   049: {request.GetField(49)}");
                Console.WriteLine($"   063: {request.GetTRN()}");

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
                    _ = _pendingStore.StoreRequestAsync(txnContext.TransactionId, sessionId, requestCopy, messageBytes);
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
                    "0200" => await HandleAuthorizationRequestAsync(request, sessionId, txnContext),
                    "0400" => await HandleReversalRequestAsync(request, sessionId, txnContext),
                    "0800" => HandleNetworkManagement(request, sessionId),
                    _ => CreateErrorResponse(request, "12")
                };

                // Ensure TRN is back in the response
                if (!string.IsNullOrEmpty(txnContext.TRN))
                {
                    response.SetTRN(txnContext.TRN);
                }
                
                if (_pendingStore != null && (request.MessageType == "0200" || request.MessageType == "0400"))
                {
                    var pendingTxn = _pendingStore.GetRequestAsync(txnContext.TransactionId).GetAwaiter().GetResult();
                    
                    if (pendingTxn != null)
                    {
                        var correlationResult = _correlationValidator.ValidateResponseMatchesRequest(request, response);
                        
                        if (!correlationResult.IsValid)
                        {
                            Console.WriteLine($"[CORRELATION-ERROR] Response does not match request:");
                            Console.WriteLine(correlationResult.GetSummary());
                            
                            _pendingStore.MarkAsMismatchAsync(txnContext.TransactionId, "Correlation validation failed").GetAwaiter().GetResult();
                            
                            txnContext.TryTransitionTo(TransactionState.SystemError, "30", "Response correlation failed");
                            response = CreateErrorResponse(request, "30");
                        }
                        else
                        {
                            _pendingStore.MarkAsMatchedAsync(txnContext.TransactionId, response.GetField(39) ?? "96").GetAwaiter().GetResult();
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[CORRELATION-WARN] No pending transaction found for {txnContext.TransactionId}");
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
                else
                {
                    Console.WriteLine($" [{sessionId}] Response validation PASSED (RC: {response.GetField(39) ?? "00"})");
                }

                // Full message dump for debugging (Switch to Acquirer)
                response.LogAllFields(sessionId, "ACQ-OUTBOUND");

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

     
     
        private async Task<IsoMessage> HandleAuthorizationRequestAsync(IsoMessage request, string sessionId, TransactionContext txnContext)
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
 
            // NAPAS Routing Logic:
            // DE#32 (Acquiring Institution ID): MUST be the Acquirer (e.g. 970418), NOT the Switch (970488)
            // DE#33 (Forwarding Institution ID): Equal to Switch ID (970488)
            
            string switchId = issuerBank.IssuerCode; // 970488 for NAPAS TS
            string defaultAcquirer = "970418"; // BIDV
 
            // 1. Handle DE#32 (Acquirer ID)
            string currentDe32 = request.GetField(32) ?? string.Empty;
            if (string.IsNullOrEmpty(currentDe32))
            {
                request.SetField(32, defaultAcquirer);
                Console.WriteLine($" [{sessionId}] DE#32 missing, setting default: {defaultAcquirer}");
            }
            else if (currentDe32 == switchId && issuerBank.IsDefault)
            {
                // CRITICAL FIX: If DE#32 equals Switch ID (970488), it's logically wrong for TS routing.
                request.SetField(32, defaultAcquirer);
                Console.WriteLine($" [{sessionId}] DE#32 was {currentDe32} (Switch ID), forced to {defaultAcquirer} (Acquirer ID) to prevent RC:30");
            }
            else
            {
                Console.WriteLine($" [{sessionId}] DE#32 preserved: {currentDe32}");
            }
            
            // 2. Handle DE#33 (Forwarding ID)
            if (issuerBank.IsDefault)
            {
                request.SetField(33, switchId); // 970488
                Console.WriteLine($" [{sessionId}] DE#33 set to Switch ID: {switchId}");
            }
            else
            {
                request.SetIssuerID(issuerBank.IssuerCode);
            }
            
            txnContext.TryTransitionTo(TransactionState.RoutingToIssuer);
            txnContext.SentToIssuerAt = DateTime.UtcNow;

            IsoMessage response;

            // Use persistent connection for default TS, otherwise use per-transaction connection
            if (issuerBank.IsDefault && _tsConnection != null)
            {
                Console.WriteLine($" [{sessionId}] Using persistent TS connection");
                var tsResponse = await _tsConnection.ForwardTransactionAsync(request, sessionId);
                
                if (tsResponse != null)
                {
                    response = tsResponse;
                }
                else
                {
                    Console.WriteLine($" [{sessionId}] TS connection failed, returning system error");
                    txnContext.TryTransitionTo(TransactionState.SystemError, "91", "TS unavailable");
                    return CreateErrorResponse(request, "91");
                }
            }
            else
            {
                // Use per-transaction connection for other issuers
                response = _issuerConnector.ForwardToIssuer(request, issuerBank, sessionId);
            }
            
            txnContext.ResponseReceivedAt = DateTime.UtcNow;
            txnContext.TryTransitionTo(TransactionState.ResponseReceived);

            return response;
        }

        
        /// Handle 0400 - Reversal Request (Void/Cancel)
        
        private async Task<IsoMessage> HandleReversalRequestAsync(IsoMessage request, string sessionId, TransactionContext txnContext)
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

            Console.WriteLine($" [{sessionId}] Routing reversal to ISS: {issuerBank.IssuerName}");

            string currentDe32 = request.GetField(32) ?? string.Empty;
            string switchId = issuerBank.IssuerCode; 

            // 1. Handle DE#32 (Acquirer ID) for Reversal
            if (string.IsNullOrEmpty(currentDe32))
            {
                request.SetField(32, "970400");
                Console.WriteLine($" [{sessionId}] DE#32 missing, setting default: 970400");
            }
            else if (currentDe32 == switchId && issuerBank.IsDefault)
            {
                request.SetField(32, "970400");
                Console.WriteLine($" [{sessionId}] DE#32 was {currentDe32}, forced to 970400 (Acquirer ID)");
            }
            
            // 2. Handle DE#33 (Forwarding ID)
            if (issuerBank.IsDefault)
            {
                request.SetField(33, switchId);
                Console.WriteLine($" [{sessionId}] DE#33 set to Switch ID: {switchId}");
            }
            else
            {
                request.SetIssuerID(issuerBank.IssuerCode);
            }

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
            var utcNow = DateTime.UtcNow;
            var vnTz = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
            var nowVn = TimeZoneInfo.ConvertTime(utcNow, vnTz);

            // DE#7 MUST be GMT (UTC) per ISO standard
            if (!msg.HasField(7)) msg.SetField(7, utcNow.ToString("MMddHHmmss"));
            
            // DE#12 and DE#13 are Local Time
            if (!msg.HasField(12)) msg.SetField(12, nowVn.ToString("HHmmss"));
            if (!msg.HasField(13)) msg.SetField(13, nowVn.ToString("MMdd"));
        }

        private void EnsureMandatoryNapasFields(IsoMessage request, string sessionId)
        {
            // Fields required by NAPAS for 0200/0400 transactions
            
            // DE22: POS Entry Mode (051 for Chip, 021 for Magstripe, 011 for Manual/NFC)
            if (!request.HasField(22))
            {
                request.SetField(22, "011"); 
                Console.WriteLine($" [{sessionId}] DE#22 missing, setting default: 011");
            }

            // DE25: POS Condition Code (00 for Normal)
            if (!request.HasField(25))
            {
                request.SetField(25, "00");
                Console.WriteLine($" [{sessionId}] DE#25 missing, setting default: 00");
            }

            // DE42: Card Acceptor ID (Merchant ID)
            if (!request.HasField(42))
            {
                request.SetField(42, "NAPAS_MERCHT_01");
                Console.WriteLine($" [{sessionId}] DE#42 missing, setting default: NAPAS_MERCHT_01");
            }

            // DE43: Card Acceptor Name/Location
            if (!request.HasField(43))
            {
                request.SetField(43, "NAPAS TEST MERCHANT       HANOI        VN");
                Console.WriteLine($" [{sessionId}] DE#43 missing, setting default: NAPAS TEST MERCHANT");
            }
            
            // DE49: Currency Code (704 for VND)
            if (!request.HasField(49))
            {
                request.SetField(49, "704");
            }
            
            // DE18: Merchant Category Code
            if (!request.HasField(18))
            {
                request.SetField(18, "6011"); // ATM/Financial
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

            // Gracefully disconnect from TS
            if (_tsConnection != null)
            {
                Console.WriteLine("[DISPOSE] Signing off from TS...");
                _tsConnection.SignOffAndDisconnectAsync().Wait();
                _tsConnection.Dispose();
            }

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

        private string GenerateAlphaNumericTRN(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var result = new char[length];
            for (int i = 0; i < length; i++)
            {
                result[i] = chars[Random.Shared.Next(chars.Length)];
            }
            return new string(result);
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
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
using core.Models.Configuration;
using core.ISO8583;
using core.Security;
using core.Helpers;
using network.Validation;
using router;
using data;
using System.Data.SqlClient;
using System.Collections.Generic;

namespace server
{
/// Multi-threaded TCP server that listens for incoming ISO-8583 messages
    
public class TcpSwitchServer : IDisposable
{
    private readonly List<TcpListener> _listeners = new();
    private bool _isRunning;
    private readonly IReadOnlyList<int> _ports;
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

    // Persistent connection managers for all issuers
    private readonly ConcurrentDictionary<string, TSConnectionManager> _issuerConnections = new();

    // Map Listening Port -> Issuer Code (for Passive Mode)
    private readonly ConcurrentDictionary<int, string> _portToIssuerCode = new();

    // Thread-safe collection to track active connections
    // ConcurrentDictionary = multiple threads can access it safely!
    private readonly ConcurrentDictionary<string, ClientSession> _activeSessions;
    private readonly NapasDataElementValidator _validator;

    // Keep single-port constructor
    public TcpSwitchServer(int port = 1111, string dbConnectionString = "", bool enableLogging = true)
        : this(new[] { port }, dbConnectionString, enableLogging)
    {
    }

    // New multi-port constructor
    public TcpSwitchServer(int[] ports, string dbConnectionString = "", bool enableLogging = true)
    {
        _ports = ports.Distinct().Where(p => p > 0).ToList();
        if (_ports.Count == 0) throw new ArgumentException("At least one valid port is required.", nameof(ports));

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

        InitializeTSConnection();

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
    /// Initialize persistent connections to all issuers
    /// </summary>
    private void InitializeTSConnection()
    {
        var allIssuers = ConfigurationLoader.Instance.GetAllIssuers();
        foreach (var issuer in allIssuers)
        {
            // Skip if host or port is missing
            if (string.IsNullOrWhiteSpace(issuer.Host) || issuer.Port <= 0)
            {
                Console.WriteLine($"[INIT] Skipping connection for {issuer.IssuerName} (No Host/Port)");
                continue;
            }

            // Passive Mode Check: If the issuer's "Port" matches one of OUR listening ports,
            // we treat it as an INBOUND connection (Passive Mode).
            if (_ports.Contains(issuer.Port))
            {
                Console.WriteLine($"[INIT] configured as PASSIVE connection for {issuer.IssuerName} on port {issuer.Port} (We Listen)");
                
                // Map the port to this issuer so HandleClient knows who it is
                _portToIssuerCode[issuer.Port] = issuer.IssuerCode;
                
                // Still create the manager, but it won't dial out. It waits for AttachClient.
                var manager = new TSConnectionManager(issuer, channelCount: 1, heartbeatIntervalMs: 30000);
                _issuerConnections[issuer.IssuerCode] = manager;
            }
            else
            {
                // Active Mode: We dial out to them
                Console.WriteLine($"[INIT] configured as ACTIVE connection for {issuer.IssuerName} at {issuer.Host}:{issuer.Port} (We Dial)");
                var manager = new TSConnectionManager(issuer, channelCount: 1, heartbeatIntervalMs: 30000);
                _issuerConnections[issuer.IssuerCode] = manager;
            }
        }
        
        if (_issuerConnections.IsEmpty)
        {
            Console.WriteLine("[INIT] No issuers configured for connections");
        }
        else
        {
            Console.WriteLine($"[INIT] {_issuerConnections.Count} connection managers initialized");
        }
    }

    /// <summary>
    /// Connect to all configured ACTIVE issuers
    /// </summary>
    public async Task ConnectToTSAsync()
    {
        Console.WriteLine("[TS-MGR] Connecting to active issuers...");
        
        // Only connect managers that are NOT in our passive map
        // (i.e., we are dialing out to them)
        var activeIssuers = _issuerConnections
            .Where(kvp => !_portToIssuerCode.Values.Contains(kvp.Key))
            .Select(kvp => kvp.Value.ConnectAllAsync());

        await Task.WhenAll(activeIssuers);
        Console.WriteLine("[TS-MGR] Active connection attempts completed");
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
            int persistentConnected = _issuerConnections.Values.Count(c => c.IsAnyConnected);
            Console.WriteLine($"[POOL] Total: {poolStats.TotalPooledConnections} | Active: {poolStats.TotalActivedConnections} | H2H: {persistentConnected}/{_issuerConnections.Count}");
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
            _isRunning = true;

            foreach (var port in _ports)
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                _listeners.Add(listener);

                _ = Task.Run(() => AcceptLoopAsync(listener, port));
            }

            Console.WriteLine("╔═══════════════════════════════════════════════════╗");
            Console.WriteLine("║       NAPAS SWITCH SERVER STARTED                 ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════╝");
            var issPorts = new List<string>();
            var acqPorts = new List<string>();

            foreach (var port in _ports)
            {
                if (_portToIssuerCode.ContainsKey(port))
                {
                    issPorts.Add(port.ToString());
                }
                else
                {
                    acqPorts.Add(port.ToString());
                }
            }

            if (issPorts.Count > 0)
                Console.WriteLine($" [ISS] Listening on port: {string.Join(", ", issPorts)}");
            
            if (acqPorts.Count > 0)
                Console.WriteLine($" [ACQ] Listening on port: {string.Join(", ", acqPorts)}");
            Console.WriteLine($" Started at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($" Configuration: {ConfigurationLoader.Instance.GetStats()}");
            Console.WriteLine("\n Waiting for client connections...\n");
        }

        private async Task AcceptLoopAsync(TcpListener listener, int port)
        {
            while (_isRunning)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync();
                    // Pass the local port so we know which interface/purpose this connection serves
                    _ = Task.Run(() => HandleClientAsync(client, port));
                    
                    // Note: Active sessions count might include both ACQ and ISS sessions now
                    // Console.WriteLine($" New connection accepted on port {port}.");
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Console.WriteLine($" Error accepting connection on port {port}: {ex.Message}");
                }
            }
        }


        /// <summary>
        /// Dispatcher: Decides if the connection is an ACQUIRER (POS/ATM) or an ISSUER (H2H)
        /// </summary>
        private async Task HandleClientAsync(TcpClient client, int localPort)
        {
            if (_portToIssuerCode.TryGetValue(localPort, out string? issuerCode))
            {
                await HandleIssuerConnectionAsync(client, issuerCode);
            }
            else
            {
                await HandleAcquirerConnectionAsync(client);
            }
        }

        /// <summary>
        /// Handle incoming connection from an Issuer (Passive H2H)
        /// </summary>
        private async Task HandleIssuerConnectionAsync(TcpClient client, string issuerCode)
        {
            string remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
            MessageLogger.LogConnectionEvent("H2H-PASSIVE", $"New connection on Port {((IPEndPoint)client.Client.LocalEndPoint).Port} -> Identified as Issuer: {issuerCode}");

            if (_issuerConnections.TryGetValue(issuerCode, out var manager))
            {
                // Hand over the TCP Client to the Persistent Connection Manager
                // It will send the 0800 Sign-On
                bool accepted = await manager.AcceptConnectionAsync(client);
                if (!accepted)
                {
                    MessageLogger.LogConnectionEvent("H2H-PASSIVE", $"Manager failed to accept connection for {issuerCode}");
                    client.Close();
                }
            }
            else
            {
                MessageLogger.LogConnectionEvent("H2H-PASSIVE", $"Critical Error: No manager found for {issuerCode}");
                client.Close();
            }
        }

        /// <summary>
        /// Handle incoming connection from an Acquirer (POS/ATM)
        /// Current logic (renamed from HandleClientAsync)
        /// </summary>
        private async Task HandleAcquirerConnectionAsync(TcpClient client)
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
                    byte[]? lengthBytes = ReadExactOrNull(stream, 4);
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
            string hexString = BitConverter.ToString(messageBytes).Replace("-", " ");
            string asciiString = new string(messageBytes.Select(b => b >= 32 && b <= 126 ? (char)b : '.').ToArray());
            MessageLogger.LogRaw(sessionId, hexString, asciiString);
        }

        /// Process an incoming ISO-8583 message
            
        
        private async Task<byte[]?> ProcessMessageAsync(byte[] messageBytes, string sessionId)
        {
            var stopwatch = Stopwatch.StartNew();
            TransactionContext? txnContext = null;
            
            try
            {
                if (LooksLikeHexAscii(messageBytes))
                    messageBytes = HexToBytesSafe(System.Text.Encoding.ASCII.GetString(messageBytes));

                IsoMessage request;
                try {
                    request = _parser.Parse(messageBytes);
                    NormalizeTrack2(request);
                } catch (Exception ex) {
                    Console.WriteLine($"[{sessionId}] Parse error: {ex.Message}");
                    return _parser.Build(CreateErrorResponse(new IsoMessage { MessageType = "0200" }, "30"));
                }

                txnContext = _stateMachine.CreateTransaction(sessionId, request);
                NormalizeLocalDateTimeFields(request);

                if (request.MessageType == "0200" || request.MessageType == "0400")
                {
                    EnsureMandatoryNapasFields(request, sessionId);
                    if (request.MessageType == "0200")
                    {
                        txnContext.TRN = GenerateAlphaNumericTRN(16);
                        request.SetTRN(txnContext.TRN);
                    }
                    else txnContext.TRN = request.GetTRN() ?? txnContext.TRN;
                }

                var validationResult = _validator.ValidateMessage(request);
                if (!validationResult.IsValid)
                {
                    txnContext.TryTransitionTo(TransactionState.Failed, validationResult.GetFirstErrorCode(), "Validation failed");
                    return _parser.Build(CreateErrorResponse(request, validationResult.GetFirstErrorCode()));
                }
                
                txnContext.TryTransitionTo(TransactionState.Routing);
                MessageLogger.LogMessage(sessionId, "ACQ recv", request);

                string? clearPan = request.GetField(2);
                string? encryptedPan = !string.IsNullOrEmpty(clearPan) ? _securityProvider.EncryptPAN(clearPan) : null;

                if (request.MessageType == "0200" || request.MessageType == "0400")
                {
                    var requestCopy = CloneWithEncryptedPan(request, encryptedPan);
                    if (_pendingStore != null) await _pendingStore.StoreRequestAsync(txnContext.TransactionId, sessionId, requestCopy, messageBytes);
                    if (_transactionLogger != null) _ = _transactionLogger.LogRequestAsync(requestCopy, sessionId, "INBOUND");
                }

                IsoMessage response = request.MessageType switch
                {
                    "0200" => await HandleAuthorizationRequestAsync(request, sessionId, txnContext),
                    "0400" => await HandleReversalRequestAsync(request, sessionId, txnContext),
                    "0420" => await HandleReversalAdviceAsync(request, sessionId, txnContext),
                    "0800" => HandleEchoRequest(request, sessionId, txnContext),
                    _ => CreateErrorResponse(request, "12")
                };

                if (!string.IsNullOrEmpty(txnContext.TRN)) response.SetTRN(txnContext.TRN);
                
                if (_pendingStore != null && (request.MessageType == "0200" || request.MessageType == "0400"))
                {
                    var correlationResult = _correlationValidator.ValidateResponseMatchesRequest(request, response);
                    if (!correlationResult.IsValid)
                    {
                        await _pendingStore.MarkAsMismatchAsync(txnContext.TransactionId, "Correlation failed");
                        txnContext.TryTransitionTo(TransactionState.Failed, "30", "Correlation failed");
                        response = CreateErrorResponse(request, "30");
                    }
                    else await _pendingStore.MarkAsMatchedAsync(txnContext.TransactionId, response.GetField(39) ?? "96");
                }

                txnContext.Response = response;
                txnContext.TryTransitionTo(TransactionState.Completed);
                MessageLogger.LogMessage(sessionId, "ACQ resp", response);

                stopwatch.Stop();
                int timeMs = (int)stopwatch.ElapsedMilliseconds;

                if (_transactionLogger != null) _ = _transactionLogger.LogTransactionAsync(request, response, sessionId, timeMs, "COMPLETE");

                if (request.MessageType != "0800" && response.MessageType != "0810")
                {
                    Console.WriteLine($" [{sessionId}] DONE | {request.MessageType}->{response.MessageType} | TRN: {response.GetTRN() ?? "N/A"} | RC: {response.GetField(39) ?? "96"} | {timeMs}ms");
                }
                return _parser.Build(response);   
            }
            catch (Exception ex)
            {
                Console.WriteLine($" [{sessionId}] Error: {ex.Message}");
                txnContext?.TryTransitionTo(TransactionState.Failed, "96", ex.Message);
                return null;
            }
        }

        private void NormalizeTrack2(IsoMessage request)
        {
            if (request.HasField(35))
            {
                string t2 = request.GetField(35)!.Trim(';', '?', ' ').Replace("F", "").Replace("f", "").Replace('=', 'D');
                if (t2.Length > 37) t2 = t2.Substring(0, 37);
                request.SetField(35, t2);
            }
        }

        private IsoMessage CloneWithEncryptedPan(IsoMessage original, string? encryptedPan)
        {
            var copy = new IsoMessage { MessageType = original.MessageType };
            foreach (var f in original.Fields) copy.SetField(f.Key, (f.Key == 2 && encryptedPan != null) ? encryptedPan : f.Value);
            return copy;
        }

     
     
        private async Task<IsoMessage> HandleAuthorizationRequestAsync(IsoMessage request, string sessionId, TransactionContext txnContext)
        {
            var issuerBank = GetIssuer(request, sessionId, txnContext);
            if (issuerBank == null) return CreateErrorResponse(request, "15");

            PrepareMessageForRouting(request, issuerBank, sessionId);
            
            txnContext.TryTransitionTo(TransactionState.Routing);
            return await RouteMessageAsync(request, issuerBank, sessionId, txnContext);
        }

        private IssuerBankConfig? GetIssuer(IsoMessage request, string sessionId, TransactionContext txnContext)
        {
            string? cardBIN = request.GetCardBIN();
            if (string.IsNullOrEmpty(cardBIN))
            {
                Console.WriteLine($"  [{sessionId}] No card BIN found");
                txnContext.TryTransitionTo(TransactionState.Failed, "14", "Invalid card BIN");
                return null;
            }

            var issuerBank = ConfigurationLoader.Instance.GetIssuerByBIN(cardBIN);
            if (issuerBank == null)
            {
                Console.WriteLine($"  [{sessionId}] No issuer found for BIN: {cardBIN}");
                txnContext.TryTransitionTo(TransactionState.Failed, "15", "No such issuer");
            }
            return issuerBank;
        }

        private void PrepareMessageForRouting(IsoMessage request, IssuerBankConfig issuerBank, string sessionId)
        {
            // DE#32 (Acquirer ID) & DE#33 (Forwarding ID) management
            string switchId = issuerBank.IssuerCode; 
            string defaultAcquirer = "970418"; 

            string currentDe32 = request.GetField(32) ?? string.Empty;
            if (string.IsNullOrEmpty(currentDe32) || (currentDe32 == switchId && issuerBank.IsDefault))
            {
                request.SetField(32, defaultAcquirer);
                Console.WriteLine($" [{sessionId}] DE#32 normalized to: {defaultAcquirer}");
            }

            if (issuerBank.IsDefault)
            {
                request.SetField(33, switchId);
            }
            else
            {
                request.SetIssuerID(issuerBank.IssuerCode);
            }

            if (request.MessageType == "0400" || request.MessageType == "0420")
            {
                core.Helpers.SettlementHelper.AddSettlementAmount(request);
            }
        }

        private async Task<IsoMessage> RouteMessageAsync(IsoMessage request, IssuerBankConfig issuerBank, string sessionId, TransactionContext txnContext)
        {
            IsoMessage? response = null;
            try 
            {
                // Try persistent connection manager first
                if (_issuerConnections.TryGetValue(issuerBank.IssuerCode, out var persistentManager) && persistentManager.IsAnyConnected)
                {
                    response = await persistentManager.ForwardTransactionAsync(request, sessionId);
                }
                else
                {
                    // Check if this is a PASSIVE issuer (we listen for them)
                    // If so, we CANNOT dial out to them (using fallback pool), because their Host/Port is actually OURS.
                    bool isPassive = _portToIssuerCode.Values.Contains(issuerBank.IssuerCode);

                    if (isPassive)
                    {
                         Console.WriteLine($" [{sessionId}] [ROUTING] Passive Issuer {issuerBank.IssuerName} is NOT connected. Cannot dial out.");
                         // Fail immediately
                         response = null;
                    }
                    else
                    {
                        // Active Mode: We can try the fallback pool to dial out
                        if (!issuerBank.IsDefault)
                            Console.WriteLine($" [{sessionId}] [ROUTING] No active persistent connection for {issuerBank.IssuerName}, falling back to pool");
                        
                        response = await _issuerConnector.ForwardToIssuerAsync(request, issuerBank, sessionId);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" [{sessionId}] Routing error: {ex.Message}");
            }

            if (response == null)
            {
                txnContext.TryTransitionTo(TransactionState.Failed, "91", "Issuer unavailable");
                return CreateErrorResponse(request, "91");
            }

            return response;
        }

        
        /// Handle 0400 - Reversal Request (Void/Cancel)
        
        private async Task<IsoMessage> HandleReversalAdviceAsync(IsoMessage request, string sessionId, TransactionContext txnContext)
        {
            Console.WriteLine($" [{sessionId}] Processing reversal advice (0420)");
            
            if (_pendingStore != null)
            {
                string? trn = request.GetTRN();
                if (!string.IsNullOrEmpty(trn))
                {
                    var original = await _pendingStore.GetRequestByTRNAsync(trn);
                    if (original != null && !request.HasField(90))
                    {
                        request.SetField(90, IsoMessage.BuildDE90(
                            original.MessageType, original.RequestSTAN, original.RequestDateTime,
                            original.RequestAcquirerID, null));
                        Console.WriteLine($"  [{sessionId}] DE#90 built from original: {request.GetField(90)}");
                    }
                    else if (original == null)
                    {
                        Console.WriteLine($"  [{sessionId}] [BG-VERIFY] Original NOT found for TRN: {trn}");
                    }
                }
            }
            
            var response = CreateSuccessResponse(request);
            response.MessageType = "0430";
            
            _ = Task.Run(async () => 
            {
                try 
                {
                    var issuerBank = GetIssuer(request, sessionId, txnContext);
                    if (issuerBank == null) return;

                    PrepareMessageForRouting(request, issuerBank, sessionId);
                    await RouteMessageAsync(request, issuerBank, sessionId, txnContext);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" [{sessionId}] [BG-FWD] Error: {ex.Message}");
                }
            });

            txnContext.TryTransitionTo(TransactionState.Reversing);
            return response;
        }

        private async Task<IsoMessage> HandleReversalRequestAsync(IsoMessage request, string sessionId, TransactionContext txnContext)
        {
            Console.WriteLine($" [{sessionId}] Processing reversal request");
            
            if (_pendingStore != null)
            {
                string? trn = request.GetTRN();
                if (!string.IsNullOrEmpty(trn))
                {
                    var original = await _pendingStore.GetRequestByTRNAsync(trn);
                    if (original == null)
                    {
                        Console.WriteLine($"  [{sessionId}] Original transaction not found for TRN: {trn}");
                        txnContext.TryTransitionTo(TransactionState.Reversed, "25", "Original transaction not found");
                        return CreateErrorResponse(request, "25");
                    }
                    Console.WriteLine($"  [{sessionId}] Original transaction found and verified via TRN: {trn}");

                    // Build DE#90 from original if not already present
                    if (!request.HasField(90))
                    {
                        request.SetField(90, IsoMessage.BuildDE90(
                            original.MessageType, original.RequestSTAN, original.RequestDateTime,
                            original.RequestAcquirerID, null));
                        Console.WriteLine($"  [{sessionId}] DE#90 built from original: {request.GetField(90)}");
                    }
                }
            }

            var issuerBank = GetIssuer(request, sessionId, txnContext);
            if (issuerBank == null) return CreateErrorResponse(request, "15");

            PrepareMessageForRouting(request, issuerBank, sessionId);
            
            txnContext.TryTransitionTo(TransactionState.Reversing);
            var response = await RouteMessageAsync(request, issuerBank, sessionId, txnContext);

            if (response.GetField(39) == "00")
                txnContext.TryTransitionTo(TransactionState.Reversed);
            else
                txnContext.TryTransitionTo(TransactionState.Reversed);

            return response;
        }

        
        // Network Management (0800) - Echo Test
        private IsoMessage HandleEchoRequest(IsoMessage request, string sessionId, TransactionContext txnContext)
        {
            // Console.WriteLine($" [{sessionId}] Processing Echo Request (0800)");
            
            var response = CreateSuccessResponse(request);
            response.MessageType = "0810";
            
            // Explicitly set RC 00
            response.SetField(39, "00");
            
            // Echo back Network Management Info Code (DE70) if present
            if (request.HasField(70)) response.SetField(70, request.GetField(70));

            txnContext.TryTransitionTo(TransactionState.Completed);
            return response;
        }

        /// <summary>
        /// Create a successful response (Dynamic MTI)
        
        private IsoMessage CreateSuccessResponse(IsoMessage request, bool isReversal = false)
        {
            var response = new IsoMessage
            {
                MessageType = GetResponseMTI(request.MessageType)
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

        
        /// Create an error response with specific response code AND dynamic MTI
        
        private IsoMessage CreateErrorResponse(IsoMessage request, string responseCode)
        {
            var response = new IsoMessage
            {
                MessageType = GetResponseMTI(request.MessageType)
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

        private string GetResponseMTI(string requestMti)
        {
            // Simple logic: Request MTI + 10 (e.g. 0200->0210, 0420->0430)
            if (int.TryParse(requestMti, out int mtiVal))
            {
                return (mtiVal + 10).ToString("D4");
            }
            return "0210"; // Fallback
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
            
            // DE#15 Settlement Date (MMDD) - Always set by switch per NAPAS spec
            // This OVERWRITES any value from member institutions
            msg.SetField(15, nowVn.ToString("MMdd"));
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
            foreach (var listener in _listeners)
            {       
                try { listener.Stop(); } catch { }
            }
            Console.WriteLine(" Server stopped");
        }

        
        /// <summary>
        /// Print all active connections and their statuses to the console
        /// </summary>
        public void PrintActiveConnections()
        {
            Console.WriteLine();
            Console.WriteLine($"--- Connection Status ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ---");
            Console.WriteLine();

            // --- Acquirer (Client) Sessions ---
            Console.WriteLine("  ACQUIRER SESSIONS (POS/ATM)");
            Console.WriteLine("  -----------------------------------------------");

            var sessions = _activeSessions.Values.ToList();
            if (sessions.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  (none)");
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine("  {0,-10} {1,-22} {2,-10} {3,-10} {4,-5}",
                    "Session", "Endpoint", "Connected", "Last Act.", "Msgs");
                foreach (var s in sessions)
                {
                    string connected = s.ConnectedAt.ToString("HH:mm:ss");
                    string lastAct = s.LastActivity == default ? "-" : s.LastActivity.ToString("HH:mm:ss");
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("  {0,-10}", s.SessionId);
                    Console.ResetColor();
                    Console.WriteLine(" {0,-22} {1,-10} {2,-10} {3,-5}",
                        Truncate(s.ClientEndpoint, 20), connected, lastAct, s.MessageCount);
                }
            }

            Console.WriteLine();

            // --- Issuer H2H Connections ---
            Console.WriteLine("  ISSUER H2H CONNECTIONS");
            Console.WriteLine("  -----------------------------------------------");

            if (_issuerConnections.IsEmpty)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  (none)");
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine("  {0,-14} {1,-8} {2,-22} {3,-10} {4,-10}",
                    "Issuer", "Code", "Host", "Channel", "Status");
                foreach (var kvp in _issuerConnections)
                {
                    var manager = kvp.Value;
                    var channelStatuses = manager.GetChannelStatuses();
                    bool isPassive = _portToIssuerCode.Values.Contains(kvp.Key);
                    string mode = isPassive ? "PSV" : "ACT";

                    foreach (var ch in channelStatuses)
                    {
                        string hostPort = $"{ch.Host}:{ch.Port}";
                        Console.Write("  {0,-14} {1,-8} {2,-22} {3,-10} ",
                            Truncate(ch.IssuerName, 14), ch.IssuerCode, Truncate(hostPort, 20), $"CH{ch.ChannelIndex}({mode})");

                        if (ch.IsConnected)
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("CONNECTED");
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine("DISCONNECTED");
                        }
                        Console.ResetColor();
                    }
                }
            }

            Console.WriteLine();

            // --- Connection Pool Stats ---
            Console.WriteLine("  CONNECTION POOL");
            Console.WriteLine("  -----------------------------------------------");
            var poolStats = _issuerConnector.GetPoolStats();
            Console.WriteLine($"  Pooled: {poolStats.TotalPooledConnections}  |  Active: {poolStats.TotalActivedConnections}  |  Pools: {poolStats.PoolCount}");

            Console.WriteLine();
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= maxLength ? value : value[..(maxLength - 2)] + "..";
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

            // Gracefully disconnect from all persistent issuer connections
            foreach (var manager in _issuerConnections.Values)
            {
                try
                {
                    manager.DisconnectAll();
                    manager.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DISPOSE] Error cleaning up manager: {ex.Message}");
                }
            }
            _issuerConnections.Clear();

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
}
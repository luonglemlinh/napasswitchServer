using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
using Microsoft.Data.SqlClient;
using System.Collections.Generic;

namespace server
{
/// Multi-threaded TCP server that listens for incoming ISO-8583 messages

public class TcpSwitchServer : IDisposable
{
    private readonly List<TcpListener> _listeners = new();
    private volatile bool _isRunning;
    private readonly IReadOnlyList<int> _ports;
    private readonly DateTime _serverStartTime;
    private readonly IsoParser _parser;
    private readonly IssuerConnector _issuerConnector;
    private readonly TransactionLogger? _transactionLogger;
    private readonly TransactionStateMachine _stateMachine;
    private readonly SecureDataHandler _securityProvider;
    private readonly MessageCycleStore? _messageCycleStore;
    private readonly ResponseCorrelationValidator _correlationValidator;
    private readonly IConfigurationLoader _config;
    private readonly MessageFramer _framer = new(MessageFramer.LengthHeaderFormat.Ascii4Byte);
    private readonly HealthCheckServer _healthCheck;
    private readonly SafRetryQueue? _safQueue;
    private bool _disposed;
    private Timer? _statsTimer;
    private Timer? _poolHealthTimer;
    private CancellationTokenSource? _serverCts;

    // Persistent connection managers for all issuers
    private readonly ConcurrentDictionary<string, TSConnectionManager> _issuerConnections = new();

    // Map Listening Port -> Issuer Code (for Passive Mode)
    private readonly ConcurrentDictionary<int, string> _portToIssuerCode = new();

    // Thread-safe collection to track active connections
    // ConcurrentDictionary = multiple threads can access it safely!
    private readonly ConcurrentDictionary<string, ClientSession> _activeSessions;
    private readonly NapasDataElementValidator _validator;

    // Keep single-port constructor
    public TcpSwitchServer(int port = 1111, string dbConnectionString = "", bool enableLogging = true, IHsmProvider? hsmProvider = null, IConfigurationLoader? config = null)
        : this(new[] { port }, dbConnectionString, enableLogging, hsmProvider, config)
    {
    }

    // New multi-port constructor
    public TcpSwitchServer(int[] ports, string dbConnectionString = "", bool enableLogging = true, IHsmProvider? hsmProvider = null, IConfigurationLoader? config = null)
    {
        _config = config ?? ConfigurationLoader.Instance;
        _serverStartTime = DateTime.UtcNow;
        _ports = ports.Distinct().Where(p => p > 0).ToList();
        if (_ports.Count == 0) throw new ArgumentException("At least one valid port is required.", nameof(ports));

        _parser = new IsoParser();
        _issuerConnector = new IssuerConnector();
        _activeSessions = new ConcurrentDictionary<string, ClientSession>();
        _stateMachine = new TransactionStateMachine(transactionTimeoutSeconds: 30);

        // Use injected HSM provider or fail hard — no silent stub activation
        if (hsmProvider == null)
        {
            string? allowStub = Environment.GetEnvironmentVariable("ALLOW_HSM_STUB");
            if (string.Equals(allowStub, "true", StringComparison.OrdinalIgnoreCase))
            {
                SwitchLogger.ForContext("SECURITY").Warn("ALLOW_HSM_STUB is set — using SoftwareHsmStub. NOT FOR PRODUCTION!");
                hsmProvider = new SoftwareHsmStub();
            }
            else
            {
                throw new InvalidOperationException(
                    "No IHsmProvider injected and ALLOW_HSM_STUB is not set. " +
                    "Inject a real HSM provider (Thales payShield, Futurex, AWS CloudHSM) " +
                    "or set ALLOW_HSM_STUB=true for development only.");
            }
        }
        _securityProvider = new SecureDataHandler(hsmProvider);
        _correlationValidator = new ResponseCorrelationValidator();
        _healthCheck = new HealthCheckServer(
            () => _isRunning, () => GetStats(), () => _stateMachine.GetStats(),
            _issuerConnections, _serverStartTime, _config);

        _stateMachine.OnTransactionTimeout += OnTransactionTimeout;
        _stateMachine.OnReversalRequired += OnReversalRequired;

        string configPath = FindValidationConfigPath();
        _validator = new NapasDataElementValidator(configPath);
        SwitchLogger.ForContext("INIT").Info("Validation config loaded: {ConfigFile}", Path.GetFileName(configPath));

        InitializeTSConnection();

        if (enableLogging && !string.IsNullOrEmpty(dbConnectionString))
        {
            _transactionLogger = new TransactionLogger(dbConnectionString, enableLogging, _securityProvider);
            _messageCycleStore = new MessageCycleStore(dbConnectionString, enableLogging);
            _safQueue = new SafRetryQueue(async entry => await RetrySafAdviceAsync(entry));
            SwitchLogger.ForContext("INIT").Info("Transaction logger, message cycle store, and SAF queue initialized");
        }
        else
        {
            _transactionLogger = null;
            _messageCycleStore = null;
            SwitchLogger.ForContext("INIT").Info("Transaction logger disabled");
        }

        StartBackgroundMonitoring();
    }

    /// <summary>
    /// Initialize persistent connections to all issuers
    /// </summary>
    private void InitializeTSConnection()
    {
        var allIssuers = _config.GetAllIssuers();
        var passive = new List<string>();
        var active = new List<string>();
        var skipped = new List<string>();

        foreach (var issuer in allIssuers)
        {
            // Skip if host or port is missing
            if (string.IsNullOrWhiteSpace(issuer.Host) || issuer.Port <= 0)
            {
                skipped.Add(issuer.IssuerName);
                continue;
            }

            // Passive Mode Check: If the issuer's "Port" matches one of OUR listening ports,
            // we treat it as an INBOUND connection (Passive Mode).
            if (_ports.Contains(issuer.Port))
            {
                passive.Add($"{issuer.IssuerName}:{issuer.Port}");

                // Map the port to this issuer so HandleClient knows who it is
                _portToIssuerCode[issuer.Port] = issuer.IssuerCode;

                // Still create the manager, but it won't dial out. It waits for AttachClient.
                var manager = new TSConnectionManager(issuer, channelCount: 1, heartbeatIntervalMs: 30000);
                manager.OnConnectionChanged += LogH2HSummary;
                _issuerConnections[issuer.IssuerCode] = manager;
            }
            else
            {
                // Active Mode: We dial out to them
                active.Add($"{issuer.IssuerName}->{issuer.Host}:{issuer.Port}");
                var manager = new TSConnectionManager(issuer, channelCount: 1, heartbeatIntervalMs: 30000);
                manager.OnConnectionChanged += LogH2HSummary;
                _issuerConnections[issuer.IssuerCode] = manager;
            }
        }

        SwitchLogger.ForContext("INIT").Info("Issuer connections: {Count} ready (passive={Passive}, active={Active}, skipped={Skipped})", _issuerConnections.Count, passive.Count, active.Count, skipped.Count);
        if (passive.Count > 0) SwitchLogger.ForContext("INIT").Debug("Passive: {PassiveList}", string.Join(", ", passive));
        if (active.Count > 0)  SwitchLogger.ForContext("INIT").Debug("Active: {ActiveList}", string.Join(", ", active));
        if (skipped.Count > 0) SwitchLogger.ForContext("INIT").Debug("Skipped: {SkippedList}", string.Join(", ", skipped));
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
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "NapasFieldsConfig.xml"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Config", "NapasFieldsConfig.xml"),
            };
            
            foreach (var path in searchPaths)
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            
            throw new FileNotFoundException(
                "NAPAS validation configuration file not found. Searched paths:\n" + 
                string.Join("\n", searchPaths.Select(p => $"  - {Path.GetFullPath(p)}")));
        }
        
        private void StartBackgroundMonitoring()
        {
            // Disabled timer-based stats - now logs only on connection events
            // _statsTimer = new Timer(LogServerStats, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            // _poolHealthTimer = new Timer(LogPoolHealth, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            _healthCheck.Start();
        }

        private void LogServerStats(object? state)
        {
            var stats = GetStats();
            SwitchLogger.ForContext("STATS").Debug("Active: {ActiveConnections} | Total Msgs: {TotalMessages}", stats.ActiveConnections, stats.TotalMessageCount);
        }

        private void LogPoolHealth(object? state)
        {
            var poolStats = _issuerConnector.GetPoolStats();
            int persistentConnected = _issuerConnections.Values.Count(c => c.IsAnyConnected);
            SwitchLogger.ForContext("POOL").Debug("Total: {Pooled} | Active: {Active} | H2H: {Connected}/{Total}", poolStats.TotalPooledConnections, poolStats.TotalActivedConnections, persistentConnected, _issuerConnections.Count);
        }
        
        private void OnTransactionTimeout(TransactionContext context)
        {
            SwitchLogger.ForContext("TIMEOUT").Warn("Transaction {TransactionId} timed out after {ElapsedMs}ms", context.TransactionId, context.GetProcessingTime()?.TotalMilliseconds ?? 0);
        }

        private void OnReversalRequired(TransactionContext context)
        {
            SwitchLogger.ForContext("REVERSAL").Warn("Auto-reversal required for transaction {TransactionId}", context.TransactionId);
        }

        /// <summary>
        /// Print a compact one-line H2H status summary. Called on every connect/disconnect event.
        /// </summary>
        private void LogH2HSummary()
        {
            var parts = _issuerConnections.Values
                .Select(m => $"{m.TSName}:{(m.IsAnyConnected ? "OK" : "--")}")
                .ToList();
            int connected = _issuerConnections.Values.Count(m => m.IsAnyConnected);
            SwitchLogger.ForContext("H2H").Info("{StatusLine}  ({Connected}/{Total} connected)", string.Join(" | ", parts), connected, _issuerConnections.Count);
        }

        
        /// Start the server and begin accepting connections
        
        public void Start()
        {
            _isRunning = true;
            _serverCts = new CancellationTokenSource();

            foreach (var port in _ports)
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                _listeners.Add(listener);

                _ = Task.Run(() => AcceptLoopAsync(listener, port));
            }

            Console.WriteLine("╔═══════════════════════════════════════════════════╗");
            Console.WriteLine("║                SWITCH SERVER STARTED              ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════╝");
            
            // Get port configuration from ServerConfig
            var serverConfig = _config.ServerConfig;
            var issPorts = serverConfig.IssuerPorts.Select(p => p.ToString()).ToList();
            var acqPorts = serverConfig.AcquirerPorts.Select(p => p.ToString()).ToList();

            if (issPorts.Count > 0)
                Console.WriteLine($" [ISS] Listening on port: {string.Join(", ", issPorts)}");

            if (acqPorts.Count > 0)
                Console.WriteLine($" [ACQ] Listening on port: {string.Join(", ", acqPorts)}");
            Console.WriteLine($" Started at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($" Configuration: {_config.GetStats()}");
            Console.WriteLine("\n Waiting for client connections...\n");
        }

        private async Task AcceptLoopAsync(TcpListener listener, int port)
        {
            while (_isRunning)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        try { await HandleClientAsync(client, port); }
                        catch (Exception ex) { SwitchLogger.ForContext("SERVER").Error("Unhandled exception on port {Port}: {Error}", port, ex.Message); }
                    });
                    
                    // Note: Active sessions count might include both ACQ and ISS sessions now
                    // Console.WriteLine($" New connection accepted on port {port}.");
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                        SwitchLogger.Error("Error accepting connection on port {Port}: {Error}", port, ex.Message);
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
            bool hasManager = _issuerConnections.TryGetValue(issuerCode, out var manager);
            string issuerLabel = hasManager ? manager!.TSName : issuerCode;

            SwitchLogger.ForContext("H2H").Debug("Passive incoming from {RemoteEp} -> Issuer: {Issuer}", remoteEp, issuerLabel);
            MessageLogger.LogConnectionEvent("H2H-PASSIVE", $"New connection on Port {((IPEndPoint)client.Client.LocalEndPoint).Port} -> Identified as Issuer: {issuerLabel}");

            if (hasManager)
            {
                // Hand over the TCP Client to the Persistent Connection Manager
                // It will send the 0800 Sign-On
                bool accepted = await manager!.AcceptConnectionAsync(client);
                if (!accepted)
                {
                    SwitchLogger.ForContext("H2H").Warn("Passive ISS connection FAILED for {Issuer} from {RemoteEp} (sign-on rejected)", issuerLabel, remoteEp);
                    MessageLogger.LogConnectionEvent("H2H-PASSIVE", $"Manager failed to accept connection for {issuerLabel}");
                    client.Close();
                }
            }
            else
            {
                SwitchLogger.ForContext("H2H").Error("No manager found for ISS {Issuer} - connection rejected", issuerLabel);
                MessageLogger.LogConnectionEvent("H2H-PASSIVE", $"Critical Error: No manager found for {issuerLabel}");
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

                SwitchLogger.Debug($"[{sessionId}] ACQ connected from {clientEndpoint}");

                // Set timeouts (30 seconds)
                stream.ReadTimeout = 30000;
                stream.WriteTimeout = 30000;

                // Message processing loop — single wire format, no guessing
                while (client.Connected && _isRunning)
                {
                    byte[]? messageBytes = _framer.ReadMessage(stream, sessionId);
                    if (messageBytes == null) break;

                    PrintRawMessage(messageBytes, sessionId);

                    byte[]? responseBytes = await ProcessMessageAsync(messageBytes, sessionId, _serverCts?.Token ?? CancellationToken.None);

                    if (responseBytes != null && responseBytes.Length > 0)
                    {
                        // ACQ Response (OUTBOUND) is usually the last leg.
                        // However, per user request, we buffer legs and flush on completion.
                        // For messages processed here (like responses), we ensure they are logged.
                        // But since ProcessMessageAsync handles the main flow, we'll let it handle flushing.
                        
                        _framer.WriteMessage(stream, responseBytes, sessionId);
                    }

                    session.MessageCount++;
                    session.LastActivity = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                SwitchLogger.Error($" [{sessionId}] Error: {ex.Message}");
            }
            finally
            {
                // Cleanup
                stream?.Close();
                client?.Close();
                _activeSessions.TryRemove(sessionId, out var ended);
                int msgCount = ended?.MessageCount ?? 0;

                if (msgCount > 0)
                    SwitchLogger.Info($"[{sessionId}] ACQ disconnected | msgs={msgCount} | active={_activeSessions.Count}");
                else
                    SwitchLogger.Debug($"[{sessionId}] ACQ disconnected (no messages) | active={_activeSessions.Count}");
            }
        }

        private static byte[]? ReadExactOrNull(NetworkStream stream, int length)
        {
            return NetworkStreamHelper.ReadExactOrNull(stream, length);
        }

        private static void PrintRawMessage(byte[] messageBytes, string sessionId)
        {
            string hexString = BitConverter.ToString(messageBytes).Replace("-", " ");
            string asciiString = new string(messageBytes.Select(b => b >= 32 && b <= 126 ? (char)b : '.').ToArray());
            MessageLogger.LogRaw(sessionId, hexString, asciiString);
        }

        /// Process an incoming ISO-8583 message
            
        
        private async Task<byte[]?> ProcessMessageAsync(byte[] messageBytes, string sessionId, CancellationToken cancellationToken = default)
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
                    SwitchLogger.Warn("[{SessionId}] Parse error: {Error}", sessionId, ex.Message);
                    ServerMetrics.IncrementParseError();
                    return _parser.Build(IsoResponseBuilder.CreateErrorResponse(new IsoMessage { MessageType = MtiHelper.AuthorizationRequest }, "30"));
                }

                txnContext = _stateMachine.CreateTransaction(sessionId, request);
                NormalizeLocalDateTimeFields(request);

                if (MtiHelper.IsFinancialRequest(request.MessageType))
                {
                    EnsureMandatoryNapasFields(request, sessionId);
                    if (request.MessageType == MtiHelper.AuthorizationRequest)
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
                    return _parser.Build(IsoResponseBuilder.CreateErrorResponse(request, validationResult.GetFirstErrorCode()));
                }
                
                txnContext.TryTransitionTo(TransactionState.Routing);
                MessageLogger.LogMessage(sessionId, "ACQ recv", request);

                string? clearPan = request.GetField(2);
                string? encryptedPan = !string.IsNullOrEmpty(clearPan) ? _securityProvider.EncryptPAN(clearPan) : null;

                string? curCode = request.GetField(49);
                string? posMode = request.GetField(22);
                string? settlementDate = request.GetField(15);

                if (MtiHelper.IsFinancialRequest(request.MessageType))
                {
                    if (!MtiHelper.IsAdvice(request.MessageType) && _transactionLogger != null)
                    {
                        string? stan = request.GetSTAN();
                        string? acqId = request.GetAcquirerID();
                        string? txnDate = request.GetField(7);
                        if (!string.IsNullOrEmpty(stan) && await _transactionLogger.IsDuplicateAsync(stan, acqId, txnDate))
                        {
                            SwitchLogger.Warn("[{SessionId}] DUPLICATE detected: STAN={STAN} AcqID={AcqID} Date={Date}", sessionId, stan ?? "N/A", acqId ?? "N/A", txnDate ?? "N/A");
                            txnContext.TryTransitionTo(TransactionState.Failed, "94", "Duplicate transaction");
                            return _parser.Build(IsoResponseBuilder.CreateErrorResponse(request, "94"));
                        }
                    }

                    txnContext.RequestBytes = messageBytes;
                    var requestCopy = CloneWithEncryptedPan(request, encryptedPan);
                    if (_transactionLogger != null) 
                        _ = _transactionLogger.LogRequestAsync(requestCopy, messageBytes, txnContext.TransactionId, sessionId, 5, curCode, posMode, settlementDate);
                }

                IsoMessage response = request.MessageType switch
                {
                    MtiHelper.AuthorizationRequest => await HandleAuthorizationRequestAsync(request, sessionId, txnContext, cancellationToken),
                    MtiHelper.ReversalRequest => await HandleReversalRequestAsync(request, sessionId, txnContext, cancellationToken),
                    MtiHelper.ReversalAdvice => await HandleReversalAdviceAsync(request, sessionId, txnContext, cancellationToken),
                    MtiHelper.NetworkManagementRequest => HandleEchoRequest(request, sessionId, txnContext),
                    _ => IsoResponseBuilder.CreateErrorResponse(request, "12")
                };

                // Metrics and TRN logic omitted for brevity in chunk but should be preserved
                switch (request.MessageType)
                {
                    case MtiHelper.AuthorizationRequest: ServerMetrics.IncrementAuthorization(); break;
                    case MtiHelper.ReversalRequest: ServerMetrics.IncrementReversal(); break;
                    case MtiHelper.ReversalAdvice: ServerMetrics.IncrementReversalAdvice(); break;
                    case MtiHelper.NetworkManagementRequest: ServerMetrics.IncrementEcho(); break;
                }

                if (!string.IsNullOrEmpty(txnContext.TRN)) response.SetTRN(txnContext.TRN);
                
                if (MtiHelper.IsFinancialRequest(request.MessageType) && !MtiHelper.IsAdvice(request.MessageType))
                {
                    var correlationResult = _correlationValidator.ValidateResponseMatchesRequest(request, response);
                    if (!correlationResult.IsValid)
                    {
                        if (_transactionLogger != null) await _transactionLogger.UpdateStatusAsync(txnContext.TransactionId, "MISMATCH", "Correlation failed");
                        txnContext.TryTransitionTo(TransactionState.Failed, "30", "Correlation failed");
                        response = IsoResponseBuilder.CreateErrorResponse(request, "30");
                    }
                    else if (_transactionLogger != null) await _transactionLogger.LogTransactionAsync(request, response, txnContext.TransactionId, sessionId, curCode, posMode, settlementDate);
                }
                else if (MtiHelper.IsAdvice(request.MessageType))
                {
                    if (_transactionLogger != null) await _transactionLogger.UpdateStatusAsync(txnContext.TransactionId, "MATCHED", null, "00");
                }

                txnContext.Response = response;
                txnContext.TryTransitionTo(TransactionState.Completed);
                MessageLogger.LogMessage(sessionId, "ACQ resp", response);

                stopwatch.Stop();
                int timeMs = (int)stopwatch.ElapsedMilliseconds;

                if (_transactionLogger != null && txnContext != null) _ = _transactionLogger.LogTransactionAsync(request, response, txnContext.TransactionId, sessionId, curCode, posMode, settlementDate);

                // 11.2: Record completion metrics
                string? rc = response.GetField(39);
                ServerMetrics.RecordResponseCode(rc);
                ServerMetrics.RecordTransaction();
                if (rc == "00") ServerMetrics.IncrementSuccess();
                else ServerMetrics.IncrementFailed();

                if (!MtiHelper.IsNetworkManagement(request.MessageType) && !MtiHelper.IsNetworkManagement(response.MessageType))
                {
                    SwitchLogger.Info("[{SessionId}] DONE | {ReqMTI}->{ResMTI} | TRN: {TRN} | RC: {RC} | {TimeMs}ms",
                        sessionId, request.MessageType, response.MessageType, response.GetTRN() ?? "N/A", rc ?? "96", timeMs);
                }
                
                byte[] responseBytes = _parser.Build(response);
                if (txnContext != null)
                {
                    BufferLog(txnContext, "OUTBOUND", response.GetAcquirerID(), response.GetIssuerID(), response, responseBytes);
                    FlushBufferedLogs(txnContext);
                }
                return responseBytes;
            }
            catch (Exception ex)
            {
                SwitchLogger.Error($" [{sessionId}] Error: {ex.Message}");
                txnContext?.TryTransitionTo(TransactionState.Failed, "96", ex.Message);
                if (txnContext != null) FlushBufferedLogs(txnContext);
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

        private static IsoMessage CloneMessage(IsoMessage original)
        {
            var copy = new IsoMessage
            {
                MessageType = original.MessageType,
                Header = original.Header,
                PrimaryBitmap = original.PrimaryBitmap,
                SecondaryBitmap = original.SecondaryBitmap
            };
            foreach (var f in original.Fields) copy.SetField(f.Key, f.Value);
            return copy;
        }

     
     
        private async Task<IsoMessage> HandleAuthorizationRequestAsync(IsoMessage request, string sessionId, TransactionContext txnContext, CancellationToken cancellationToken = default)
        {
            var issuerBank = GetIssuer(request, sessionId, txnContext);
            if (issuerBank == null) return IsoResponseBuilder.CreateErrorResponse(request, "15");

            PrepareMessageForRouting(request, issuerBank, sessionId);

            txnContext.TryTransitionTo(TransactionState.Routing);

            BufferLog(txnContext, "FORWARDED", request.GetAcquirerID(), request.GetIssuerID(), request, _parser.Build(request));

            var response = await ForwardPinTransactionAsync(request, issuerBank, sessionId, txnContext, cancellationToken);

            BufferLog(txnContext, "RECEIVED", response.GetAcquirerID(), response.GetIssuerID(), response, _parser.Build(response));

            return response;
        }

        private IssuerBankConfig? GetIssuer(IsoMessage request, string sessionId, TransactionContext txnContext)
        {
            string? cardBIN = request.GetCardBIN();
            if (string.IsNullOrEmpty(cardBIN))
            {
                SwitchLogger.Debug($"  [{sessionId}] No card BIN found");
                txnContext.TryTransitionTo(TransactionState.Failed, "14", "Invalid card BIN");
                return null;
            }

            var issuerBank = _config.GetIssuerByBIN(cardBIN);
            if (issuerBank == null)
            {
                SwitchLogger.Debug($"  [{sessionId}] No issuer found for BIN: {cardBIN}");
                txnContext.TryTransitionTo(TransactionState.Failed, "15", "No such issuer");
            }
            return issuerBank;
        }

        private void PrepareMessageForRouting(IsoMessage request, IssuerBankConfig issuerBank, string sessionId)
        {
            // DE#32 (Acquirer ID) & DE#33 (Forwarding ID) management
            string switchId = issuerBank.IssuerCode; 
            string defaultAcquirer = _config.ServerConfig.Defaults.DefaultAcquirerId;

            string currentDe32 = request.GetField(32) ?? string.Empty;
            if (string.IsNullOrEmpty(currentDe32) || (currentDe32 == switchId && issuerBank.IsDefault))
            {
                request.SetField(32, defaultAcquirer);
                SwitchLogger.Info($" [{sessionId}] DE#32 normalized to: {defaultAcquirer}");
            }

            if (issuerBank.IsDefault)
            {
                request.SetField(33, switchId);
            }
            else
            {
                request.SetIssuerID(issuerBank.IssuerCode);
            }

            if (MtiHelper.IsReversal(request.MessageType))
            {
                core.Helpers.SettlementHelper.AddSettlementAmount(request);
            }
        }

        /// <summary>
        /// Translate PIN block (DE#52) from ACQ zone key to ISS zone key.
        /// Without this, PIN transactions are declined with RC 55 by the issuer
        /// because the PIN block is encrypted under the wrong key.
        /// </summary>
        private void TranslatePinBlockForIssuer(IsoMessage request, IssuerBankConfig issuerBank, string sessionId)
        {
            if (!request.HasField(52)) return;

            try
            {
                string pinBlockHex = request.GetField(52)!;
                byte[] pinBlockBytes = Convert.FromHexString(pinBlockHex);

                string acqCode = request.GetField(32) ?? "DEFAULT";
                byte[] translated = _securityProvider.TranslatePinBlock(pinBlockBytes, acqCode, issuerBank.IssuerCode);

                request.SetField(52, Convert.ToHexString(translated));
                SwitchLogger.Debug($"  [{sessionId}] DE#52 PIN block translated: ACQ({acqCode}) -> ISS({issuerBank.IssuerCode})");
            }
            catch (Exception ex)
            {
                SwitchLogger.Warn($"  [{sessionId}] PIN block translation failed: {ex.Message} — passing through raw (ISS may decline with RC 55)");
            }
        }

        /// <summary>
        /// Translate PIN block (DE#52) in ISS response back to ACQ zone key.
        /// The issuer responds with a PIN block encrypted under its own key;
        /// the acquirer expects encryption under its key.
        /// </summary>
        private void TranslatePinBlockForAcquirer(IsoMessage response, IsoMessage originalRequest, string sessionId)
        {
            if (!response.HasField(52)) return;

            try
            {
                string pinBlockHex = response.GetField(52)!;
                byte[] pinBlockBytes = Convert.FromHexString(pinBlockHex);

                // Reverse direction: ISS key -> ACQ key
                string acqCode = originalRequest.GetField(32) ?? "DEFAULT";
                string issCode = originalRequest.GetIssuerID() ?? originalRequest.GetField(33) ?? "DEFAULT";
                byte[] translated = _securityProvider.TranslatePinBlock(pinBlockBytes, issCode, acqCode);
                
                response.SetField(52, Convert.ToHexString(translated));
                SwitchLogger.Debug($"  [{sessionId}] DE#52 PIN block translated: ISS({issCode}) -> ACQ({acqCode})");
            }
            catch (Exception ex)
            {
                SwitchLogger.Warn($"  [{sessionId}] PIN block translation back failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Forward a PIN transaction (DE#52) to the issuer with full PIN block translation.
        /// Translates the PIN block from the acquirer's zone key to the issuer's zone key
        /// before forwarding, routes the message, then translates the response PIN block
        /// back to the acquirer's zone key.
        /// For non-PIN transactions (no DE#52), the message is routed without translation.
        /// </summary>
        private async Task<IsoMessage> ForwardPinTransactionAsync(
            IsoMessage request,
            IssuerBankConfig issuerBank,
            string sessionId,
            TransactionContext txnContext,
            CancellationToken cancellationToken)
        {
            TranslatePinBlockForIssuer(request, issuerBank, sessionId);

            var response = await RouteMessageAsync(request, issuerBank, sessionId, txnContext, cancellationToken);

            TranslatePinBlockForAcquirer(response, request, sessionId);

            return response;
        }

        private void BufferLog(TransactionContext? ctx, string direction, string? acq, string? iss, IsoMessage? msg, byte[]? bytes)
        {
            if (ctx == null || _messageCycleStore == null || bytes == null) return;

            decimal? amount = null;
            if (decimal.TryParse(msg?.GetField(4) ?? "0", out decimal parsedAmount))
            {
                amount = parsedAmount / 100m;
            }

            ctx.BufferedLogs.Add(new BufferedLogEntry
            {
                Direction = direction,
                ACQ = acq,
                ISS = iss,
                MessageType = msg?.MessageType ?? "Unknown",
                ProcessingCode = msg?.GetField(3),
                Amount = amount,
                STAN = msg?.GetField(11),
                RRN = msg?.GetField(37),
                ResponseCode = msg?.GetField(39),
                RawMessage = _messageCycleStore.BuildSanitizedRawMessageHex(msg, bytes),
                LogTime = DateTime.UtcNow
            });
        }

        private void FlushBufferedLogs(TransactionContext ctx)
        {
            if (_messageCycleStore == null || ctx.BufferedLogs.Count == 0) return;
            _ = _messageCycleStore.LogCyclesAsync(ctx.TransactionId, ctx.SessionId, ctx.BufferedLogs);
            ctx.BufferedLogs.Clear();
        }


        /// <summary>
        /// Route a message to the appropriate issuer.
        /// Connection strategy:
        ///   1. Persistent H2H channel (TSConnectionManager) — preferred for all bank connections.
        ///   2. Pool-based fallback (IssuerConnectionPool via IssuerConnector) — emergency only,
        ///      used when the persistent channel is down and the issuer is active-mode (we dial out).
        ///      This path should generate alerts; if you see frequent fallbacks, investigate the H2H link.
        ///   3. Passive issuers (they connect to us) have no fallback — return RC 91 immediately.
        /// </summary>
        private async Task<IsoMessage> RouteMessageAsync(IsoMessage request, IssuerBankConfig issuerBank, string sessionId, TransactionContext txnContext, CancellationToken cancellationToken = default)
        {
            IsoMessage? response = null;
            try 
            {
                // Primary path: persistent H2H connection
                if (_issuerConnections.TryGetValue(issuerBank.IssuerCode, out var persistentManager) && persistentManager.IsAnyConnected)
                {
                    response = await persistentManager.ForwardTransactionAsync(request, sessionId);
                }
                else
                {
                    bool isPassive = _portToIssuerCode.Values.Contains(issuerBank.IssuerCode);

                    if (isPassive)
                    {
                         SwitchLogger.ForContext("ROUTING").Info("Passive Issuer {Issuer} is NOT connected. Cannot dial out. Session={SessionId}", issuerBank.IssuerName, sessionId);
                         response = null;
                    }
                    else
                    {
                        // Emergency fallback: pool-based connection
                        if (!issuerBank.IsDefault)
                            SwitchLogger.ForContext("ROUTING").Warn("No active persistent connection for {Issuer}, falling back to pool (investigate H2H link). Session={SessionId}", issuerBank.IssuerName, sessionId);

                        ServerMetrics.IncrementConnectionError();
                        response = await _issuerConnector.ForwardToIssuerAsync(request, issuerBank, sessionId, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                SwitchLogger.ForContext("ROUTING").Error("Routing error for session {SessionId}: {Error}", sessionId, ex.Message);
            }

            if (response == null)
            {
                txnContext.TryTransitionTo(TransactionState.Failed, "91", "Issuer unavailable");
                return IsoResponseBuilder.CreateErrorResponse(request, "91");
            }

            return response;
        }


        /// Handle 0420 - Reversal/Void Advice
        /// Per NAPAS spec: Respond immediately with 0430 RC=00 to ACQ, then forward to ISS asynchronously.
        /// - Void (PC 00xxxx): Cancels a purchase before settlement. Always forwarded to ISS.
        /// - Reversal: Corrects any transaction. Only forwarded if the original transaction is found.

        private Task<IsoMessage> HandleReversalAdviceAsync(IsoMessage request, string sessionId, TransactionContext txnContext, CancellationToken cancellationToken = default)
        {
            string? processingCode = request.GetProcessingCode();
            bool isVoid = IsVoidAdvice(processingCode);
            string txnType = isVoid ? "VOID" : "REVERSAL";

            SwitchLogger.ForContext("ADVICE").Info("Processing {TxnType} advice (0420). Session={SessionId}, PC={PC}",
                txnType, sessionId, processingCode ?? "N/A");

            // Per NAPAS spec 4.1.2: Respond immediately with 0430 RC=00 to ACQ
            var ackResponse = IsoResponseBuilder.CreateSuccessResponse(request);

            if (isVoid) ServerMetrics.IncrementVoidAdvice();

            // Clone the request for the background task — the main thread continues using
            // the original `request` for logging/metrics, and Dictionary<int,string> is NOT
            // thread-safe for concurrent read+write. Without this clone,
            // PrepareMessageForRouting in the background task mutates Fields while the
            // main thread may still be iterating over them, causing InvalidOperationException.
            var requestClone = CloneMessage(request);

            // Fire-and-forget: Forward to ISS asynchronously (ACQ does not wait)
            _ = ForwardAdviceToIssuerAsync(requestClone, sessionId, txnContext, isVoid, cancellationToken);

            txnContext.TryTransitionTo(TransactionState.Completed);
            return Task.FromResult(ackResponse);
        }

        /// <summary>
        /// Background task: Forward a 0420 advice to the issuer after the ACQ has been acknowledged.
        /// For reversal: only forward if the original transaction is found in UnsettledTransactions.
        /// For void: always forward (the original purchase must be cancelled at the issuer).
        /// On failure, logs for SAF (Store and Forward) retry.
        /// </summary>
        private async Task ForwardAdviceToIssuerAsync(IsoMessage request, string sessionId, TransactionContext txnContext, bool isVoid, CancellationToken cancellationToken)
        {
            string txnType = isVoid ? "VOID" : "REVERSAL";
            try
            {
                // Settlement date check — NAPAS does not process reversals
                // for transactions outside the current settlement date
                string? settlementDate = request.GetField(15);
                if (!string.IsNullOrEmpty(settlementDate))
                {
                    var vnTz = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
                    string currentDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vnTz).ToString("MMdd");
                    if (settlementDate != currentDate)
                    {
                        SwitchLogger.ForContext("ADVICE").Warn(
                            "{TxnType} settlement date mismatch: DE#15={DE15}, current={Current}. Not forwarding. Session={SessionId}",
                            txnType, settlementDate, currentDate, sessionId);
                        return;
                    }
                }

                // Look up original financial transaction (PURCHASE, etc.) from today's settlement.
                OriginalTransactionInfo? originalInfo = null;
                if (_transactionLogger != null)
                {
                    string? trn = request.GetTRN();
                    string? stan = request.GetSTAN();
                    string? acqId = request.GetAcquirerID();
                    originalInfo = await _transactionLogger.GetOriginalRequestAsync(trn, stan, acqId);
                }

                IsoMessage? original = originalInfo?.Message;
                string? originalTransactionId = originalInfo?.TransactionId;

                // Reversal (not void): Only forward if original transaction is found
                if (!isVoid && original == null)
                {
                    SwitchLogger.ForContext("ADVICE").Info(
                        "Original transaction not found for {TxnType} advice. Not forwarding to ISS. Session={SessionId}",
                        txnType, sessionId);
                    return;
                }

                // Build DE#90 (Original Data Elements) from original if not already present
                if (original != null && !request.HasField(90))
                {
                    request.SetField(90, IsoMessage.BuildDE90(original));
                    SwitchLogger.ForContext("ADVICE").Debug("DE#90 built from original. Session={SessionId}", sessionId);
                }

                var issuerBank = GetIssuer(request, sessionId, txnContext);
                if (issuerBank == null)
                {
                    SwitchLogger.ForContext("ADVICE").Warn(
                        "No issuer found for {TxnType} advice forwarding. Session={SessionId}", txnType, sessionId);
                    return;
                }

                PrepareMessageForRouting(request, issuerBank, sessionId);
                TranslatePinBlockForIssuer(request, issuerBank, sessionId);
                
                BufferLog(txnContext, "FORWARDED", request.GetAcquirerID(), request.GetIssuerID(), request, _parser.Build(request));
                var issuerResponse = await RouteMessageAsync(request, issuerBank, sessionId, txnContext, cancellationToken);
                BufferLog(txnContext, "RECEIVED", issuerResponse.GetAcquirerID(), issuerResponse.GetIssuerID(), issuerResponse, _parser.Build(issuerResponse));

                string? rc = issuerResponse.GetField(39);
                SwitchLogger.ForContext("ADVICE").Info(
                    "ISS {TxnType} response: RC={RC}. Session={SessionId}", txnType, rc ?? "N/A", sessionId);

                if (_transactionLogger != null)
                    _ = _transactionLogger.LogTransactionAsync(request, issuerResponse, txnContext.TransactionId, sessionId, 
                        request.GetField(49), request.GetField(22), request.GetField(15), originalTransactionId);
                
                FlushBufferedLogs(txnContext);
            }
            catch (Exception ex)
            {
                SwitchLogger.ForContext("ADVICE").Error(
                    "Failed to forward {TxnType} advice to ISS. Session={SessionId}, Error={Error}. Queuing for SAF retry.",
                    txnType, sessionId, ex.Message);
                _safQueue?.Enqueue(request, sessionId, isVoid);
            }
        }

        /// <summary>
        /// SAF retry callback: Re-attempt to forward a failed advice to the issuer.
        /// Uses lightweight routing without creating tracked TransactionContexts
        /// to avoid leaking state-machine entries on every retry attempt.
        /// </summary>
        private async Task<bool> RetrySafAdviceAsync(SafEntry entry)
        {
            try
            {
                string? cardBIN = entry.Request.GetCardBIN();
                if (string.IsNullOrEmpty(cardBIN)) return false;

                var issuerBank = _config.GetIssuerByBIN(cardBIN);
                if (issuerBank == null) return false;

                PrepareMessageForRouting(entry.Request, issuerBank, entry.SessionId);
                TranslatePinBlockForIssuer(entry.Request, issuerBank, entry.SessionId);

                IsoMessage? response = null;
                if (_issuerConnections.TryGetValue(issuerBank.IssuerCode, out var manager) && manager.IsAnyConnected)
                {
                    response = await manager.ForwardTransactionAsync(entry.Request, entry.SessionId);
                }

                if (response == null) return false;

                string? rc = response.GetField(39);
                bool success = rc == "00";
                SwitchLogger.ForContext("SAF").Info(
                    "SAF retry {Result} for session {SessionId}: RC={RC}",
                    success ? "succeeded" : "failed", entry.SessionId, rc ?? "N/A");
                return success;
            }
            catch (Exception ex)
            {
                SwitchLogger.ForContext("SAF").Error("SAF retry error for session {SessionId}: {Error}", entry.SessionId, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Check if a 0420 advice is a Void (purchase cancellation) vs. a generic Reversal.
        /// Void uses processing code 00xxxx (purchase transaction type).
        /// </summary>
        private static bool IsVoidAdvice(string? processingCode)
        {
            return !string.IsNullOrEmpty(processingCode) && processingCode.Length >= 2 && processingCode.StartsWith("00");
        }

        private async Task<IsoMessage> HandleReversalRequestAsync(IsoMessage request, string sessionId, TransactionContext txnContext, CancellationToken cancellationToken = default)
        {
            SwitchLogger.Info($" [{sessionId}] Processing reversal request");
            
            string? originalTransactionId = null;
            if (_transactionLogger != null)
            {
                string? trn = request.GetTRN();
                string? stan = request.GetSTAN();
                string? acqId = request.GetAcquirerID();
                var originalInfo = await _transactionLogger.GetOriginalRequestAsync(trn, stan, acqId);
                var original = originalInfo?.Message;
                originalTransactionId = originalInfo?.TransactionId;
                
                if (original == null)
                {
                    SwitchLogger.Debug($"  [{sessionId}] Original transaction not found for reversal (TRN: {trn}, STAN: {stan})");
                    txnContext.TryTransitionTo(TransactionState.Reversed, "25", "Original transaction not found");
                    return IsoResponseBuilder.CreateErrorResponse(request, "25");
                }
                SwitchLogger.Debug($"  [{sessionId}] Original transaction found (Type: {original.MessageType})");

                if (!request.HasField(90))
                {
                    request.SetField(90, IsoMessage.BuildDE90(original));
                    SwitchLogger.Debug($"  [{sessionId}] DE#90 built from original: {request.GetField(90)}");
                }
            }

            var issuerBank = GetIssuer(request, sessionId, txnContext);
            if (issuerBank == null) return IsoResponseBuilder.CreateErrorResponse(request, "15");

            PrepareMessageForRouting(request, issuerBank, sessionId);

            txnContext.TryTransitionTo(TransactionState.Reversing);
            BufferLog(txnContext, "FORWARDED", request.GetAcquirerID(), request.GetIssuerID(), request, _parser.Build(request));
            var response = await ForwardPinTransactionAsync(request, issuerBank, sessionId, txnContext, cancellationToken);
            BufferLog(txnContext, "RECEIVED", response.GetAcquirerID(), response.GetIssuerID(), response, _parser.Build(response));

            if (_transactionLogger != null)
            {
                _ = _transactionLogger.LogTransactionAsync(request, response, txnContext.TransactionId, sessionId,
                    request.GetField(49), request.GetField(22), request.GetField(15), originalTransactionId);
            }

            if (response.GetField(39) == "00")
                txnContext.TryTransitionTo(TransactionState.Reversed);
            else
                txnContext.TryTransitionTo(TransactionState.Failed);

            FlushBufferedLogs(txnContext);
            return response;
        }

        
        // Network Management (0800) - Echo Test
        private IsoMessage HandleEchoRequest(IsoMessage request, string sessionId, TransactionContext txnContext)
        {
            var response = IsoResponseBuilder.CreateSuccessResponse(request);
            response.MessageType = MtiHelper.NetworkManagementResponse;
            
            response.SetField(39, "00");
            
            if (request.HasField(70)) response.SetField(70, request.GetField(70));

            txnContext.TryTransitionTo(TransactionState.Completed);
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
            
            // DE#15 Settlement Date (MMDD)
            // NAPAS spec 4.2.1: The switch ALWAYS sets DE#15 to the current settlement date.
            // This is intentional — NAPAS does not allow member institutions to set their own
            // settlement date. If an acquirer sends a correction for a prior day's transaction,
            // it must go through a separate reconciliation process (not DE#15 override).
            // Void/reversal for prior-day transactions is blocked by the SettlementDate check
            // in ForwardAdviceToIssuerAsync and GetOriginalForVoidReversalAsync.
            msg.SetField(15, nowVn.ToString("MMdd"));
        }

        private void EnsureMandatoryNapasFields(IsoMessage request, string sessionId)
        {
            var defaults = _config.ServerConfig.Defaults;

            if (!request.HasField(22))
            {
                request.SetField(22, defaults.DefaultPOSEntryMode); 
                SwitchLogger.Info($" [{sessionId}] DE#22 missing, setting default: {defaults.DefaultPOSEntryMode}");
            }

            if (!request.HasField(25))
            {
                request.SetField(25, defaults.DefaultPOSConditionCode);
                SwitchLogger.Info($" [{sessionId}] DE#25 missing, setting default: {defaults.DefaultPOSConditionCode}");
            }

            if (!request.HasField(42))
            {
                request.SetField(42, defaults.DefaultMerchantId);
                SwitchLogger.Info($" [{sessionId}] DE#42 missing, setting default: {defaults.DefaultMerchantId}");
            }

            if (!request.HasField(43))
            {
                request.SetField(43, defaults.DefaultMerchantName);
                SwitchLogger.Info($" [{sessionId}] DE#43 missing, setting default: NAPAS TEST MERCHANT");
            }
            
            if (!request.HasField(49))
            {
                request.SetField(49, defaults.DefaultCurrencyCode);
            }
            
            if (!request.HasField(18))
            {
                request.SetField(18, defaults.DefaultMerchantCategoryCode);
            }
        }


        
        /// Stop the server
        
        public void Stop()
        {
            _isRunning = false;
            _serverCts?.Cancel();
            foreach (var listener in _listeners)
            {       
                try { listener.Stop(); } catch { }
            }
            SwitchLogger.Info("Server stopped");
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
                Console.WriteLine("  {0,-14} {1,-22} {2,-10} {3,-10}",
                    "Issuer", "Host", "Channel", "Status");
                foreach (var kvp in _issuerConnections)
                {
                    var manager = kvp.Value;
                    var channelStatuses = manager.GetChannelStatuses();
                    bool isPassive = _portToIssuerCode.Values.Contains(kvp.Key);
                    string mode = isPassive ? "PSV" : "ACT";

                    foreach (var ch in channelStatuses)
                    {
                        string hostPort = $"{ch.Host}:{ch.Port}";
                        Console.Write("  {0,-14} {1,-22} {2,-10} ",
                            Truncate(ch.IssuerName, 14), Truncate(hostPort, 20), $"CH{ch.ChannelIndex}({mode})");

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
                UptimeSince = _serverStartTime
            };
        }

        
        
        /// Dispose resources
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();

            // 15.2: Drain active sessions before disposing issuer connections
            var drainDeadline = DateTime.UtcNow.AddSeconds(30);
            while (_activeSessions.Count > 0 && DateTime.UtcNow < drainDeadline)
            {
                SwitchLogger.ForContext("DISPOSE").Info("Waiting for {SessionCount} active session(s) to drain...", _activeSessions.Count);
                Thread.Sleep(1000);
            }
            if (_activeSessions.Count > 0)
            {
                SwitchLogger.ForContext("DISPOSE").Info("Drain timeout: {SessionCount} session(s) still active, proceeding with shutdown", _activeSessions.Count);
            }

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
                    SwitchLogger.ForContext("DISPOSE").Error("Error cleaning up manager: {Error}", ex.Message);
                }
            }
            _issuerConnections.Clear();

            _statsTimer?.Dispose();
            _poolHealthTimer?.Dispose();
            _healthCheck.Dispose();
            _safQueue?.Dispose();
            _serverCts?.Dispose();
            _issuerConnector?.Dispose();
            _transactionLogger?.Dispose();
            _stateMachine?.Dispose();
            
            SwitchLogger.ForContext("DISPOSE").Info("TcpSwitchServer disposed");
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

            // Use cryptographically secure random number generator
            // Using GetInt32 to avoid modulo bias (62 doesn't divide 256 evenly)
            for (int i = 0; i < length; i++)
            {
                result[i] = chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, chars.Length)];
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
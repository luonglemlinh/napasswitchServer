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
        private bool _disposed;
        private Timer? _statsTimer;
        private Timer? _poolHealthTimer;

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
            
            _stateMachine.OnTransactionTimeout += OnTransactionTimeout;
            _stateMachine.OnReversalRequired += OnReversalRequired;
            
            string configPath = FindValidationConfigPath();
            _validator = new NapasDataElementValidator(configPath);
            
            // Initialize transaction logger
            if (enableLogging && !string.IsNullOrEmpty(dbConnectionString))
            {
                _transactionLogger = new TransactionLogger(dbConnectionString, enableLogging);
                Console.WriteLine("[INIT] Transaction logger initialized with circuit breaker");
            }
            else
            {
                _transactionLogger = null;
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
                    // Step 1: Read message length header (2 bytes, big-endian)
                    byte[] lengthBytes = new byte[2];
                    int bytesRead = stream.Read(lengthBytes, 0, 2);
                    if (bytesRead == 0) break; // Client disconnected gracefully

                    // Convert 2 bytes to integer (big-endian format)
                    int messageLength = (lengthBytes[0] << 8) | lengthBytes[1];

                    if (messageLength <= 0 || messageLength > 9999)
                    {
                        Console.WriteLine($"  [{sessionId}] Invalid message length: {messageLength}");
                        break;
                    }

                    // Step 2: Read the actual ISO-8583 message
                    byte[] messageBytes = new byte[messageLength];
                    int totalRead = 0;
                    while (totalRead < messageLength)
                    {
                        bytesRead = stream.Read(messageBytes, totalRead, messageLength - totalRead);
                        if (bytesRead == 0) break; // Connection lost
                        totalRead += bytesRead;
                    }

                    if (totalRead < messageLength)
                    {
                        Console.WriteLine($"  [{sessionId}] Incomplete message (expected {messageLength}, got {totalRead})");
                        break;
                    }

                    Console.WriteLine($" [{sessionId}] Received {messageLength} bytes");

                    // Step 3: Process the message and get response
                    byte[]? responseBytes = ProcessMessage(messageBytes, sessionId);

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

        
        /// Process an incoming ISO-8583 message
        /// This is where the routing magic happens!
        
        private byte[]? ProcessMessage(byte[] messageBytes, string sessionId)
        {
            var stopwatch = Stopwatch.StartNew();
            TransactionContext? txnContext = null;
            
            try
            {
                IsoMessage request = _parser.Parse(messageBytes);
                
                string txnId = $"{request.GetField(11)}_{DateTime.UtcNow.Ticks}";
                txnContext = _stateMachine.CreateTransaction(sessionId, request);

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
                    request.SetField(2, encryptedPan);
                }

                if (_transactionLogger != null)
                {
                    _ = _transactionLogger.LogRequestAsync(request, sessionId, "INBOUND");
                }

                if (!string.IsNullOrEmpty(encryptedPan) && !string.IsNullOrEmpty(clearPan))
                {
                    request.SetField(2, clearPan);
                }

                IsoMessage response = request.MessageType switch
                {
                    "0200" => HandleAuthorizationRequest(request, sessionId, txnContext),
                    "0400" => HandleReversalRequest(request, sessionId, txnContext),
                    "0800" => HandleNetworkManagement(request, sessionId),
                    _ => CreateErrorResponse(request, "12")
                };

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
            _issuerConnector?.Dispose();
            _transactionLogger?.Dispose();
            _stateMachine?.Dispose();
            
            Console.WriteLine("[DISPOSE] TcpSwitchServer disposed");
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
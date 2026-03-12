using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using core.Configuration;
using core.Helpers;
using router;

namespace server
{
    /// <summary>
    /// Standalone HTTP endpoint for health checks, readiness probes, and metrics.
    /// Extracted from TcpSwitchServer to keep connection-handling code focused.
    /// </summary>
    public sealed class HealthCheckServer : IDisposable
    {
        private HttpListener? _listener;
        private Task? _listenTask;
        private readonly Func<bool> _isRunning;
        private readonly Func<ServerStats> _getStats;
        private readonly Func<TransactionStateMachineStats> _getTxnStats;
        private readonly ConcurrentDictionary<string, TSConnectionManager> _issuerConnections;
        private readonly DateTime _serverStartTime;
        private readonly IConfigurationLoader _config;

        public HealthCheckServer(
            Func<bool> isRunning,
            Func<ServerStats> getStats,
            Func<TransactionStateMachineStats> getTxnStats,
            ConcurrentDictionary<string, TSConnectionManager> issuerConnections,
            DateTime serverStartTime,
            IConfigurationLoader config)
        {
            _isRunning = isRunning;
            _getStats = getStats;
            _getTxnStats = getTxnStats;
            _issuerConnections = issuerConnections;
            _serverStartTime = serverStartTime;
            _config = config;
        }

        public void Start()
        {
            try
            {
                var serverConfig = _config.ServerConfig;
                int port = serverConfig.Settings.HealthCheckPort;
                if (port <= 0) return;

                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://+:{port}/");
                _listener.Start();

                _listenTask = Task.Run(async () =>
                {
                    SwitchLogger.ForContext("HEALTH").Info("HTTP health check listening on port {Port}", port);
                    while (_listener.IsListening)
                    {
                        try
                        {
                            var ctx = await _listener.GetContextAsync();
                            await HandleRequestAsync(ctx);
                        }
                        catch (HttpListenerException) { break; }
                        catch (ObjectDisposedException) { break; }
                        catch (Exception ex)
                        {
                            SwitchLogger.ForContext("HEALTH").Debug("Error: {Error}", ex.Message);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                SwitchLogger.ForContext("HEALTH").Warn("Failed to start health check endpoint: {Error}. " +
                    "Run as admin or use: netsh http add urlacl url=http://+:{Port}/ user=Everyone", ex.Message);
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            var request = ctx.Request;
            var response = ctx.Response;

            try
            {
                string path = request.Url?.AbsolutePath?.TrimEnd('/') ?? "";

                string json;
                int statusCode;

                switch (path)
                {
                    case "/health":
                    case "":
                        statusCode = _isRunning() ? 200 : 503;
                        json = BuildHealthJson();
                        break;
                    case "/health/ready":
                        bool hasConnections = _issuerConnections.Values.Any(c => c.IsAnyConnected);
                        statusCode = (_isRunning() && hasConnections) ? 200 : 503;
                        json = JsonSerializer.Serialize(new { status = statusCode == 200 ? "ready" : "not_ready", isRunning = _isRunning(), issuerConnections = hasConnections });
                        break;
                    case "/metrics":
                        statusCode = 200;
                        json = ServerMetrics.ToJson();
                        break;
                    default:
                        statusCode = 404;
                        json = "{\"error\":\"not_found\"}";
                        break;
                }

                response.StatusCode = statusCode;
                response.ContentType = "application/json";
                byte[] body = Encoding.UTF8.GetBytes(json);
                response.ContentLength64 = body.Length;
                await response.OutputStream.WriteAsync(body, 0, body.Length);
            }
            catch (Exception ex)
            {
                SwitchLogger.Debug("Health response error: {Error}", ex.Message);
            }
            finally
            {
                try { response.Close(); } catch { }
            }
        }

        private string BuildHealthJson()
        {
            var stats = _getStats();
            var txnStats = _getTxnStats();
            int persistentConnected = _issuerConnections.Values.Count(c => c.IsAnyConnected);

            var connections = new System.Collections.Generic.Dictionary<string, object>();
            foreach (var kvp in _issuerConnections)
            {
                // Use BankCode for consistent display (e.g. "VCB", "TCB") instead of the raw IssuerCode/Key
                string displayKey = !string.IsNullOrEmpty(kvp.Value.Config.BankCode) 
                    ? kvp.Value.Config.BankCode 
                    : kvp.Key;
                
                connections[displayKey] = new { connected = kvp.Value.IsAnyConnected };
            }

            var health = new
            {
                status = _isRunning() ? "healthy" : "unhealthy",
                uptime = (DateTime.UtcNow - _serverStartTime).ToString(@"d\.hh\:mm\:ss"),
                activeConnections = stats.ActiveConnections,
                totalMessages = stats.TotalMessageCount,
                tps = ServerMetrics.GetTransactionsPerSecond(),
                transactions = new
                {
                    pending = txnStats.RoutingCount,
                    completed = txnStats.CompletedCount,
                    failed = txnStats.FailedCount,
                    reversing = txnStats.ReversingCount,
                    active = txnStats.ActiveTransactions
                },
                issuerConnections = new
                {
                    connected = persistentConnected,
                    total = _issuerConnections.Count,
                    details = connections
                },
                timestamp = DateTime.UtcNow.ToString("o")
            };

            return JsonSerializer.Serialize(health, new JsonSerializerOptions { WriteIndented = true });
        }

        public void Dispose()
        {
            try { _listener?.Stop(); _listener?.Close(); } catch { }
        }
    }
}

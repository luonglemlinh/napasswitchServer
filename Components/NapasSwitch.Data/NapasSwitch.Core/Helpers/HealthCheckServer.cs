using System.Net;
using System.Text;
using System.Text.Json;

namespace core.Helpers
{
    /// <summary>
    /// Lightweight HTTP health-check endpoint for load balancers.
    /// Listens on a configurable port and responds to:
    ///   GET /health  → 200 OK with JSON status + metrics
    ///   GET /ready   → 200 OK when accepting traffic, 503 otherwise
    ///   GET /metrics → 200 OK with full metrics JSON
    /// </summary>
    public sealed class HealthCheckServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly int _port;
        private readonly CancellationTokenSource _cts = new();
        private Task? _listenTask;
        private bool _disposed;

        private volatile bool _isReady;

        /// <summary>
        /// Set to true once the server is accepting acquirer traffic.
        /// </summary>
        public bool IsReady
        {
            get => _isReady;
            set => _isReady = value;
        }

        /// <summary>
        /// Optional callback that returns the number of active sessions.
        /// </summary>
        public Func<int>? ActiveSessionsProvider { get; set; }

        /// <summary>
        /// Optional callback that returns the number of connected issuers.
        /// </summary>
        public Func<int>? ConnectedIssuersProvider { get; set; }

        public HealthCheckServer(int port = 8080)
        {
            _port = port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_port}/");
        }

        /// <summary>
        /// Start the health-check HTTP listener in the background.
        /// </summary>
        public void Start()
        {
            try
            {
                _listener.Start();
                _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
                SwitchLogger.Info("[HEALTH] Health-check endpoint started on port {Port}", _port);
            }
            catch (HttpListenerException ex)
            {
                // Requires admin / URL reservation on Windows
                SwitchLogger.Warn("[HEALTH] Could not start health-check listener on port {Port}: {Error}. " +
                    "Run as admin or use 'netsh http add urlacl url=http://+:{Port2}/ user=Everyone'",
                    _port, ex.Message, _port);
            }
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync().WaitAsync(token);
                    _ = Task.Run(() => HandleRequest(context), token);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    SwitchLogger.Debug("[HEALTH] Listener error: {Error}", ex.Message);
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                string path = context.Request.Url?.AbsolutePath?.ToLowerInvariant() ?? "/";
                int statusCode;
                string body;

                switch (path)
                {
                    case "/health":
                        statusCode = 200;
                        body = BuildHealthJson();
                        break;

                    case "/ready":
                        statusCode = _isReady ? 200 : 503;
                        body = JsonSerializer.Serialize(new { ready = _isReady });
                        break;

                    case "/metrics":
                        statusCode = 200;
                        body = ServerMetrics.ToJson();
                        break;

                    default:
                        statusCode = 404;
                        body = "{\"error\":\"not found\"}";
                        break;
                }

                byte[] data = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = data.Length;
                context.Response.OutputStream.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                SwitchLogger.Debug("[HEALTH] Error handling request: {Error}", ex.Message);
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        private string BuildHealthJson()
        {
            var snapshot = ServerMetrics.GetSnapshot();
            var health = new
            {
                status = _isReady ? "UP" : "STARTING",
                timestamp = DateTime.UtcNow.ToString("o"),
                activeSessions = ActiveSessionsProvider?.Invoke() ?? 0,
                connectedIssuers = ConnectedIssuersProvider?.Invoke() ?? 0,
                metrics = new
                {
                    snapshot.TotalTransactions,
                    snapshot.TransactionsPerSecond,
                    snapshot.SuccessfulTransactions,
                    snapshot.FailedTransactions,
                    errorRate = snapshot.TotalTransactions > 0
                        ? Math.Round((double)snapshot.FailedTransactions / snapshot.TotalTransactions * 100, 2)
                        : 0.0
                }
            };

            return JsonSerializer.Serialize(health, new JsonSerializerOptions { WriteIndented = true });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _cts.Dispose();
            SwitchLogger.Info("[HEALTH] Health-check endpoint stopped");
        }
    }
}

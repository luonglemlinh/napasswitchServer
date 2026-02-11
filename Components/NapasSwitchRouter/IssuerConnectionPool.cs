using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using core.Models.Configuration;

namespace router
{
    
    /// Manages a pool of TCP connections to Issuer banks
    /// Reuses connections instead of creating new ones for every transaction
    
    public class IssuerConnectionPool : IDisposable
    {
        private readonly ConcurrentDictionary<string, ConnectionPoolEntry> _pools;
        private readonly int _maxPoolSize;
        private readonly int _connectionIdleTimeoutMs;
        private readonly Timer _cleanupTimer;
        private bool _disposed;

        public IssuerConnectionPool(int maxPoolSize = 10, int connectionIdleTimeoutMs = 60000)
        {
            _maxPoolSize = maxPoolSize;
            _connectionIdleTimeoutMs = connectionIdleTimeoutMs;
            _pools = new ConcurrentDictionary<string, ConnectionPoolEntry>();
            
            // Cleanup idle connections every 30 seconds
            _cleanupTimer = new Timer(CleanupIdleConnections, null, 30000, 30000);
        }

        
        /// Get a connection from the pool or create a new one
        
        public PooledConnection GetConnection(IssuerBankConfig issuerBank)
        {
            string poolKey = $"{issuerBank.Host}:{issuerBank.Port}";
            
            var poolEntry = _pools.GetOrAdd(poolKey, _ => new ConnectionPoolEntry(_maxPoolSize));
            
            // Try to get existing connection from pool
            if (poolEntry.TryGetConnection(out TcpClient? existingClient, out NetworkStream? existingStream) 
                && existingClient != null && existingStream != null)
            {
                if (IsConnectionAlive(existingClient))
                {
                    Console.WriteLine($"[POOL] Reusing existing connection to {poolKey}");
                    return new PooledConnection(existingClient, existingStream, poolEntry, poolKey);
                }
                else
                {
                    // Connection is dead, close it and fix the count
                    SafeClose(existingClient, existingStream);
                    poolEntry.DecrementActiveCount();
                }
            }

            // Create new connection
            Console.WriteLine($"[POOL] Creating new connection to {poolKey}");
            var client = new TcpClient();
            
            // Use async connect with timeout instead of blocking
            var connectTask = client.ConnectAsync(issuerBank.Host, issuerBank.Port);
            if (!connectTask.Wait(issuerBank.Timeout))
            {
                client.Close();
                throw new TimeoutException($"Connection to {poolKey} timed out after {issuerBank.Timeout}ms");
            }
            
            var stream = client.GetStream();
            stream.ReadTimeout = issuerBank.Timeout;
            stream.WriteTimeout = issuerBank.Timeout;

            poolEntry.IncrementActiveCount();
            return new PooledConnection(client, stream, poolEntry, poolKey);
        }

        
        /// Check if a connection is still alive
        
        private bool IsConnectionAlive(TcpClient? client)
        {
            if (client == null || !client.Connected)
                return false;

            try
            {
                // Check if the socket is still connected
                var socket = client.Client;
                return !(socket.Poll(1, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch
            {
                return false;
            }
        }

        private void SafeClose(TcpClient? client, NetworkStream? stream)
        {
            try { stream?.Close(); } catch { }
            try { client?.Close(); } catch { }
        }

        private void CleanupIdleConnections(object? state)
        {
            var cutoffTime = DateTime.UtcNow.AddMilliseconds(-_connectionIdleTimeoutMs);
            
            foreach (var kvp in _pools)
            {
                kvp.Value.CleanupIdleConnections(cutoffTime);
            }
        }

        public ConnectionPoolStats GetStats()
        {
            int totalActive = 0;
            int totalPooled = 0;

            foreach (var kvp in _pools)
            {
                totalActive += kvp.Value.ActiveCount;
                totalPooled += kvp.Value.PooledCount;
            }

            return new ConnectionPoolStats
            {
                TotalActivedConnections = totalActive,
                TotalPooledConnections = totalPooled,
                PoolCount = _pools.Count
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cleanupTimer?.Dispose();

            foreach (var kvp in _pools)
            {
                kvp.Value.Dispose();
            }
            _pools.Clear();
        }
    }

    
    /// Represents a single pool of connections to one host
    
    internal class ConnectionPoolEntry : IDisposable
    {
        private readonly ConcurrentQueue<PooledConnectionItem> _availableConnections;
        private readonly int _maxSize;
        private int _activeCount;

        public int ActiveCount => _activeCount;
        public int PooledCount => _availableConnections.Count;

        public ConnectionPoolEntry(int maxSize)
        {
            _maxSize = maxSize;
            _availableConnections = new ConcurrentQueue<PooledConnectionItem>();
        }

        public bool TryGetConnection(out TcpClient? client, out NetworkStream? stream)
        {
            while (_availableConnections.TryDequeue(out var item))
            {
                if (item.Client.Connected)
                {
                    client = item.Client;
                    stream = item.Stream;
                    Interlocked.Increment(ref _activeCount);
                    return true;
                }
                else
                {
                    // Connection is dead, dispose it
                    SafeDispose(item);
                }
            }

            client = null;
            stream = null;
            return false;
        }

        public void ReturnConnection(TcpClient client, NetworkStream stream)
        {
            Interlocked.Decrement(ref _activeCount);

            if (_availableConnections.Count < _maxSize && client.Connected)
            {
                _availableConnections.Enqueue(new PooledConnectionItem
                {
                    Client = client,
                    Stream = stream,
                    LastUsed = DateTime.UtcNow
                });
            }
            else
            {
                SafeDispose(client, stream);
            }
        }

        public void IncrementActiveCount()
        {
            Interlocked.Increment(ref _activeCount);
        }

        public void DecrementActiveCount()
        {
            Interlocked.Decrement(ref _activeCount);
        }

        public void CleanupIdleConnections(DateTime cutoffTime)
        {
            var tempList = new List<PooledConnectionItem>();
            
            while (_availableConnections.TryDequeue(out var item))
            {
                if (item.LastUsed > cutoffTime && IsConnectionAlive(item.Client))
                {
                    tempList.Add(item);
                }
                else
                {
                    SafeDispose(item);
                }
            }

            foreach (var item in tempList)
            {
                _availableConnections.Enqueue(item);
            }
        }

        private static bool IsConnectionAlive(TcpClient? client)
        {
            if (client == null || !client.Connected) return false;
            try
            {
                var socket = client.Client;
                return !(socket.Poll(1, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch { return false; }
        }

        private void SafeDispose(PooledConnectionItem item)
        {
            SafeDispose(item.Client, item.Stream);
        }

        private void SafeDispose(TcpClient client, NetworkStream stream)
        {
            try { stream?.Close(); } catch { }
            try { client?.Close(); } catch { }
        }

        public void Dispose()
        {
            while (_availableConnections.TryDequeue(out var item))
            {
                SafeDispose(item);
            }
        }
    }

    internal class PooledConnectionItem
    {
        public TcpClient Client { get; set; } = null!;
        public NetworkStream Stream { get; set; } = null!;
        public DateTime LastUsed { get; set; }
    }

    
    /// Represents a connection checked out from the pool
    /// Implements IDisposable to return connection to pool
    
    public class PooledConnection : IDisposable
    {
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        
        private readonly ConnectionPoolEntry _poolEntry;
        private readonly string _poolKey;
        private bool _disposed;
        private bool _markAsFailed;

        internal PooledConnection(TcpClient client, NetworkStream stream, ConnectionPoolEntry poolEntry, string poolKey)
        {
            Client = client;
            Stream = stream;
            _poolEntry = poolEntry;
            _poolKey = poolKey;
        }

        
        /// Mark this connection as failed (will not be returned to pool)
        
        public void MarkAsFailed()
        {
            _markAsFailed = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_markAsFailed)
            {
                // Connection failed, don't return to pool
                try { Stream?.Close(); } catch { }
                try { Client?.Close(); } catch { }
                _poolEntry.DecrementActiveCount();
                Console.WriteLine($"[POOL] Connection to {_poolKey} marked as failed, closing");
            }
            else
            {
                // Return to pool
                _poolEntry.ReturnConnection(Client, Stream);
                Console.WriteLine($"[POOL] Connection to {_poolKey} returned to pool");
            }
        }
    }

    public class ConnectionPoolStats
    {
        public int TotalActivedConnections { get; set; }
        public int TotalPooledConnections { get; set; }
        public int PoolCount { get; set; }
    }
}

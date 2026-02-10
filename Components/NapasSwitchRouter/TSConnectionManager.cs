using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using core.Models;
using core.Models.Configuration;

namespace router
{
    /// <summary>
    /// Manages multiple persistent connections (channels) to the Transaction Switch (TS)
    /// Provides load balancing and failover across multiple channels.
    /// </summary>
    public class TSConnectionManager : IDisposable
    {
        private readonly List<TSPersistentConnection> _channels;
        private readonly IssuerBankConfig _tsConfig;
        private readonly int _channelCount;
        private int _lastChannelIndex = -1;
        private bool _disposed;

        public bool IsAnyConnected => _channels.Any(c => c.IsConnected);
        public int ConnectedCount => _channels.Count(c => c.IsConnected);
        public string TSName => _tsConfig.IssuerName;

        public TSConnectionManager(IssuerBankConfig tsConfig, int channelCount = 1, int heartbeatIntervalMs = 75000)
        {
            _tsConfig = tsConfig ?? throw new ArgumentNullException(nameof(tsConfig));
            _channelCount = channelCount;
            _channels = new List<TSPersistentConnection>();

            for (int i = 0; i < _channelCount; i++)
            {
                var channel = new TSPersistentConnection(tsConfig, heartbeatIntervalMs);
                _channels.Add(channel);
            }
        }

        /// <summary>
        /// Connect all channels asynchronously
        /// </summary>
        public async Task ConnectAllAsync()
        {
            core.Helpers.MessageLogger.LogConnectionEvent("TS-MGR", $"Connecting {_channelCount} channels to {TSName}...");
            var tasks = _channels.Select(c => c.ConnectAsync());
            await Task.WhenAll(tasks);
            core.Helpers.MessageLogger.LogConnectionEvent("TS-MGR", $"Connected {ConnectedCount}/{_channelCount} channels");
        }

        /// <summary>
        /// Accept an incoming connection (Passive Mode)
        /// </summary>
        public async Task<bool> AcceptConnectionAsync(System.Net.Sockets.TcpClient client)
        {
            // For passive mode, we typically use the first channel (or find an idle one)
            // Since we usually have 1 channel for H2H, we just use the first/primary channel.
            
            var channel = _channels.FirstOrDefault();
            if (channel == null) return false;

            core.Helpers.MessageLogger.LogConnectionEvent("TS-MGR", $"Accepting inbound connection for {TSName}...");
            return await channel.AttachClientAsync(client);
        }

        /// <summary>
        /// Forward a transaction using an available channel (Round-Robin)
        /// </summary>
        public async Task<IsoMessage?> ForwardTransactionAsync(IsoMessage request, string sessionId)
        {
            // Try to find a connected channel using Round-Robin
            for (int i = 0; i < _channelCount; i++)
            {
                int index = Interlocked.Increment(ref _lastChannelIndex) % _channelCount;
                var channel = _channels[index];

                if (channel.IsConnected)
                {
                    return await channel.ForwardTransactionAsync(request, sessionId);
                }
            }

            // Fallback: If no channel is connected, try to connect the first one and use it
            Console.WriteLine($"[TS-MGR] [{sessionId}] No active channels, attempting emergency connect on Channel 0");
            var firstChannel = _channels[0];
            if (await firstChannel.ConnectAsync())
            {
                return await firstChannel.ForwardTransactionAsync(request, sessionId);
            }

            Console.WriteLine($"[TS-MGR] [{sessionId}] Failed to find any active channel for routing");
            return null;
        }

        /// <summary>
        /// Get the status of each channel for monitoring
        /// </summary>
        public List<ChannelStatus> GetChannelStatuses()
        {
            var statuses = new List<ChannelStatus>();
            for (int i = 0; i < _channels.Count; i++)
            {
                var ch = _channels[i];
                statuses.Add(new ChannelStatus
                {
                    ChannelIndex = i,
                    IssuerName = _tsConfig.IssuerName,
                    IssuerCode = _tsConfig.IssuerCode,
                    Host = _tsConfig.Host,
                    Port = _tsConfig.Port,
                    IsConnected = ch.IsConnected
                });
            }
            return statuses;
        }

        public void DisconnectAll()
        {
            foreach (var channel in _channels)
            {
                channel.Disconnect();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var channel in _channels)
            {
                channel.Dispose();
            }
            _channels.Clear();
        }
    }

    public class ChannelStatus
    {
        public int ChannelIndex { get; set; }
        public string IssuerName { get; set; } = string.Empty;
        public string IssuerCode { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public bool IsConnected { get; set; }
    }
}

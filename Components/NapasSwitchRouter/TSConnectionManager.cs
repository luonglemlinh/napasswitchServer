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

        public TSConnectionManager(IssuerBankConfig tsConfig, int channelCount = 10, int heartbeatIntervalMs = 75000)
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
            Console.WriteLine($"[TS-MGR] Connecting {_channelCount} channels to {TSName}...");
            var tasks = _channels.Select(c => c.ConnectAsync());
            await Task.WhenAll(tasks);
            Console.WriteLine($"[TS-MGR] Connected {ConnectedCount}/{_channelCount} channels");
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
}

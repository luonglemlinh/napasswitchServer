using System.Net.Sockets;

namespace core.Helpers
{
    /// <summary>
    /// Shared helper for reliable NetworkStream read operations.
    /// Replaces 3 duplicate ReadExact implementations across TcpSwitchServer,
    /// TSPersistentConnection, and IssuerConnector.
    /// </summary>
    public static class NetworkStreamHelper
    {
        /// <summary>
        /// Synchronously reads exactly <paramref name="length"/> bytes from the stream.
        /// Returns null if the remote side closes the connection before all bytes are read.
        /// </summary>
        public static byte[]? ReadExactOrNull(NetworkStream stream, int length)
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

        /// <summary>
        /// Asynchronously reads exactly <paramref name="count"/> bytes into the buffer.
        /// Returns the total bytes read, or 0 if the connection was closed.
        /// </summary>
        public static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken token = default)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, token);
                if (read == 0) return 0;
                totalRead += read;
            }
            return totalRead;
        }

        /// <summary>
        /// Asynchronously reads exactly <paramref name="count"/> bytes into the buffer.
        /// Returns true if all bytes were read, false if the connection was closed prematurely.
        /// </summary>
        public static async Task<bool> TryReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken token = default)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int bytesRead = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, token);
                if (bytesRead == 0) return false;
                totalRead += bytesRead;
            }
            return true;
        }
    }
}

using System;
using System.Net.Sockets;
using core.Helpers;
using network;

namespace server
{
    /// <summary>
    /// Reads length-delimited ISO-8583 frames from a NetworkStream.
    /// Enforces a single wire format per instance — no guessing.
    /// </summary>
    public sealed class MessageFramer
    {
        public enum LengthHeaderFormat
        {
            Ascii4Byte,
            Binary4ByteBigEndian,
            Binary2ByteBigEndian
        }

        private readonly LengthHeaderFormat _format;
        private readonly int _headerSize;
        private const int MaxMessageLength = 2000;

        public MessageFramer(LengthHeaderFormat format = LengthHeaderFormat.Ascii4Byte)
        {
            _format = format;
            _headerSize = format == LengthHeaderFormat.Binary2ByteBigEndian ? 2 : 4;
        }

        /// <summary>
        /// Read a single framed message from the stream.
        /// Returns null if the client disconnected or the frame is invalid.
        /// </summary>
        public byte[]? ReadMessage(NetworkStream stream, string sessionId)
        {
            byte[]? lengthBytes = NetworkStreamHelper.ReadExactOrNull(stream, _headerSize);
            if (lengthBytes == null) return null;

            int messageLength = ParseLength(lengthBytes);

            if (messageLength <= 0 || messageLength > MaxMessageLength)
            {
                SwitchLogger.ForContext("FRAMING").Warn(
                    "Invalid length header from {SessionId}: {Header} (parsed={Length}). Disconnecting.",
                    sessionId, BitConverter.ToString(lengthBytes), messageLength);
                return null;
            }

            byte[]? messageBytes = NetworkStreamHelper.ReadExactOrNull(stream, messageLength);
            if (messageBytes == null)
            {
                SwitchLogger.ForContext("FRAMING").Debug(
                    "Incomplete message from {SessionId} (expected {Length} bytes)", sessionId, messageLength);
                return null;
            }

            SwitchLogger.Info($" [{sessionId}] Received {messageLength} bytes");
            return messageBytes;
        }

        /// <summary>
        /// Write a framed response to the stream using the same header format.
        /// </summary>
        public void WriteMessage(NetworkStream stream, byte[] data, string sessionId)
        {
            byte[] header = FormatLength(data.Length);
            stream.Write(header, 0, header.Length);
            stream.Write(data, 0, data.Length);
            stream.Flush();

            string headerDisplay = _format == LengthHeaderFormat.Ascii4Byte
                ? System.Text.Encoding.ASCII.GetString(header)
                : BitConverter.ToString(header);
            SwitchLogger.Info($" [{sessionId}] Sent {data.Length} bytes response (Length Header: {headerDisplay})");
        }

        private int ParseLength(byte[] bytes)
        {
            return _format switch
            {
                LengthHeaderFormat.Ascii4Byte =>
                    int.TryParse(System.Text.Encoding.ASCII.GetString(bytes), out int ascii) ? ascii : -1,
                LengthHeaderFormat.Binary4ByteBigEndian =>
                    (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3],
                LengthHeaderFormat.Binary2ByteBigEndian =>
                    (bytes[0] << 8) | bytes[1],
                _ => -1
            };
        }

        private byte[] FormatLength(int length)
        {
            return _format switch
            {
                LengthHeaderFormat.Ascii4Byte =>
                    System.Text.Encoding.ASCII.GetBytes(length.ToString("D4")),
                LengthHeaderFormat.Binary4ByteBigEndian =>
                    new[] { (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length },
                LengthHeaderFormat.Binary2ByteBigEndian =>
                    new[] { (byte)(length >> 8), (byte)length },
                _ => throw new InvalidOperationException($"Unknown format: {_format}")
            };
        }
    }
}

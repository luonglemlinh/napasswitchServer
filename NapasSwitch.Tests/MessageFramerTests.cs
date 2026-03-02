using System.IO;
using System.Net.Sockets;
using server;
using Xunit;

namespace NapasSwitch.Tests;

/// <summary>
/// Tests for MessageFramer — verifies that each length-header format
/// is enforced strictly (no guessing) and round-trips correctly.
/// </summary>
public class MessageFramerTests
{
    [Fact]
    public void Ascii4Byte_ShouldRoundTrip()
    {
        var framer = new MessageFramer(MessageFramer.LengthHeaderFormat.Ascii4Byte);
        byte[] payload = System.Text.Encoding.ASCII.GetBytes("0200TEST");

        using var ms = new MemoryStream();
        var ns = BuildWritableNetworkStream(ms);
        framer.WriteMessage(ns, payload, "test");

        ms.Position = 0;
        var readNs = BuildReadableNetworkStream(ms);
        byte[]? result = framer.ReadMessage(readNs, "test");

        Assert.NotNull(result);
        Assert.Equal(payload, result);
    }

    [Fact]
    public void Binary2Byte_ShouldRoundTrip()
    {
        var framer = new MessageFramer(MessageFramer.LengthHeaderFormat.Binary2ByteBigEndian);
        byte[] payload = new byte[] { 0x30, 0x32, 0x30, 0x30 }; // "0200"

        using var ms = new MemoryStream();
        var ns = BuildWritableNetworkStream(ms);
        framer.WriteMessage(ns, payload, "test");

        ms.Position = 0;
        var readNs = BuildReadableNetworkStream(ms);
        byte[]? result = framer.ReadMessage(readNs, "test");

        Assert.NotNull(result);
        Assert.Equal(payload, result);
    }

    [Fact]
    public void ReadMessage_ShouldReturnNull_OnEmptyStream()
    {
        var framer = new MessageFramer(MessageFramer.LengthHeaderFormat.Ascii4Byte);
        using var ms = new MemoryStream(System.Array.Empty<byte>());
        var ns = BuildReadableNetworkStream(ms);

        byte[]? result = framer.ReadMessage(ns, "test");

        Assert.Null(result);
    }

    [Fact]
    public void ReadMessage_ShouldReturnNull_OnInvalidAsciiLength()
    {
        var framer = new MessageFramer(MessageFramer.LengthHeaderFormat.Ascii4Byte);
        // "XXXX" is not a valid length
        byte[] header = System.Text.Encoding.ASCII.GetBytes("XXXX");
        using var ms = new MemoryStream(header);
        var ns = BuildReadableNetworkStream(ms);

        byte[]? result = framer.ReadMessage(ns, "test");

        Assert.Null(result);
    }

    [Fact]
    public void ReadMessage_ShouldReturnNull_OnOverSizedLength()
    {
        var framer = new MessageFramer(MessageFramer.LengthHeaderFormat.Ascii4Byte);
        // "9999" exceeds MaxMessageLength (2000)
        byte[] header = System.Text.Encoding.ASCII.GetBytes("9999");
        using var ms = new MemoryStream(header);
        var ns = BuildReadableNetworkStream(ms);

        byte[]? result = framer.ReadMessage(ns, "test");

        Assert.Null(result);
    }

    /// <summary>
    /// Helper: wraps a MemoryStream in a NetworkStream-compatible wrapper for writing.
    /// Since NetworkStream requires a Socket, we use a simple adapter.
    /// </summary>
    private static NetworkStream BuildWritableNetworkStream(MemoryStream ms)
    {
        // NetworkStream requires a real socket — use the MemoryStream directly through a helper
        return new FakeNetworkStream(ms);
    }

    private static NetworkStream BuildReadableNetworkStream(MemoryStream ms)
    {
        return new FakeNetworkStream(ms);
    }

    /// <summary>
    /// Minimal NetworkStream stand-in that wraps a MemoryStream.
    /// Only supports the Read/Write/Flush methods used by MessageFramer.
    /// </summary>
    private sealed class FakeNetworkStream : NetworkStream
    {
        private readonly MemoryStream _inner;

        public FakeNetworkStream(MemoryStream inner)
            : base(CreateDummySocket(), ownsSocket: true)
        {
            _inner = inner;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count)
            => _inner.Write(buffer, offset, count);

        public override void Flush() => _inner.Flush();

        private static Socket CreateDummySocket()
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            return s;
        }
    }
}

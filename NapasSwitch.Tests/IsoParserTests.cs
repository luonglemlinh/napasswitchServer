using core.ISO8583;
using core.Models;
using Xunit;

namespace NapasSwitch.Tests;

public class IsoParserTests
{
    private readonly IsoParser _parser = new();

    [Fact]
    public void ParseThenBuild_ShouldProduceIdenticalBytes_ForSimpleMessage()
    {
        // Arrange: build a known message
        var original = new IsoMessage { MessageType = "0200" };
        original.SetField(3, "000000");     // Processing Code (Fixed 6)
        original.SetField(4, "000000010000"); // Amount (Fixed 12)
        original.SetField(11, "123456");    // STAN (Fixed 6)
        original.SetField(7, "0115120000"); // Transmission Date/Time (Fixed 10)

        byte[] builtBytes = _parser.Build(original);

        // Act: parse then rebuild
        var parsed = _parser.Parse(builtBytes);
        byte[] rebuiltBytes = _parser.Build(parsed);

        // Assert: round-trip produces identical bytes
        Assert.Equal(builtBytes, rebuiltBytes);
    }

    [Fact]
    public void ParseThenBuild_ShouldPreserveAllFields()
    {
        // Arrange
        var original = new IsoMessage { MessageType = "0200" };
        original.SetField(3, "000000");
        original.SetField(4, "000000010000");
        original.SetField(7, "0115120000");
        original.SetField(11, "000001");
        original.SetField(12, "120000");
        original.SetField(13, "0115");
        original.SetField(32, "970418");       // LLVAR
        original.SetField(37, "123456789012"); // Fixed 12
        original.SetField(39, "00");           // Fixed 2
        original.SetField(41, "TERM0001");     // Fixed 8
        original.SetField(49, "704");          // Fixed 3

        byte[] builtBytes = _parser.Build(original);

        // Act
        var parsed = _parser.Parse(builtBytes);

        // Assert: every field is preserved
        Assert.Equal("0200", parsed.MessageType);
        Assert.Equal("000000", parsed.GetField(3));
        Assert.Equal("000000010000", parsed.GetField(4));
        Assert.Equal("0115120000", parsed.GetField(7));
        Assert.Equal("000001", parsed.GetField(11));
        Assert.Equal("120000", parsed.GetField(12));
        Assert.Equal("0115", parsed.GetField(13));
        Assert.Equal("970418", parsed.GetField(32));
        Assert.Equal("123456789012", parsed.GetField(37));
        Assert.Equal("00", parsed.GetField(39));
        Assert.Equal("TERM0001", parsed.GetField(41));
        Assert.Equal("704", parsed.GetField(49));
    }

    [Fact]
    public void ParseThenBuild_ShouldRoundTrip_WithVariableLengthFields()
    {
        // Arrange: LLVAR (DE2, DE32) and LLLVAR (DE63) fields
        var original = new IsoMessage { MessageType = "0200" };
        original.SetField(2, "4111111111111111"); // PAN LLVAR
        original.SetField(3, "000000");
        original.SetField(4, "000000005000");
        original.SetField(11, "000002");
        original.SetField(32, "12345678901");     // LLVAR max 11
        original.SetField(63, "TRN00000001");     // LLLVAR

        byte[] builtBytes = _parser.Build(original);
        var parsed = _parser.Parse(builtBytes);
        byte[] rebuiltBytes = _parser.Build(parsed);

        Assert.Equal(builtBytes, rebuiltBytes);
        Assert.Equal("4111111111111111", parsed.GetField(2));
        Assert.Equal("12345678901", parsed.GetField(32));
        Assert.Equal("TRN00000001", parsed.GetField(63));
    }

    [Fact]
    public void ParseThenBuild_ShouldRoundTrip_NetworkManagementMessage()
    {
        // Arrange: 0800 sign-on message
        var original = new IsoMessage { MessageType = "0800" };
        original.SetField(7, "0115120000");
        original.SetField(11, "000001");
        original.SetField(32, "970418");
        original.SetField(70, "001"); // Sign-on

        byte[] builtBytes = _parser.Build(original);
        var parsed = _parser.Parse(builtBytes);
        byte[] rebuiltBytes = _parser.Build(parsed);

        Assert.Equal(builtBytes, rebuiltBytes);
        Assert.Equal("0800", parsed.MessageType);
        Assert.Equal("001", parsed.GetField(70));
    }

    [Fact]
    public void ParseThenBuild_ShouldRoundTrip_WithHexBitmap()
    {
        // Arrange
        var hexParser = new IsoParser();
        hexParser.UseHexBitmap = true;

        var original = new IsoMessage { MessageType = "0200" };
        original.SetField(3, "000000");
        original.SetField(4, "000000010000");
        original.SetField(11, "123456");

        byte[] builtBytes = hexParser.Build(original);
        var parsed = hexParser.Parse(builtBytes);
        byte[] rebuiltBytes = hexParser.Build(parsed);

        Assert.Equal(builtBytes, rebuiltBytes);
    }

    [Fact]
    public void Parse_ShouldThrow_ForTooShortMessage()
    {
        Assert.Throws<ArgumentException>(() => _parser.Parse(new byte[] { 0x30, 0x32 }));
    }

    [Fact]
    public void Build_ShouldThrow_WhenMessageTypeIsEmpty()
    {
        var msg = new IsoMessage();
        Assert.Throws<ArgumentException>(() => _parser.Build(msg));
    }

    [Fact]
    public void Parse_ShouldHandleAsciiHexPinBlock_WithoutShiftingSubsequentFields()
    {
        // 0200 message with DE#52 (16 chars ASCII Hex) and DE#63 (LLLVAR)
        // This simulates an acquirer sending the PIN block in a non-standard length.
        
        var mtiBytes = System.Text.Encoding.ASCII.GetBytes("0200");
        var bitmapBytes = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x02 }; // bits 52 and 63
        var de52Bytes = System.Text.Encoding.ASCII.GetBytes("0612523FEBB9BADA"); // 16 bytes ASCII
        var de63Bytes = System.Text.Encoding.ASCII.GetBytes("011TRN12345678");     // LLLVAR(11) + content
        
        var message = new List<byte>();
        message.AddRange(mtiBytes);
        message.AddRange(bitmapBytes);
        message.AddRange(de52Bytes);
        message.AddRange(de63Bytes);
        
        var parsed = _parser.Parse(message.ToArray());
        
        Assert.Equal("0200", parsed.MessageType);
        Assert.Equal("0612523FEBB9BADA", parsed.GetField(52));
        Assert.Equal("TRN12345678", parsed.GetField(63));
    }
}

using core.Models;
using data;
using Xunit;

namespace NapasSwitch.Tests;

/// <summary>
/// Unit tests for MessageCycleStore.DeriveRoutingFields.
/// Covers ACQ (DE#32), ISS (first 6 of PAN from DE#2 or DE#35), and Sender (MTI-aware).
/// </summary>
public class MessageCycleStoreTests
{
    [Fact]
    public void DeriveRoutingFields_RequestMessage_NoDE39_SenderIsACQ()
    {
        var msg = new IsoMessage { MessageType = "0200" };
        msg.SetField(32, "12345678901");
        msg.SetField(2, "4111111111111111");
        // No DE#39

        var (acq, iss, sender) = MessageCycleStore.DeriveRoutingFields(msg);

        Assert.Equal("12345678901", acq);
        Assert.Equal("411111", iss);
        Assert.Equal("ACQ", sender);
    }

    [Fact]
    public void DeriveRoutingFields_ResponseMessage_DE39Present_MTIEndsIn10_SenderIsISS()
    {
        var msg = new IsoMessage { MessageType = "0210" };
        msg.SetField(32, "12345678901");
        msg.SetField(2, "4111111111111111");
        msg.SetField(39, "00");

        var (acq, iss, sender) = MessageCycleStore.DeriveRoutingFields(msg);

        Assert.Equal("12345678901", acq);
        Assert.Equal("411111", iss);
        Assert.Equal("ISS", sender);
    }

    [Fact]
    public void DeriveRoutingFields_MalformedRequest_DE39Present_MTI0200_SenderIsACQ()
    {
        var msg = new IsoMessage { MessageType = "0200" };
        msg.SetField(32, "ACQ001");
        msg.SetField(2, "4111111111111111");
        msg.SetField(39, "00"); // Misbehaving acquirer sent DE#39 in request

        var (acq, iss, sender) = MessageCycleStore.DeriveRoutingFields(msg);

        Assert.Equal("ACQ001", acq);
        Assert.Equal("411111", iss);
        Assert.Equal("ACQ", sender); // MTI is request, so Sender stays ACQ
    }

    [Fact]
    public void DeriveRoutingFields_PANInDE35Only_NoDE2_ISSIsFirst6OfTrack2()
    {
        var msg = new IsoMessage { MessageType = "0200" };
        msg.SetField(32, "12345678901");
        msg.SetField(35, "4111111111111111D2512"); // Track 2: PAN=EXPIRY...

        var (acq, iss, sender) = MessageCycleStore.DeriveRoutingFields(msg);

        Assert.Equal("12345678901", acq);
        Assert.Equal("411111", iss);
        Assert.Equal("ACQ", sender);
    }

    [Fact]
    public void DeriveRoutingFields_0800Echo_NoPAN_ISSIsNull_NoException()
    {
        var msg = new IsoMessage { MessageType = "0800" };
        msg.SetField(32, "12345678901");
        // No DE#2, no DE#35 — network management

        var (acq, iss, sender) = MessageCycleStore.DeriveRoutingFields(msg);

        Assert.Equal("12345678901", acq);
        Assert.Null(iss);
        Assert.Equal("ACQ", sender);
    }

    [Fact]
    public void DeriveRoutingFields_DE32Absent_ACQIsNull()
    {
        var msg = new IsoMessage { MessageType = "0200" };
        msg.SetField(2, "4111111111111111");
        // No DE#32

        var (acq, iss, sender) = MessageCycleStore.DeriveRoutingFields(msg);

        Assert.Null(acq);
        Assert.Equal("411111", iss);
        Assert.Equal("ACQ", sender);
    }

    [Fact]
    public void DeriveRoutingFields_MTI0430_DE39Present_SenderIsISS()
    {
        var msg = new IsoMessage { MessageType = "0430" };
        msg.SetField(32, "12345678901");
        msg.SetField(39, "00");

        var (_, _, sender) = MessageCycleStore.DeriveRoutingFields(msg);

        Assert.Equal("ISS", sender); // Response MTI (ends in 30)
    }
}
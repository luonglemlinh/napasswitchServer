using core.Helpers;
using core.Models;
using network.Validation;
using Xunit;

namespace NapasSwitch.Tests;

/// <summary>
/// Integration tests for response correlation validation.
/// Verifies that the switch correctly detects mismatches between
/// request and response fields that indicate a routing or data error.
/// </summary>
public class CorrelationValidatorTests
{
    private readonly ResponseCorrelationValidator _validator = new();

    private static IsoMessage BuildRequest(string mti = "0200",
        string stan = "123456", string amount = "000000010000",
        string processingCode = "000000", string pan = "4111111111111111",
        string rrn = "123456789012", string terminalId = "TERM0001",
        string merchantId = "MERCHANT000001")
    {
        var msg = new IsoMessage { MessageType = mti };
        msg.SetField(2, pan);
        msg.SetField(3, processingCode);
        msg.SetField(4, amount);
        msg.SetField(7, "0115120000");
        msg.SetField(11, stan);
        msg.SetField(37, rrn);
        msg.SetField(41, terminalId);
        msg.SetField(42, merchantId);
        return msg;
    }

    private static IsoMessage BuildMatchingResponse(IsoMessage request, string rc = "00")
    {
        var response = new IsoMessage { MessageType = MtiHelper.GetResponseMTI(request.MessageType) };
        foreach (var f in request.Fields)
            response.SetField(f.Key, f.Value);
        response.SetField(39, rc);
        return response;
    }

    [Fact]
    public void MatchingResponse_ShouldPass()
    {
        var request = BuildRequest();
        var response = BuildMatchingResponse(request);

        var result = _validator.ValidateResponseMatchesRequest(request, response);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void StanMismatch_ShouldFail()
    {
        var request = BuildRequest(stan: "111111");
        var response = BuildMatchingResponse(request);
        response.SetField(11, "999999");

        var result = _validator.ValidateResponseMatchesRequest(request, response);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void AmountMismatch_OnPurchase_ShouldFail()
    {
        var request = BuildRequest(mti: "0200", amount: "000000010000");
        var response = BuildMatchingResponse(request);
        response.SetField(4, "000000099999");

        var result = _validator.ValidateResponseMatchesRequest(request, response);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ProcessingCodeMismatch_ShouldFail()
    {
        var request = BuildRequest(processingCode: "000000");
        var response = BuildMatchingResponse(request);
        response.SetField(3, "300000");

        var result = _validator.ValidateResponseMatchesRequest(request, response);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void MtiMismatch_ShouldFail()
    {
        var request = BuildRequest(mti: "0200");
        var response = BuildMatchingResponse(request);
        response.MessageType = "0410"; // Wrong response MTI for 0200

        var result = _validator.ValidateResponseMatchesRequest(request, response);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void TerminalIdMismatch_ShouldFail()
    {
        var request = BuildRequest(terminalId: "TERM0001");
        var response = BuildMatchingResponse(request);
        response.SetField(41, "TERM9999");

        var result = _validator.ValidateResponseMatchesRequest(request, response);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void NetworkManagement_ShouldCorrelate()
    {
        var request = new IsoMessage { MessageType = "0800" };
        request.SetField(7, "0115120000");
        request.SetField(11, "000001");
        request.SetField(70, "001");

        var response = new IsoMessage { MessageType = "0810" };
        response.SetField(7, "0115120000");
        response.SetField(11, "000001");
        response.SetField(70, "001");
        response.SetField(39, "00");

        var result = _validator.ValidateResponseMatchesRequest(request, response);

        Assert.True(result.IsValid);
    }
}

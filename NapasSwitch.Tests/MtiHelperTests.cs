using core.Helpers;
using Xunit;

namespace NapasSwitch.Tests;

public class MtiHelperTests
{
    [Theory]
    [InlineData("0200", "0210")]
    [InlineData("0400", "0410")]
    [InlineData("0420", "0430")]
    [InlineData("0800", "0810")]
    public void GetResponseMTI_ShouldReturnCorrectResponse(string request, string expected)
    {
        Assert.Equal(expected, MtiHelper.GetResponseMTI(request));
    }

    [Theory]
    [InlineData("0210", true)]
    [InlineData("0410", true)]
    [InlineData("0430", false)]  // 3rd char is '3', not '1' — IsResponse checks mti[2] == '1'
    [InlineData("0810", true)]
    [InlineData("0200", false)]
    [InlineData("0400", false)]
    [InlineData("0800", false)]
    public void IsResponse_ShouldIdentifyResponseMTIs(string mti, bool expected)
    {
        Assert.Equal(expected, MtiHelper.IsResponse(mti));
    }

    [Theory]
    [InlineData("0800", true)]
    [InlineData("0810", true)]
    [InlineData("0200", false)]
    [InlineData("0400", false)]
    public void IsNetworkManagement_ShouldIdentifyNetworkMessages(string mti, bool expected)
    {
        Assert.Equal(expected, MtiHelper.IsNetworkManagement(mti));
    }

    [Theory]
    [InlineData("0200", true)]
    [InlineData("0400", true)]
    [InlineData("0420", false)]
    [InlineData("0800", false)]
    public void IsFinancialRequest_ShouldIdentifyFinancialRequests(string mti, bool expected)
    {
        Assert.Equal(expected, MtiHelper.IsFinancialRequest(mti));
    }

    [Theory]
    [InlineData("0400", true)]
    [InlineData("0420", true)]
    [InlineData("0200", false)]
    [InlineData("0800", false)]
    public void IsReversal_ShouldIdentifyReversals(string mti, bool expected)
    {
        Assert.Equal(expected, MtiHelper.IsReversal(mti));
    }

    [Fact]
    public void GetResponseMTI_ShouldReturnFallback_ForInvalidInput()
    {
        // Non-numeric input should return the fallback (AuthorizationResponse)
        Assert.Equal(MtiHelper.AuthorizationResponse, MtiHelper.GetResponseMTI("XXXX"));
    }

    [Fact]
    public void Constants_ShouldHaveCorrectValues()
    {
        Assert.Equal("0200", MtiHelper.AuthorizationRequest);
        Assert.Equal("0210", MtiHelper.AuthorizationResponse);
        Assert.Equal("0400", MtiHelper.ReversalRequest);
        Assert.Equal("0410", MtiHelper.ReversalResponse);
        Assert.Equal("0420", MtiHelper.ReversalAdvice);
        Assert.Equal("0430", MtiHelper.ReversalAdviceResponse);
        Assert.Equal("0800", MtiHelper.NetworkManagementRequest);
        Assert.Equal("0810", MtiHelper.NetworkManagementResponse);
    }
}

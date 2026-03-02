using core.Helpers;
using core.Models;
using Xunit;

namespace NapasSwitch.Tests;

/// <summary>
/// Tests for TransactionTypeHelper — verifies that MTI + Processing Code
/// combinations are correctly classified into transaction types used by
/// the settlement and void/reversal logic.
/// </summary>
public class TransactionTypeTests
{
    [Theory]
    [InlineData("0200", "000000", "PURCHASE")]
    [InlineData("0200", "300000", "BALANCE_INQUIRY")]
    [InlineData("0200", "310000", "BALANCE_INQUIRY")]
    [InlineData("0200", "010000", "CASH_WITHDRAWAL")]
    [InlineData("0200", "200000", "REFUND")]
    [InlineData("0200", "400000", "TRANSFER")]
    public void FinancialTransactions_ShouldBeClassifiedCorrectly(string mti, string pc, string expected)
    {
        Assert.Equal(expected, TransactionTypeHelper.GetTransactionType(mti, pc));
    }

    [Theory]
    [InlineData("0400", null, "REVERSAL")]
    [InlineData("0400", "000000", "REVERSAL")]
    [InlineData("0410", null, "REVERSAL")]
    public void ReversalMessages_ShouldBeReversal(string mti, string? pc, string expected)
    {
        Assert.Equal(expected, TransactionTypeHelper.GetTransactionType(mti, pc));
    }

    [Theory]
    [InlineData("0420", "000000", "VOID")]
    [InlineData("0430", "000000", "VOID")]
    public void VoidAdvice_WithPurchasePC_ShouldBeVoid(string mti, string pc, string expected)
    {
        Assert.Equal(expected, TransactionTypeHelper.GetTransactionType(mti, pc));
    }

    [Theory]
    [InlineData("0420", "200000", "REVERSAL")]
    [InlineData("0420", null, "REVERSAL")]
    public void ReversalAdvice_WithNonPurchasePC_ShouldBeReversal(string mti, string? pc, string expected)
    {
        Assert.Equal(expected, TransactionTypeHelper.GetTransactionType(mti, pc));
    }

    [Theory]
    [InlineData("0800")]
    [InlineData("0810")]
    public void NetworkManagement_ShouldBeClassifiedCorrectly(string mti)
    {
        Assert.Equal("NETWORK_MGMT", TransactionTypeHelper.GetTransactionType(mti, null));
    }

    [Fact]
    public void IsBalanceInquiry_ShouldReturnTrue_ForCode30()
    {
        Assert.True(TransactionTypeHelper.IsBalanceInquiry("300000"));
        Assert.True(TransactionTypeHelper.IsBalanceInquiry("310000"));
    }

    [Fact]
    public void IsBalanceInquiry_ShouldReturnFalse_ForPurchase()
    {
        Assert.False(TransactionTypeHelper.IsBalanceInquiry("000000"));
        Assert.False(TransactionTypeHelper.IsBalanceInquiry(null));
    }

    [Fact]
    public void IsFinancialTransaction_ShouldReturnTrue_For0200_0210()
    {
        Assert.True(TransactionTypeHelper.IsFinancialTransaction("0200"));
        Assert.True(TransactionTypeHelper.IsFinancialTransaction("0210"));
    }

    [Fact]
    public void IsReversal_ShouldReturnTrue_ForReversalMtis()
    {
        Assert.True(TransactionTypeHelper.IsReversal("0400"));
        Assert.True(TransactionTypeHelper.IsReversal("0410"));
        Assert.True(TransactionTypeHelper.IsReversal("0420"));
        Assert.True(TransactionTypeHelper.IsReversal("0430"));
    }

    [Fact]
    public void GetFormattedType_ShouldIncludeMtiAndPC()
    {
        string result = TransactionTypeHelper.GetFormattedTransactionType("0200", "000000");
        Assert.Contains("Purchase", result);
        Assert.Contains("0200", result);
        Assert.Contains("000000", result);
    }
}

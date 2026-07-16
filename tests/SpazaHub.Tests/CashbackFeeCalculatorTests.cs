using SpazaHub.Domain.Enums;
using SpazaHub.Domain.Services;

namespace SpazaHub.Domain.Tests;

public class CashbackFeeCalculatorTests
{
    [Fact]
    public void Quote_DefaultPolicy_HundredRandCashback()
    {
        // The spec's canonical example: cash out R100, fee R10, card charged R110.
        var quote = CashbackFeeCalculator.Quote(100m, 0.10m, FeeRoundingMode.UpToRand);

        Assert.Equal(100m, quote.CashOut);
        Assert.Equal(10m, quote.Fee);
        Assert.Equal(110m, quote.TotalCharged);
    }

    [Theory]
    [InlineData(95, 10.0, 9.50)]
    [InlineData(95.50, 10.0, 9.55)]
    [InlineData(33.33, 10.0, 3.33)]
    public void Quote_ExactMode_KeepsCents(decimal cashOut, decimal ratePercent, decimal expectedFee)
    {
        var quote = CashbackFeeCalculator.Quote(cashOut, ratePercent / 100m, FeeRoundingMode.Exact);

        Assert.Equal(expectedFee, quote.Fee);
        Assert.Equal(cashOut + expectedFee, quote.TotalCharged);
    }

    [Theory]
    [InlineData(95, 10)]
    [InlineData(91, 10)]
    [InlineData(100.01, 11)]
    public void Quote_UpToRandMode_AlwaysRoundsUp(decimal cashOut, decimal expectedFee)
    {
        var quote = CashbackFeeCalculator.Quote(cashOut, 0.10m, FeeRoundingMode.UpToRand);

        Assert.Equal(expectedFee, quote.Fee);
    }

    [Theory]
    [InlineData(94, 9)]
    [InlineData(95, 10)]
    [InlineData(96, 10)]
    public void Quote_NearestRandMode_RoundsHalfUp(decimal cashOut, decimal expectedFee)
    {
        var quote = CashbackFeeCalculator.Quote(cashOut, 0.10m, FeeRoundingMode.NearestRand);

        Assert.Equal(expectedFee, quote.Fee);
    }

    [Fact]
    public void Quote_RejectsNonPositiveCashback()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CashbackFeeCalculator.Quote(0m, 0.10m, FeeRoundingMode.UpToRand));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CashbackFeeCalculator.Quote(-50m, 0.10m, FeeRoundingMode.UpToRand));
    }

    [Fact]
    public void Quote_RejectsNegativeRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CashbackFeeCalculator.Quote(100m, -0.01m, FeeRoundingMode.UpToRand));
    }
}

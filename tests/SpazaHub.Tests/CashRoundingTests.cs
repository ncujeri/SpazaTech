using SpazaHub.Domain.Services;

namespace SpazaHub.Domain.Tests;

public class CashRoundingTests
{
    [Theory]
    [InlineData(12.34, 0.10, 12.30, -0.04)]
    [InlineData(12.35, 0.10, 12.40, 0.05)]
    [InlineData(12.36, 0.10, 12.40, 0.04)]
    [InlineData(12.30, 0.10, 12.30, 0.00)]
    [InlineData(0.04, 0.10, 0.00, -0.04)]
    [InlineData(12.34, 0.05, 12.35, 0.01)]
    public void RoundCash_RoundsToIncrementAndReportsAdjustment(
        decimal amount, decimal increment, decimal expectedRounded, decimal expectedAdjustment)
    {
        var (rounded, adjustment) = CashRounding.RoundCash(amount, increment);

        Assert.Equal(expectedRounded, rounded);
        Assert.Equal(expectedAdjustment, adjustment);
    }

    [Fact]
    public void RoundCash_ZeroIncrementMeansNoRounding()
    {
        var (rounded, adjustment) = CashRounding.RoundCash(12.34m, 0m);

        Assert.Equal(12.34m, rounded);
        Assert.Equal(0m, adjustment);
    }

    [Fact]
    public void RoundCash_AdjustmentAlwaysBalances()
    {
        var (rounded, adjustment) = CashRounding.RoundCash(99.97m, 0.10m);

        Assert.Equal(rounded, 99.97m + adjustment);
    }
}

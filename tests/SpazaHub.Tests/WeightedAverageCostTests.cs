using SpazaHub.Domain.Services;

namespace SpazaHub.Domain.Tests;

public class WeightedAverageCostTests
{
    [Fact]
    public void Apply_BlendsExistingAndReceivedValue()
    {
        // 10 on hand at R5 plus 10 received at R7 = R6 average.
        decimal result = WeightedAverageCost.Apply(10m, 5m, 10m, 7m);

        Assert.Equal(6m, result);
    }

    [Fact]
    public void Apply_UnevenQuantitiesWeightCorrectly()
    {
        // 30 at R4 plus 10 at R8: (120 + 80) / 40 = R5.
        decimal result = WeightedAverageCost.Apply(30m, 4m, 10m, 8m);

        Assert.Equal(5m, result);
    }

    [Fact]
    public void Apply_ZeroOnHand_TakesReceivedCost()
    {
        decimal result = WeightedAverageCost.Apply(0m, 5m, 12m, 7.5m);

        Assert.Equal(7.5m, result);
    }

    [Fact]
    public void Apply_NegativeBookStock_TakesReceivedCost()
    {
        decimal result = WeightedAverageCost.Apply(-3m, 5m, 12m, 7.5m);

        Assert.Equal(7.5m, result);
    }

    [Fact]
    public void Apply_RoundsToFourDecimals()
    {
        decimal result = WeightedAverageCost.Apply(3m, 1m, 3m, 2m);

        Assert.Equal(1.5m, result);

        decimal repeating = WeightedAverageCost.Apply(1m, 1m, 2m, 2m);
        Assert.Equal(1.6667m, repeating);
    }

    [Fact]
    public void Apply_RejectsInvalidInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WeightedAverageCost.Apply(1m, 1m, 0m, 1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => WeightedAverageCost.Apply(1m, 1m, 1m, -1m));
    }
}

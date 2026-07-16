using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;
using SpazaHub.Domain.Services;

namespace SpazaHub.Tests;

public class CreditLedgerTests
{
    private static CreditEntry Entry(CreditEntryType type, decimal amount)
        => new() { CustomerId = Guid.NewGuid(), Type = type, Amount = amount, OccurredAtUtc = DateTime.UtcNow };

    [Fact]
    public void Balance_IsDebitsMinusCredits()
    {
        var entries = new List<CreditEntry>
        {
            Entry(CreditEntryType.Debit, 80m),
            Entry(CreditEntryType.Debit, 45.50m),
            Entry(CreditEntryType.Credit, 50m)
        };

        Assert.Equal(75.50m, CreditLedger.Balance(entries));
    }

    [Fact]
    public void Balance_EmptyLedgerIsZero_OverpaymentGoesNegative()
    {
        Assert.Equal(0m, CreditLedger.Balance([]));
        Assert.Equal(-20m, CreditLedger.Balance([Entry(CreditEntryType.Credit, 20m)]));
    }

    [Theory]
    [InlineData(150, 40, 200, CreditLimitCheck.Ok)]
    [InlineData(150, 60, 200, CreditLimitCheck.OverLimit)]
    [InlineData(150, 5000, 0, CreditLimitCheck.Ok)]
    public void CheckLimit_SoftWarnsOverLimit_ZeroMeansNoLimit(
        decimal balance, decimal add, decimal limit, CreditLimitCheck expected)
    {
        Assert.Equal(expected, CreditLedger.CheckLimit(balance, add, limit));
    }
}

public class ReminderSmsTests
{
    [Fact]
    public void BuildBody_FitsOneGsmSegment()
    {
        string body = ReminderSms.BuildBody("Gogo Dlamini", 145.50m, "Mama Thoko Spaza");

        Assert.True(body.Length <= ReminderSms.MaxGsmSegmentLength);
        Assert.Contains("R145.50", body);
        Assert.Contains("Gogo Dlamini", body);
    }

    [Fact]
    public void BuildBody_IsiZuluTemplate()
    {
        string body = ReminderSms.BuildBody("Sipho", 60m, "KwaMhlanga Shop", "zu");

        Assert.Contains("Sawubona", body);
        Assert.True(body.Length <= 160);
    }

    [Fact]
    public void BuildBody_StripsUcs2FlippingCharactersAndLongNames()
    {
        // Emoji and curly quotes would flip encoding to UCS-2; they must vanish.
        string body = ReminderSms.BuildBody(
            "Nomvula “The Boss” 😊 with an extremely long name indeed",
            99999.99m,
            "A shop with a very very very long painted sign name");

        Assert.True(body.Length <= 160);
        Assert.DoesNotContain('“', body);
        Assert.DoesNotContain('\ud83d', body);
    }

    [Fact]
    public void BuildLink_EncodesBodyIntoSmsUri()
    {
        string link = ReminderSms.BuildLink("+27821234567", "Hello & thanks");

        Assert.StartsWith("sms:+27821234567?body=", link);
        Assert.Contains("Hello%20%26%20thanks", link);
    }
}

public class StockTakeCalculatorTests
{
    private static StockCount Count(string name, decimal expected, decimal counted, decimal cost = 10m)
        => new(Guid.NewGuid(), name, expected, counted, cost);

    [Fact]
    public void Build_MatchingCountsProduceNothing()
    {
        var result = StockTakeCalculator.Build(
            [Count("Bread", 10m, 10m)], null, DateTime.UtcNow);

        Assert.Empty(result.Corrections);
        Assert.False(result.HasVariances);
        Assert.Equal(0m, result.TotalShrinkageValue);
    }

    [Fact]
    public void Build_ShortCountBecomesNegativeCorrectionAndShrinkage()
    {
        var result = StockTakeCalculator.Build(
            [Count("Airtime", 20m, 17m, cost: 45m)], null, DateTime.UtcNow);

        var correction = Assert.Single(result.Corrections);
        Assert.Equal(SpazaHub.Domain.Enums.StockMovementType.StockTakeCorrection, correction.Type);
        Assert.Equal(-3m, correction.Quantity);

        var variance = Assert.Single(result.Variances);
        Assert.Equal(-135m, variance.VarianceValue);
        Assert.Equal(135m, result.TotalShrinkageValue);
    }

    [Fact]
    public void Build_OverCountIsPositiveCorrectionButNotShrinkage()
    {
        var result = StockTakeCalculator.Build(
            [Count("Sugar", 5m, 7m, cost: 40m)], null, DateTime.UtcNow);

        Assert.Equal(2m, Assert.Single(result.Corrections).Quantity);
        Assert.Equal(0m, result.TotalShrinkageValue);
    }
}

public class ReorderAdvisorTests
{
    private static ProductActivity Product(
        string name, decimal onHand, decimal soldInWindow, DateTime? lastSale = null, decimal threshold = 0m)
        => new(Guid.NewGuid(), name, onHand, threshold, soldInWindow, 14, lastSale);

    [Fact]
    public void TripList_SuggestsTopUpToCoverDemand()
    {
        // 28 sold in 14 days = 2 a day; 7 days cover needs 14; 4 on hand -> buy 10.
        var list = ReorderAdvisor.BuildTripList([Product("Bread", 4m, 28m)], daysToCover: 7);

        var suggestion = Assert.Single(list);
        Assert.Equal(10m, suggestion.SuggestedQuantity);
        Assert.Equal(2m, suggestion.UnitsPerDay);
    }

    [Fact]
    public void TripList_SkipsWellStockedAndOrdersByVelocity()
    {
        var list = ReorderAdvisor.BuildTripList(
        [
            Product("Slow", 50m, 7m),
            Product("Fast", 1m, 56m),
            Product("Medium", 2m, 28m)
        ]);

        Assert.Equal(2, list.Count);
        Assert.Equal("Fast", list[0].ProductName);
        Assert.Equal("Medium", list[1].ProductName);
    }

    [Fact]
    public void DeadStock_FlagsUnsoldShelfStockOnly()
    {
        DateTime now = DateTime.UtcNow;
        var dead = ReorderAdvisor.FindDeadStock(
        [
            Product("Never sold", 6m, 0m, lastSale: null),
            Product("Old seller", 3m, 0m, lastSale: now.AddDays(-45)),
            Product("Recent seller", 3m, 5m, lastSale: now.AddDays(-2)),
            Product("Sold out anyway", 0m, 0m, lastSale: null)
        ], now, deadAfterDays: 30);

        Assert.Equal(2, dead.Count);
        Assert.Contains(dead, d => d.ProductName == "Never sold");
        Assert.Contains(dead, d => d.ProductName == "Old seller");
    }
}

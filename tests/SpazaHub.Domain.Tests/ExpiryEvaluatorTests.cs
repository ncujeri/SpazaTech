using SpazaHub.Domain.Services;

namespace SpazaHub.Domain.Tests;

public class ExpiryEvaluatorTests
{
    private static readonly Guid ProductId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 7, 6);

    private static ExpiryBatch Batch(string? expiry, decimal qty, int receivedDaysAgo)
        => new(expiry is null ? null : DateOnly.Parse(expiry), qty,
            Today.ToDateTime(TimeOnly.MinValue).AddDays(-receivedDaysAgo));

    [Fact]
    public void Evaluate_WarnsInsideWindowOnly()
    {
        var batches = new List<ExpiryBatch>
        {
            Batch("2026-07-10", 10m, 1),  // 4 days out: warn
            Batch("2026-08-01", 10m, 0)   // far out: quiet
        };

        var alerts = ExpiryEvaluator.Evaluate(ProductId, 20m, batches, Today, warningDays: 7);

        var alert = Assert.Single(alerts);
        Assert.Equal(new DateOnly(2026, 7, 10), alert.ExpiryDate);
        Assert.Equal(10m, alert.EstimatedUnitsAtRisk);
        Assert.False(alert.IsExpired);
    }

    [Fact]
    public void Evaluate_MarksPastDatesExpired()
    {
        var batches = new List<ExpiryBatch> { Batch("2026-07-01", 5m, 10) };

        var alerts = ExpiryEvaluator.Evaluate(ProductId, 5m, batches, Today, 7);

        Assert.True(Assert.Single(alerts).IsExpired);
    }

    [Fact]
    public void Evaluate_FullySoldOldBatch_NeverWarns()
    {
        // Old dated batch of 10 was received first, then a fresh undated batch of 10.
        // Only 8 remain on hand: rotation means the old 10 are gone.
        var batches = new List<ExpiryBatch>
        {
            Batch("2026-07-07", 10m, 14),
            Batch(null, 10m, 1)
        };

        var alerts = ExpiryEvaluator.Evaluate(ProductId, 8m, batches, Today, 7);

        Assert.Empty(alerts);
    }

    [Fact]
    public void Evaluate_PartiallyRemainingOldBatch_WarnsForTheRemainder()
    {
        // 10 old dated + 10 new undated received; 14 on hand means about 4 old ones left.
        var batches = new List<ExpiryBatch>
        {
            Batch("2026-07-08", 10m, 14),
            Batch(null, 10m, 1)
        };

        var alerts = ExpiryEvaluator.Evaluate(ProductId, 14m, batches, Today, 7);

        Assert.Equal(4m, Assert.Single(alerts).EstimatedUnitsAtRisk);
    }

    [Fact]
    public void Evaluate_NoStockOnHand_IsQuiet()
    {
        var batches = new List<ExpiryBatch> { Batch("2026-07-01", 10m, 10) };

        Assert.Empty(ExpiryEvaluator.Evaluate(ProductId, 0m, batches, Today, 7));
    }

    [Fact]
    public void Evaluate_SameDateBatchesMerge()
    {
        var batches = new List<ExpiryBatch>
        {
            Batch("2026-07-09", 6m, 3),
            Batch("2026-07-09", 4m, 2)
        };

        var alerts = ExpiryEvaluator.Evaluate(ProductId, 10m, batches, Today, 7);

        var alert = Assert.Single(alerts);
        Assert.Equal(10m, alert.EstimatedUnitsAtRisk);
    }

    [Fact]
    public void Evaluate_RejectsNegativeWindow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExpiryEvaluator.Evaluate(ProductId, 1m, [Batch("2026-07-09", 1m, 1)], Today, -1));
    }
}

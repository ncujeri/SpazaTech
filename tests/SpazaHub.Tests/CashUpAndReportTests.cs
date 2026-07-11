using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;
using SpazaHub.Domain.Services;

namespace SpazaHub.Domain.Tests;

public class CashUpAndReportTests
{
    private static CashMovement Move(CashMovementType type, decimal amount)
        => new() { Type = type, Amount = amount, OccurredAtUtc = DateTime.UtcNow };

    [Fact]
    public void ExpectedCash_IsTheSpecFormula()
    {
        // Opening float 200 + cash sales 950 - cashback out 300 - payout 120 - expense 30.
        var movements = new List<CashMovement>
        {
            Move(CashMovementType.OpeningFloat, 200m),
            Move(CashMovementType.CashSale, 600m),
            Move(CashMovementType.CashSale, 350m),
            Move(CashMovementType.CashbackPaid, -300m),
            Move(CashMovementType.Payout, -120m),
            Move(CashMovementType.Expense, -30m)
        };

        decimal expected = CashUpCalculator.ComputeExpectedCash(movements);

        Assert.Equal(700m, expected);
        Assert.Equal(-50m, CashUpCalculator.ComputeVariance(650m, expected));
        Assert.Equal(300m, CashUpCalculator.ComputeCashbackPaidToday(movements));
    }

    [Fact]
    public void DailyReport_SeparatesGoodsMarginFromServiceIncome()
    {
        var tradingDate = new DateOnly(2026, 7, 6);
        var saleId = Guid.NewGuid();

        var sales = new List<Sale> { new() { OccurredAtUtc = DateTime.UtcNow, Total = 100m } };
        var lines = new List<SaleLine>
        {
            new() { SaleId = saleId, Quantity = 4m, UnitPrice = 25m, UnitCostSnapshot = 18m, LineTotal = 100m }
        };
        var payments = new List<SalePayment>
        {
            new() { SaleId = saleId, Method = PaymentMethod.Cash, Amount = 60m },
            new() { SaleId = saleId, Method = PaymentMethod.Card, Amount = 40m }
        };
        var cashbacks = new List<CashbackTransaction>
        {
            new() { CashOutAmount = 100m, FeeAmount = 10m, TotalCharged = 110m }
        };
        var fees = new List<FeeIncome> { new() { Type = FeeIncomeType.Cashback, Amount = 10m } };
        var movements = new List<CashMovement>
        {
            Move(CashMovementType.OpeningFloat, 150m),
            Move(CashMovementType.CashSale, 60m),
            Move(CashMovementType.CashbackPaid, -100m),
            Move(CashMovementType.Payout, -20m)
        };

        var summary = DailyReportCalculator.Build(
            tradingDate, sales, lines, payments, cashbacks, fees, movements);

        Assert.Equal(1, summary.SaleCount);
        Assert.Equal(100m, summary.GoodsRevenue);
        Assert.Equal(72m, summary.GoodsCost);
        Assert.Equal(28m, summary.GrossMargin);

        // Service income sits on its own line, never inside goods margin.
        Assert.Equal(10m, summary.CashbackFeeIncome);
        Assert.Equal(100m, summary.CashbackPaidOut);
        Assert.Equal(1, summary.CashbackCount);

        Assert.Equal(60m, summary.CashTendered);
        Assert.Equal(40m, summary.CardTendered);
        Assert.Equal(20m, summary.PayoutsAndExpenses);
        Assert.Equal(90m, summary.ExpectedCash);
    }

    [Fact]
    public void DailyReport_VoidedSaleNetsToZeroAndIsNotCounted()
    {
        var tradingDate = new DateOnly(2026, 7, 6);
        var originalId = Guid.NewGuid();

        var sales = new List<Sale>
        {
            new() { Id = originalId, Total = 50m },
            new() { Total = -50m, ReversesSaleId = originalId }
        };
        var lines = new List<SaleLine>
        {
            new() { SaleId = originalId, Quantity = 1m, UnitPrice = 50m, UnitCostSnapshot = 30m, LineTotal = 50m },
            new() { Quantity = -1m, UnitPrice = 50m, UnitCostSnapshot = 30m, LineTotal = -50m }
        };

        var summary = DailyReportCalculator.Build(
            tradingDate, sales, lines, [], [], [], []);

        // Only the real sale counts; revenue and margin net to zero.
        Assert.Equal(1, summary.SaleCount);
        Assert.Equal(0m, summary.GoodsRevenue);
        Assert.Equal(0m, summary.GrossMargin);
    }
}

using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Services;

/// <summary>
/// One trading day on a page: goods revenue and margin from the cost snapshots taken at
/// sale time (so history never shifts), tender breakdown, and cashback fees shown as
/// service income on their own line, separate from goods margin.
/// </summary>
public sealed record DailySummary(
    DateOnly TradingDate,
    int SaleCount,
    decimal GoodsRevenue,
    decimal GoodsCost,
    decimal GrossMargin,
    decimal CashbackFeeIncome,
    decimal CashbackPaidOut,
    int CashbackCount,
    decimal CashTendered,
    decimal CardTendered,
    decimal SassaTendered,
    decimal QrTendered,
    decimal StoreCreditTendered,
    decimal PayoutsAndExpenses,
    decimal ExpectedCash);

/// <summary>Builds the daily summary from the trading day's raw local rows.</summary>
public static class DailyReportCalculator
{
    public static DailySummary Build(
        DateOnly tradingDate,
        IReadOnlyList<Sale> sales,
        IReadOnlyList<SaleLine> lines,
        IReadOnlyList<SalePayment> payments,
        IReadOnlyList<CashbackTransaction> cashbacks,
        IReadOnlyList<FeeIncome> feeIncomes,
        IReadOnlyList<CashMovement> cashMovements)
    {
        decimal revenue = lines.Sum(l => l.LineTotal);
        decimal cost = lines.Sum(l => Math.Round(l.Quantity * l.UnitCostSnapshot, 2, MidpointRounding.AwayFromZero));

        decimal TenderTotal(PaymentMethod method)
            => payments.Where(p => p.Method == method).Sum(p => p.Amount);

        decimal payoutsAndExpenses = -cashMovements
            .Where(m => m.Type is CashMovementType.Payout or CashMovementType.Expense)
            .Sum(m => m.Amount);

        return new DailySummary(
            TradingDate: tradingDate,
            SaleCount: sales.Count(s => s.ReversesSaleId == null),
            GoodsRevenue: revenue,
            GoodsCost: cost,
            GrossMargin: revenue - cost,
            CashbackFeeIncome: feeIncomes.Where(f => f.Type == FeeIncomeType.Cashback).Sum(f => f.Amount),
            CashbackPaidOut: cashbacks.Sum(c => c.CashOutAmount),
            CashbackCount: cashbacks.Count,
            CashTendered: TenderTotal(PaymentMethod.Cash),
            CardTendered: TenderTotal(PaymentMethod.Card),
            SassaTendered: TenderTotal(PaymentMethod.SassaCard),
            QrTendered: TenderTotal(PaymentMethod.Qr),
            StoreCreditTendered: TenderTotal(PaymentMethod.StoreCredit),
            PayoutsAndExpenses: payoutsAndExpenses,
            ExpectedCash: CashUpCalculator.ComputeExpectedCash(cashMovements));
    }
}

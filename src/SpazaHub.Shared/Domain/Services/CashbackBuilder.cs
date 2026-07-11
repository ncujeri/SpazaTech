using SpazaHub.Domain.Common;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Services;

/// <summary>Why a cashback request was refused. Shown to the cashier in plain terms.</summary>
public enum CashbackDenialReason
{
    None = 0,
    AmountNotPositive = 1,
    OverPerTransactionLimit = 2,
    OverDailyLimit = 3,
    DrawerFloorBreached = 4,
    CashierNotPermitted = 5,
    InvalidPaymentMethod = 6
}

/// <summary>Shop state the cashback controls check against, all read locally.</summary>
public sealed record CashbackContext(
    decimal FeeRate,
    FeeRoundingMode FeeRounding,
    decimal MaxPerTransaction,
    decimal MaxPerDay,
    decimal OwnerReviewThreshold,
    decimal DrawerCashFloor,
    decimal CashbackAlreadyPaidToday,
    decimal DrawerCashNow,
    bool CashierMayDoCashback);

/// <summary>The full event stream one cashback produces. Append-only, immutable.</summary>
public sealed record CashbackEvents(
    CashbackTransaction Transaction,
    CashMovement DrawerCashOut,
    FeeIncome FeeIncome)
{
    /// <summary>Entities in outbox write order: the transaction first, then its events.</summary>
    public IEnumerable<Entity> InWriteOrder()
    {
        yield return Transaction;
        yield return DrawerCashOut;
        yield return FeeIncome;
    }
}

/// <summary>
/// Builds the till cashback event stream with every control from the spec: the fee is
/// charged on top (customer receives R100, card charged R110), the applied rate is
/// snapshotted, and the theft controls (per-transaction and per-day limits, drawer
/// floor, permission flag, owner review threshold) are enforced before anything is
/// written. Card processing itself happens on the external terminal.
/// </summary>
public static class CashbackBuilder
{
    /// <summary>Checks every control without building anything. None means allowed.</summary>
    public static CashbackDenialReason Check(
        decimal cashOut, PaymentMethod method, CashbackContext context)
    {
        if (cashOut <= 0m)
        {
            return CashbackDenialReason.AmountNotPositive;
        }

        if (method is not (PaymentMethod.Card or PaymentMethod.SassaCard))
        {
            return CashbackDenialReason.InvalidPaymentMethod;
        }

        if (!context.CashierMayDoCashback)
        {
            return CashbackDenialReason.CashierNotPermitted;
        }

        if (cashOut > context.MaxPerTransaction)
        {
            return CashbackDenialReason.OverPerTransactionLimit;
        }

        if (context.CashbackAlreadyPaidToday + cashOut > context.MaxPerDay)
        {
            return CashbackDenialReason.OverDailyLimit;
        }

        if (context.DrawerCashNow - cashOut < context.DrawerCashFloor)
        {
            return CashbackDenialReason.DrawerFloorBreached;
        }

        return CashbackDenialReason.None;
    }

    /// <summary>
    /// Builds the event stream after the controls pass. Throws if they do not; call
    /// Check first to show the cashier a friendly reason.
    /// </summary>
    public static CashbackEvents Build(
        decimal cashOut,
        PaymentMethod method,
        CashbackContext context,
        Guid cashierId,
        DateTime occurredAtUtc,
        Guid? saleId = null,
        Guid? customerId = null)
    {
        var denial = Check(cashOut, method, context);
        if (denial != CashbackDenialReason.None)
        {
            throw new InvalidOperationException($"Cashback refused: {denial}.");
        }

        var quote = CashbackFeeCalculator.Quote(cashOut, context.FeeRate, context.FeeRounding);

        var transaction = new CashbackTransaction
        {
            SaleId = saleId,
            CustomerId = customerId,
            CashOutAmount = quote.CashOut,
            FeeRateApplied = context.FeeRate,
            FeeRoundingApplied = context.FeeRounding,
            FeeAmount = quote.Fee,
            TotalCharged = quote.TotalCharged,
            PaymentMethod = method,
            CashierId = cashierId,
            FlaggedForOwnerReview = cashOut >= context.OwnerReviewThreshold,
            OccurredAtUtc = occurredAtUtc
        };

        var drawerOut = new CashMovement
        {
            Type = CashMovementType.CashbackPaid,
            Amount = -quote.CashOut,
            CashbackTransactionId = transaction.Id,
            CashierId = cashierId,
            OccurredAtUtc = occurredAtUtc
        };

        var feeIncome = new FeeIncome
        {
            Type = FeeIncomeType.Cashback,
            Amount = quote.Fee,
            SourceId = transaction.Id,
            OccurredAtUtc = occurredAtUtc
        };

        return new CashbackEvents(transaction, drawerOut, feeIncome);
    }
}

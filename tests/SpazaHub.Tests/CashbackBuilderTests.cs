using SpazaHub.Domain.Enums;
using SpazaHub.Domain.Services;

namespace SpazaHub.Domain.Tests;

public class CashbackBuilderTests
{
    private static readonly Guid CashierId = Guid.NewGuid();

    private static CashbackContext DefaultContext(
        decimal alreadyToday = 0m, decimal drawerCash = 1000m, bool permitted = true)
        => new(
            FeeRate: 0.10m,
            FeeRounding: FeeRoundingMode.UpToRand,
            MaxPerTransaction: 500m,
            MaxPerDay: 2000m,
            OwnerReviewThreshold: 200m,
            DrawerCashFloor: 100m,
            CashbackAlreadyPaidToday: alreadyToday,
            DrawerCashNow: drawerCash,
            CashierMayDoCashback: permitted);

    [Fact]
    public void Build_ProducesFullEventStreamWithSnapshot()
    {
        var events = CashbackBuilder.Build(
            100m, PaymentMethod.Card, DefaultContext(), CashierId, DateTime.UtcNow);

        // The spec's canonical numbers: cash out 100, fee 10, card charged 110.
        Assert.Equal(100m, events.Transaction.CashOutAmount);
        Assert.Equal(10m, events.Transaction.FeeAmount);
        Assert.Equal(110m, events.Transaction.TotalCharged);
        Assert.Equal(0.10m, events.Transaction.FeeRateApplied);
        Assert.Equal(FeeRoundingMode.UpToRand, events.Transaction.FeeRoundingApplied);
        Assert.Equal(CashierId, events.Transaction.CashierId);

        Assert.Equal(-100m, events.DrawerCashOut.Amount);
        Assert.Equal(CashMovementType.CashbackPaid, events.DrawerCashOut.Type);
        Assert.Equal(events.Transaction.Id, events.DrawerCashOut.CashbackTransactionId);

        Assert.Equal(10m, events.FeeIncome.Amount);
        Assert.Equal(FeeIncomeType.Cashback, events.FeeIncome.Type);
        Assert.Equal(events.Transaction.Id, events.FeeIncome.SourceId);
    }

    [Fact]
    public void Build_StandaloneCashback_HasNoSale()
    {
        var events = CashbackBuilder.Build(
            50m, PaymentMethod.SassaCard, DefaultContext(), CashierId, DateTime.UtcNow);

        Assert.Null(events.Transaction.SaleId);
    }

    [Theory]
    [InlineData(199, false)]
    [InlineData(200, true)]
    [InlineData(350, true)]
    public void Build_FlagsOwnerReviewAtThreshold(decimal amount, bool expectFlag)
    {
        var events = CashbackBuilder.Build(
            amount, PaymentMethod.Card, DefaultContext(), CashierId, DateTime.UtcNow);

        Assert.Equal(expectFlag, events.Transaction.FlaggedForOwnerReview);
    }

    [Fact]
    public void Check_EnforcesEveryControl()
    {
        Assert.Equal(CashbackDenialReason.AmountNotPositive,
            CashbackBuilder.Check(0m, PaymentMethod.Card, DefaultContext()));

        Assert.Equal(CashbackDenialReason.InvalidPaymentMethod,
            CashbackBuilder.Check(50m, PaymentMethod.Cash, DefaultContext()));

        Assert.Equal(CashbackDenialReason.CashierNotPermitted,
            CashbackBuilder.Check(50m, PaymentMethod.Card, DefaultContext(permitted: false)));

        Assert.Equal(CashbackDenialReason.OverPerTransactionLimit,
            CashbackBuilder.Check(501m, PaymentMethod.Card, DefaultContext()));

        // 1900 already paid today; 150 more would cross the 2000 daily cap.
        Assert.Equal(CashbackDenialReason.OverDailyLimit,
            CashbackBuilder.Check(150m, PaymentMethod.Card, DefaultContext(alreadyToday: 1900m)));

        // Drawer holds 250, floor is 100: paying out 200 would leave 50.
        Assert.Equal(CashbackDenialReason.DrawerFloorBreached,
            CashbackBuilder.Check(200m, PaymentMethod.Card, DefaultContext(drawerCash: 250m)));

        Assert.Equal(CashbackDenialReason.None,
            CashbackBuilder.Check(100m, PaymentMethod.Card, DefaultContext()));
    }

    [Fact]
    public void Build_RefusesWhenAnyControlFails()
    {
        Assert.Throws<InvalidOperationException>(() => CashbackBuilder.Build(
            600m, PaymentMethod.Card, DefaultContext(), CashierId, DateTime.UtcNow));
    }
}

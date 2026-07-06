using SpazaHub.Domain.Enums;
using SpazaHub.Domain.Services;

namespace SpazaHub.Domain.Tests;

public class SaleBuilderTests
{
    private static readonly Guid DeviceId = Guid.NewGuid();
    private static readonly Guid CashierId = Guid.NewGuid();
    private static readonly DateTime Now = DateTime.UtcNow;

    private static CartLine Line(decimal qty, decimal price, decimal cost = 0m, Guid? productId = null)
        => new(productId ?? Guid.NewGuid(), "Item", qty, price, cost);

    [Fact]
    public void Build_CashSale_RoundsCashAndRecordsAdjustment()
    {
        // Total 12.34, cash only: rounded to 12.30 at a 10c increment, adjustment -0.04.
        var result = SaleBuilder.Build(
            [Line(2, 6.17m)], [], 0.10m, DeviceId, CashierId, Now);

        Assert.Equal(12.34m, result.Sale.Total);
        Assert.Equal(-0.04m, result.Sale.CashRoundingAdjustment);

        var cash = Assert.Single(result.Payments);
        Assert.Equal(PaymentMethod.Cash, cash.Method);
        Assert.Equal(12.30m, cash.Amount);

        Assert.NotNull(result.CashMovement);
        Assert.Equal(12.30m, result.CashMovement!.Amount);
        Assert.Equal(CashMovementType.CashSale, result.CashMovement.Type);
    }

    [Fact]
    public void Build_MultiTender_CashIsRoundedRemainder()
    {
        // Total 50, card 30, cash remainder 20 (no rounding needed).
        var result = SaleBuilder.Build(
            [Line(1, 50m)],
            [new TenderInput(PaymentMethod.Card, 30m)],
            0.10m, DeviceId, CashierId, Now);

        Assert.Equal(2, result.Payments.Count);
        Assert.Equal(30m, result.Payments.Single(p => p.Method == PaymentMethod.Card).Amount);
        Assert.Equal(20m, result.Payments.Single(p => p.Method == PaymentMethod.Cash).Amount);
    }

    [Fact]
    public void Build_FullyNonCashSale_HasNoCashMovement()
    {
        var result = SaleBuilder.Build(
            [Line(1, 45m)],
            [new TenderInput(PaymentMethod.SassaCard, 45m)],
            0.10m, DeviceId, CashierId, Now);

        Assert.Null(result.CashMovement);
        Assert.DoesNotContain(result.Payments, p => p.Method == PaymentMethod.Cash);
        Assert.Equal(0m, result.Sale.CashRoundingAdjustment);
    }

    [Fact]
    public void Build_SnapshotsUnitCostOntoLines()
    {
        var result = SaleBuilder.Build(
            [Line(3, 10m, cost: 7.25m)], [], 0.10m, DeviceId, CashierId, Now);

        Assert.Equal(7.25m, Assert.Single(result.Lines).UnitCostSnapshot);
    }

    [Fact]
    public void Build_CreatesNegativeStockMovementsPerProductLine()
    {
        var productId = Guid.NewGuid();
        var result = SaleBuilder.Build(
            [
                Line(2, 10m, productId: productId),
                new CartLine(null, "Loose sweets", 1m, 5m, 0m)
            ],
            [], 0.10m, DeviceId, CashierId, Now);

        // The loose amount line has no product and moves no stock.
        var movement = Assert.Single(result.StockMovements);
        Assert.Equal(productId, movement.ProductId);
        Assert.Equal(-2m, movement.Quantity);
        Assert.Equal(StockMovementType.Sale, movement.Type);
        Assert.Equal(result.Sale.Id, movement.SaleId);
    }

    [Fact]
    public void Build_WriteOrder_ParentsBeforeChildren()
    {
        var result = SaleBuilder.Build(
            [Line(1, 10m)], [new TenderInput(PaymentMethod.Qr, 5m)], 0.10m, DeviceId, CashierId, Now);

        var order = result.InWriteOrder().ToList();
        Assert.Same(result.Sale, order[0]);
        Assert.True(order.Count >= 4);
    }

    [Fact]
    public void Build_RejectsEmptyCartOverpaymentAndBadInput()
    {
        Assert.Throws<InvalidOperationException>(
            () => SaleBuilder.Build([], [], 0.10m, DeviceId, CashierId, Now));

        Assert.Throws<InvalidOperationException>(
            () => SaleBuilder.Build([Line(0, 10m)], [], 0.10m, DeviceId, CashierId, Now));

        Assert.Throws<InvalidOperationException>(
            () => SaleBuilder.Build(
                [Line(1, 10m)], [new TenderInput(PaymentMethod.Card, 11m)], 0.10m, DeviceId, CashierId, Now));

        // Cash must never be entered as a tender: it is always the remainder.
        Assert.Throws<InvalidOperationException>(
            () => SaleBuilder.Build(
                [Line(1, 10m)], [new TenderInput(PaymentMethod.Cash, 10m)], 0.10m, DeviceId, CashierId, Now));
    }

    [Fact]
    public void BuildFullReversal_NegatesEverythingAndRefundsRoundedCash()
    {
        var productId = Guid.NewGuid();
        var original = SaleBuilder.Build(
            [Line(2, 6.17m, cost: 4m, productId: productId)],
            [], 0.10m, DeviceId, CashierId, Now);

        var reversal = SaleBuilder.BuildFullReversal(
            original.Sale, original.Lines.ToList(), original.Payments.ToList(),
            DeviceId, CashierId, Now.AddMinutes(5));

        Assert.Equal(original.Sale.Id, reversal.Sale.ReversesSaleId);
        Assert.Equal(-12.34m, reversal.Sale.Total);
        Assert.Equal(0.04m, reversal.Sale.CashRoundingAdjustment);

        // The refund matches the 12.30 the customer actually paid, not the raw total.
        Assert.Equal(-12.30m, reversal.Payments.Single(p => p.Method == PaymentMethod.Cash).Amount);
        Assert.Equal(-12.30m, reversal.CashMovement!.Amount);
        Assert.Equal(CashMovementType.CashRefund, reversal.CashMovement.Type);

        // Stock returns to the shelf.
        Assert.Equal(2m, Assert.Single(reversal.StockMovements).Quantity);

        // Cost snapshots carry over so margin reports net to zero.
        Assert.Equal(4m, Assert.Single(reversal.Lines).UnitCostSnapshot);
    }

    [Fact]
    public void BuildFullReversal_OfAVoid_IsRejected()
    {
        var original = SaleBuilder.Build([Line(1, 10m)], [], 0.10m, DeviceId, CashierId, Now);
        var reversal = SaleBuilder.BuildFullReversal(
            original.Sale, original.Lines.ToList(), original.Payments.ToList(), DeviceId, CashierId, Now);

        Assert.Throws<InvalidOperationException>(
            () => SaleBuilder.BuildFullReversal(
                reversal.Sale, reversal.Lines.ToList(), reversal.Payments.ToList(), DeviceId, CashierId, Now));
    }

    [Fact]
    public void BuildFullReversal_MultiTender_RefundsEachMethod()
    {
        var original = SaleBuilder.Build(
            [Line(1, 100m)],
            [new TenderInput(PaymentMethod.Card, 60m)],
            0.10m, DeviceId, CashierId, Now);

        var reversal = SaleBuilder.BuildFullReversal(
            original.Sale, original.Lines.ToList(), original.Payments.ToList(), DeviceId, CashierId, Now);

        Assert.Equal(-60m, reversal.Payments.Single(p => p.Method == PaymentMethod.Card).Amount);
        Assert.Equal(-40m, reversal.Payments.Single(p => p.Method == PaymentMethod.Cash).Amount);
    }
}

using SpazaHub.Domain.Common;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Services;

/// <summary>One cart line as rung up at the counter.</summary>
public sealed record CartLine(
    Guid? ProductId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal UnitCost);

/// <summary>One non-cash tender entered by the cashier. Cash is always the remainder.</summary>
public sealed record TenderInput(PaymentMethod Method, decimal Amount);

/// <summary>How much of one original sale line the customer is bringing back.</summary>
public sealed record ReturnSelection(SaleLine OriginalLine, decimal ReturnQuantity);

/// <summary>
/// Everything one completed sale produces, in the exact order it must be written
/// locally and pushed to the server (parents before children).
/// </summary>
public sealed record CompletedSale(
    Sale Sale,
    IReadOnlyList<SaleLine> Lines,
    IReadOnlyList<SalePayment> Payments,
    IReadOnlyList<StockMovement> StockMovements,
    CashMovement? CashMovement)
{
    /// <summary>Entities in outbox write order: referential parents first.</summary>
    public IEnumerable<Entity> InWriteOrder()
    {
        yield return Sale;
        foreach (var line in Lines)
        {
            yield return line;
        }

        foreach (var payment in Payments)
        {
            yield return payment;
        }

        foreach (var movement in StockMovements)
        {
            yield return movement;
        }

        if (CashMovement is not null)
        {
            yield return CashMovement;
        }
    }
}

/// <summary>
/// Assembles an immutable sale from cart lines and tenders: snapshots unit costs so
/// historical margins never shift, rounds the cash portion to the shop's increment with
/// the difference recorded explicitly, and derives the stock and cash movements.
/// Pure logic, no persistence: a sale completes with zero network dependency.
/// </summary>
public static class SaleBuilder
{
    /// <summary>
    /// Builds a completed sale. Non-cash tenders are taken as entered; the cash portion
    /// is the rounded remainder. Pass includeCash false for fully non-cash sales.
    /// </summary>
    /// <exception cref="InvalidOperationException">On empty carts, non-positive lines, or overpaying non-cash tenders.</exception>
    public static CompletedSale Build(
        IReadOnlyList<CartLine> cart,
        IReadOnlyList<TenderInput> nonCashTenders,
        decimal cashRoundingIncrement,
        Guid deviceId,
        Guid? cashierId,
        DateTime occurredAtUtc,
        Guid? reversesSaleId = null)
    {
        if (cart.Count == 0)
        {
            throw new InvalidOperationException("Cannot complete an empty sale.");
        }

        bool isReversal = reversesSaleId is not null;

        foreach (var line in cart)
        {
            bool validQuantity = isReversal ? line.Quantity != 0m : line.Quantity > 0m;
            if (!validQuantity || line.UnitPrice < 0m)
            {
                throw new InvalidOperationException($"Invalid quantity or price on '{line.Description}'.");
            }
        }

        if (nonCashTenders.Any(t => t.Method == PaymentMethod.Cash))
        {
            throw new InvalidOperationException("Cash is computed as the remainder, not entered as a tender.");
        }

        if (!isReversal && nonCashTenders.Any(t => t.Amount <= 0m))
        {
            throw new InvalidOperationException("Tender amounts must be positive.");
        }

        decimal total = cart.Sum(l => Math.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero));
        decimal nonCashTotal = nonCashTenders.Sum(t => t.Amount);

        if (!isReversal && nonCashTotal > total)
        {
            throw new InvalidOperationException("Non-cash tenders exceed the sale total.");
        }

        decimal cashDue = total - nonCashTotal;
        (decimal roundedCash, decimal adjustment) = cashDue != 0m
            ? CashRounding.RoundCash(cashDue, cashRoundingIncrement)
            : (0m, 0m);

        var sale = new Sale
        {
            DeviceId = deviceId,
            CashierId = cashierId,
            OccurredAtUtc = occurredAtUtc,
            Total = total,
            CashRoundingAdjustment = adjustment,
            ReversesSaleId = reversesSaleId
        };

        var lines = cart.Select(l => new SaleLine
        {
            SaleId = sale.Id,
            ProductId = l.ProductId,
            Description = l.Description,
            Quantity = l.Quantity,
            UnitPrice = l.UnitPrice,
            UnitCostSnapshot = l.UnitCost,
            LineTotal = Math.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero)
        }).ToList();

        var payments = nonCashTenders
            .Select(t => new SalePayment { SaleId = sale.Id, Method = t.Method, Amount = t.Amount })
            .ToList();

        if (roundedCash != 0m)
        {
            payments.Add(new SalePayment { SaleId = sale.Id, Method = PaymentMethod.Cash, Amount = roundedCash });
        }

        var stockMovements = cart
            .Where(l => l.ProductId is not null)
            .Select(l => new StockMovement
            {
                ProductId = l.ProductId!.Value,
                Type = StockMovementType.Sale,
                Quantity = -l.Quantity,
                SaleId = sale.Id,
                CashierId = cashierId,
                OccurredAtUtc = occurredAtUtc
            })
            .ToList();

        CashMovement? cashMovement = roundedCash != 0m
            ? new CashMovement
            {
                Type = roundedCash > 0m ? CashMovementType.CashSale : CashMovementType.CashRefund,
                Amount = roundedCash,
                SaleId = sale.Id,
                CashierId = cashierId,
                OccurredAtUtc = occurredAtUtc
            }
            : null;

        return new CompletedSale(sale, lines, payments, stockMovements, cashMovement);
    }

    /// <summary>
    /// Builds the compensating sale that voids an original in full: negated lines and
    /// tenders referencing the original via ReversesSaleId. Stock returns to the shelf
    /// and cash leaves the drawer as a refund.
    /// </summary>
    public static CompletedSale BuildFullReversal(
        Sale original,
        IReadOnlyList<SaleLine> originalLines,
        IReadOnlyList<SalePayment> originalPayments,
        Guid deviceId,
        Guid? cashierId,
        DateTime occurredAtUtc)
    {
        if (original.ReversesSaleId is not null)
        {
            throw new InvalidOperationException("Cannot void a void; ring the goods again instead.");
        }

        var reversedCart = originalLines
            .Select(l => new CartLine(l.ProductId, l.Description, -l.Quantity, l.UnitPrice, l.UnitCostSnapshot))
            .ToList();

        var reversedNonCash = originalPayments
            .Where(p => p.Method != PaymentMethod.Cash)
            .Select(p => new TenderInput(p.Method, -p.Amount))
            .ToList();

        // No re-rounding on a reversal: the refund must mirror the original rounded
        // cash tender exactly, or the drawer drifts by the rounding cents.
        var reversal = Build(
            reversedCart, reversedNonCash, cashRoundingIncrement: 0m,
            deviceId, cashierId, occurredAtUtc, original.Id);

        var originalCash = originalPayments.FirstOrDefault(p => p.Method == PaymentMethod.Cash);
        var reversalCash = reversal.Payments.FirstOrDefault(p => p.Method == PaymentMethod.Cash);

        if (originalCash is not null && reversalCash is not null)
        {
            reversalCash.Amount = -originalCash.Amount;
            reversal.Sale.CashRoundingAdjustment = -original.CashRoundingAdjustment;
            if (reversal.CashMovement is not null)
            {
                reversal.CashMovement.Amount = -originalCash.Amount;
            }
        }

        // Link every reversal line to the original it returns.
        for (int i = 0; i < reversal.Lines.Count && i < originalLines.Count; i++)
        {
            reversal.Lines[i].ReversesSaleLineId = originalLines[i].Id;
        }

        return reversal;
    }

    /// <summary>
    /// Builds a partial return: only the selected lines and quantities come back. The
    /// refund goes out by one method (cash from the drawer, or a book credit / card
    /// reversal), because splitting a partial refund across the original tenders the way
    /// a full void does is more than a counter needs. Stock returns to the shelf and the
    /// cost snapshots carry over so margins net out for the returned portion.
    /// </summary>
    public static CompletedSale BuildPartialReversal(
        Sale original,
        IReadOnlyList<ReturnSelection> selections,
        PaymentMethod refundMethod,
        decimal cashRoundingIncrement,
        Guid deviceId,
        Guid? cashierId,
        DateTime occurredAtUtc)
    {
        if (original.ReversesSaleId is not null)
        {
            throw new InvalidOperationException("Cannot return against a void; ring the goods again instead.");
        }

        if (selections.Count == 0)
        {
            throw new InvalidOperationException("Choose at least one item to return.");
        }

        foreach (var selection in selections)
        {
            if (selection.ReturnQuantity <= 0m || selection.ReturnQuantity > selection.OriginalLine.Quantity)
            {
                throw new InvalidOperationException(
                    $"Return quantity for '{selection.OriginalLine.Description}' must be between 0 and what was sold.");
            }
        }

        var reversedCart = selections
            .Select(s => new CartLine(
                s.OriginalLine.ProductId, s.OriginalLine.Description,
                -s.ReturnQuantity, s.OriginalLine.UnitPrice, s.OriginalLine.UnitCostSnapshot))
            .ToList();

        decimal returnedValue = reversedCart
            .Sum(l => Math.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero));

        // Cash refunds round to the shop increment; any other method reverses the exact value.
        var nonCash = refundMethod == PaymentMethod.Cash
            ? new List<TenderInput>()
            : [new TenderInput(refundMethod, returnedValue)];

        var reversal = Build(
            reversedCart, nonCash,
            refundMethod == PaymentMethod.Cash ? cashRoundingIncrement : 0m,
            deviceId, cashierId, occurredAtUtc, original.Id);

        for (int i = 0; i < reversal.Lines.Count && i < selections.Count; i++)
        {
            reversal.Lines[i].ReversesSaleLineId = selections[i].OriginalLine.Id;
        }

        return reversal;
    }
}

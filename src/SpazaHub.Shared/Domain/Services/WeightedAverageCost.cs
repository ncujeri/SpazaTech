namespace SpazaHub.Domain.Services;

/// <summary>
/// Maintains the weighted average cost per product. Applied on every GoodsReceived
/// movement. FIFO layers are deliberately not implemented.
/// </summary>
public static class WeightedAverageCost
{
    /// <summary>
    /// Returns the new weighted average cost after receiving stock.
    /// If on-hand quantity is zero or negative, the received cost becomes the new average
    /// (negative book stock is a data correction case, not a costing signal).
    /// Result is rounded to 4 decimal places to keep unit costs stable.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when receivedQuantity is not positive or receivedUnitCost is negative.
    /// </exception>
    public static decimal Apply(
        decimal onHandQuantity,
        decimal currentAverageCost,
        decimal receivedQuantity,
        decimal receivedUnitCost)
    {
        if (receivedQuantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(receivedQuantity), "Received quantity must be positive.");
        }

        if (receivedUnitCost < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(receivedUnitCost), "Received unit cost cannot be negative.");
        }

        if (onHandQuantity <= 0m)
        {
            return Math.Round(receivedUnitCost, 4, MidpointRounding.AwayFromZero);
        }

        decimal totalValue = (onHandQuantity * currentAverageCost) + (receivedQuantity * receivedUnitCost);
        decimal totalQuantity = onHandQuantity + receivedQuantity;
        return Math.Round(totalValue / totalQuantity, 4, MidpointRounding.AwayFromZero);
    }
}

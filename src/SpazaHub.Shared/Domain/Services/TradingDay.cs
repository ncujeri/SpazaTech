namespace SpazaHub.Domain.Services;

/// <summary>
/// Trading day boundary logic. The day rolls at a tenant-configurable hour of shop
/// local time (default 04:00), not at midnight: a 02:00 sale belongs to the previous
/// trading day. South African shops run on SAST (UTC+2, no daylight saving).
/// </summary>
public static class TradingDay
{
    public static readonly TimeSpan SouthAfricaUtcOffset = TimeSpan.FromHours(2);

    /// <summary>Trading date that a UTC instant falls into.</summary>
    public static DateOnly GetTradingDate(DateTime utc, int rollHour, TimeSpan? utcOffset = null)
    {
        ValidateRollHour(rollHour);
        DateTime local = utc + (utcOffset ?? SouthAfricaUtcOffset);
        return DateOnly.FromDateTime(local.AddHours(-rollHour));
    }

    /// <summary>UTC window [start, end) covered by a trading date.</summary>
    public static (DateTime StartUtc, DateTime EndUtc) GetUtcWindow(
        DateOnly tradingDate, int rollHour, TimeSpan? utcOffset = null)
    {
        ValidateRollHour(rollHour);
        TimeSpan offset = utcOffset ?? SouthAfricaUtcOffset;
        DateTime localStart = tradingDate.ToDateTime(TimeOnly.MinValue).AddHours(rollHour);
        DateTime startUtc = localStart - offset;
        return (startUtc, startUtc.AddDays(1));
    }

    private static void ValidateRollHour(int rollHour)
    {
        if (rollHour is < 0 or > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(rollHour), "Roll hour must be 0 to 23.");
        }
    }
}

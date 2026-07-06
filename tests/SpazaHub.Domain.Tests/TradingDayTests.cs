using SpazaHub.Domain.Services;

namespace SpazaHub.Domain.Tests;

public class TradingDayTests
{
    [Theory]
    // 03:59 SAST on 5 July belongs to the 4 July trading day (roll at 04:00).
    [InlineData("2026-07-05T01:59:00Z", 4, "2026-07-04")]
    // 04:00 SAST exactly starts the new trading day.
    [InlineData("2026-07-05T02:00:00Z", 4, "2026-07-05")]
    // A normal mid-morning sale.
    [InlineData("2026-07-05T08:00:00Z", 4, "2026-07-05")]
    // Late evening still belongs to the same trading day.
    [InlineData("2026-07-05T21:30:00Z", 4, "2026-07-05")]
    // Roll at midnight behaves like calendar days in local time.
    [InlineData("2026-07-05T21:59:00Z", 0, "2026-07-05")]
    [InlineData("2026-07-05T22:00:00Z", 0, "2026-07-06")]
    public void GetTradingDate_RespectsRollHourInLocalTime(string utcText, int rollHour, string expected)
    {
        DateTime utc = DateTime.Parse(utcText, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

        DateOnly result = TradingDay.GetTradingDate(utc, rollHour);

        Assert.Equal(DateOnly.Parse(expected), result);
    }

    [Fact]
    public void GetUtcWindow_CoversExactlyOneDayAndRoundTrips()
    {
        var tradingDate = new DateOnly(2026, 7, 5);
        var (startUtc, endUtc) = TradingDay.GetUtcWindow(tradingDate, rollHour: 4);

        Assert.Equal(TimeSpan.FromDays(1), endUtc - startUtc);
        // 04:00 SAST is 02:00 UTC.
        Assert.Equal(new DateTime(2026, 7, 5, 2, 0, 0), startUtc);

        Assert.Equal(tradingDate, TradingDay.GetTradingDate(startUtc, 4));
        Assert.Equal(tradingDate, TradingDay.GetTradingDate(endUtc.AddSeconds(-1), 4));
        Assert.NotEqual(tradingDate, TradingDay.GetTradingDate(endUtc, 4));
    }

    [Fact]
    public void GetTradingDate_RejectsInvalidRollHour()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TradingDay.GetTradingDate(DateTime.UtcNow, 24));
    }
}

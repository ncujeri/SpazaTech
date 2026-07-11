using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SpazaHub.Client.Data;
using SpazaHub.Client.Services;
using SpazaHub.Client.Sync;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;
using SpazaHub.Domain.Services;

namespace SpazaHub.Sync.Tests;

/// <summary>
/// A whole trading day through the real client services: open the drawer, sell,
/// cash back, pay a supplier, count, and reconcile to zero variance.
/// </summary>
public class TradingDayFlowTests : IDisposable
{
    private sealed class StubJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => throw new NotSupportedException();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => throw new NotSupportedException();
    }

    private sealed class TestContextFactory : IDbContextFactory<ClientDbContext>, IDisposable
    {
        private readonly SqliteConnection _connection;

        public TestContextFactory()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
        }

        public ClientDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<ClientDbContext>().UseSqlite(_connection).Options);

        public void Dispose() => _connection.Dispose();
    }

    private readonly TestContextFactory _factory = new();
    private readonly LocalStore _store;
    private readonly PosService _pos;
    private readonly InventoryService _inventory;
    private readonly CashbackService _cashback;
    private readonly CashUpService _cashUp;
    private readonly ReportService _reports;

    public TradingDayFlowTests()
    {
        var persistence = new OpfsDbPersistence(new StubJsRuntime(), NullLogger<OpfsDbPersistence>.Instance);
        _store = new LocalStore(_factory, persistence, NullLogger<LocalStore>.Instance);
        _pos = new PosService(_store, _factory);
        _inventory = new InventoryService(_store, _factory);
        _cashback = new CashbackService(_store, _factory);
        _cashUp = new CashUpService(_store, _factory);
        _reports = new ReportService(_factory);

        _store.InitializeAsync(Guid.NewGuid(), Guid.NewGuid()).GetAwaiter().GetResult();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task FullTradingDay_ReconcilesToZeroVariance()
    {
        // Open the drawer with a R200 float.
        await _cashUp.StartDayAsync(200m);

        // Stock and sell: two cash sales of R50 each.
        var product = new Product { Name = "Airtime voucher", SellPrice = 50m };
        await _inventory.SaveProductAsync(product);
        await _inventory.ReceiveGoodsAsync(product.Id, 10m, 42m);

        for (int i = 0; i < 2; i++)
        {
            await _pos.CompleteSaleAsync([new CartLine(product.Id, product.Name, 1m, 50m, 42m)], []);
        }

        // One R100 cashback on card: drawer pays 100, card charged 110.
        var events = await _cashback.CompleteCashbackAsync(100m, PaymentMethod.Card);
        Assert.Equal(110m, events.Transaction.TotalCharged);

        // Pay the bread supplier R80 from the drawer.
        await _cashUp.RecordCashOutAsync(80m, CashMovementType.Payout, "Bread supplier");

        // Drawer should hold 200 + 100 - 100 - 80 = 120.
        Assert.Equal(120m, await _cashUp.GetDrawerCashAsync());

        // Count exactly that and sign off: zero variance.
        var cashUp = await _cashUp.CompleteCashUpAsync(120m);
        Assert.Equal(120m, cashUp.ExpectedCash);
        Assert.Equal(0m, cashUp.Variance);
        Assert.NotNull(cashUp.CashierSignedAtUtc);

        var signed = await _cashUp.OwnerSignAsync(cashUp.TradingDate);
        Assert.NotNull(signed.OwnerSignedAtUtc);
    }

    [Fact]
    public async Task ShortDrawer_ShowsNegativeVariance()
    {
        await _cashUp.StartDayAsync(100m);

        var cashUp = await _cashUp.CompleteCashUpAsync(90m);

        Assert.Equal(100m, cashUp.ExpectedCash);
        Assert.Equal(-10m, cashUp.Variance);
    }

    [Fact]
    public async Task Cashback_BlockedByDrawerFloor()
    {
        // Default floor is R100; the drawer only holds R150.
        await _cashUp.StartDayAsync(150m);

        var denial = await _cashback.CheckAsync(100m, PaymentMethod.Card);
        Assert.Equal(CashbackDenialReason.DrawerFloorBreached, denial);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _cashback.CompleteCashbackAsync(100m, PaymentMethod.Card));
    }

    [Fact]
    public async Task Cashback_BlockedOverDailyLimit()
    {
        // Big float so the floor is never the binding constraint.
        await _cashUp.StartDayAsync(5000m);

        // Default per-day cap is 2000: four 500s are fine, the fifth is refused.
        for (int i = 0; i < 4; i++)
        {
            await _cashback.CompleteCashbackAsync(500m, PaymentMethod.Card);
        }

        var denial = await _cashback.CheckAsync(500m, PaymentMethod.Card);
        Assert.Equal(CashbackDenialReason.OverDailyLimit, denial);
    }

    [Fact]
    public async Task Cashback_WritesEventStreamToOutbox()
    {
        await _cashUp.StartDayAsync(1000m);
        var before = (await _store.GetPendingItemsAsync(500)).Count;

        await _cashback.CompleteCashbackAsync(150m, PaymentMethod.SassaCard);

        var pending = await _store.GetPendingItemsAsync(500);
        Assert.Equal(before + 3, pending.Count);
        Assert.Contains(pending, i => i.EntityType == nameof(CashbackTransaction));
        Assert.Contains(pending, i => i.EntityType == nameof(CashMovement));
        Assert.Contains(pending, i => i.EntityType == nameof(FeeIncome));

        // Review flag set: 150 is at or above the default 200 threshold? No: below.
        await using var db = _factory.CreateDbContext();
        var tx = await db.CashbackTransactions.SingleAsync();
        Assert.False(tx.FlaggedForOwnerReview);
        Assert.Equal(0.10m, tx.FeeRateApplied);
    }

    [Fact]
    public async Task DailyReport_TellsGoodsFromServiceIncome()
    {
        await _cashUp.StartDayAsync(300m);

        var product = new Product { Name = "Bread", SellPrice = 20m };
        await _inventory.SaveProductAsync(product);
        await _inventory.ReceiveGoodsAsync(product.Id, 10m, 15m);
        await _pos.CompleteSaleAsync([new CartLine(product.Id, "Bread", 2m, 20m, 15m)], []);

        await _cashback.CompleteCashbackAsync(100m, PaymentMethod.Card);
        await _cashUp.RecordCashOutAsync(50m, CashMovementType.Expense, "Electricity");

        var tradingDate = await _cashUp.GetCurrentTradingDateAsync();
        var summary = await _reports.GetDailySummaryAsync(tradingDate);

        Assert.Equal(1, summary.SaleCount);
        Assert.Equal(40m, summary.GoodsRevenue);
        Assert.Equal(30m, summary.GoodsCost);
        Assert.Equal(10m, summary.GrossMargin);
        Assert.Equal(10m, summary.CashbackFeeIncome);
        Assert.Equal(100m, summary.CashbackPaidOut);
        Assert.Equal(40m, summary.CashTendered);
        Assert.Equal(50m, summary.PayoutsAndExpenses);
        // Drawer: 300 float + 40 cash - 100 cashback - 50 expense = 190.
        Assert.Equal(190m, summary.ExpectedCash);
    }

    [Fact]
    public async Task StartDay_Twice_IsRefused()
    {
        await _cashUp.StartDayAsync(100m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _cashUp.StartDayAsync(100m));
    }
}

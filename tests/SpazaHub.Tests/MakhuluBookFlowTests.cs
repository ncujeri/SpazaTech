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

namespace SpazaHub.Tests;

/// <summary>
/// Makhulu Book and stock take flows through the real client services: the ledger,
/// the drawer, the outbox, and the POS on-book sale all working together offline.
/// </summary>
public class MakhuluBookFlowTests : IDisposable
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
    private readonly CustomerService _book;
    private readonly PosService _pos;
    private readonly InventoryService _inventory;
    private readonly StockTakeService _stockTake;

    public MakhuluBookFlowTests()
    {
        var persistence = new OpfsDbPersistence(new StubJsRuntime(), NullLogger<OpfsDbPersistence>.Instance);
        _store = new LocalStore(_factory, persistence, NullLogger<LocalStore>.Instance);
        _book = new CustomerService(_store, _factory);
        _pos = new PosService(_store, _factory, new CashUpService(_store, _factory));
        _inventory = new InventoryService(_store, _factory);
        _stockTake = new StockTakeService(_store, _factory);

        _store.InitializeAsync(Guid.NewGuid(), Guid.NewGuid(), "Mama Thoko Spaza").GetAwaiter().GetResult();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Customer> AddCustomerAsync(
        string name, string? phone = null, bool consent = false, decimal limit = 0m)
    {
        var customer = new Customer
        {
            Name = name, Phone = phone, ReminderConsent = consent, CreditLimit = limit
        };
        await _book.SaveCustomerAsync(customer);
        return customer;
    }

    [Fact]
    public async Task ChargeAndPayment_DeriveTheRightBalance_AndCashHitsTheDrawer()
    {
        var gogo = await AddCustomerAsync("Gogo Dlamini");

        await _book.ChargeToBookAsync(gogo.Id, 80m);
        await _book.ChargeToBookAsync(gogo.Id, 45.50m);
        Assert.Equal(125.50m, await _book.GetBalanceAsync(gogo.Id));

        await _book.ReceivePaymentAsync(gogo.Id, 50m);
        Assert.Equal(75.50m, await _book.GetBalanceAsync(gogo.Id));

        await using var db = _factory.CreateDbContext();
        var drawerIn = await db.CashMovements.SingleAsync(m => m.Type == CashMovementType.CreditPaymentReceived);
        Assert.Equal(50m, drawerIn.Amount);
    }

    [Fact]
    public async Task PosSaleOnBook_WritesStoreCreditPaymentAndLinkedDebit()
    {
        var customer = await AddCustomerAsync("Sipho");
        var product = new Product { Name = "Bread", SellPrice = 18.50m };
        await _inventory.SaveProductAsync(product);
        await _inventory.ReceiveGoodsAsync(product.Id, 10m, 14m);

        var (completed, _) = await _pos.CompleteSaleOnBookAsync(
            [new CartLine(product.Id, "Bread", 2m, 18.50m, 14m)], customer.Id);

        await using var db = _factory.CreateDbContext();

        var payment = await db.SalePayments.SingleAsync();
        Assert.Equal(PaymentMethod.StoreCredit, payment.Method);
        Assert.Equal(37m, payment.Amount);

        // No cash moved: the drawer stays honest.
        Assert.Equal(0, await db.CashMovements.CountAsync());

        var debit = await db.CreditEntries.SingleAsync();
        Assert.Equal(CreditEntryType.Debit, debit.Type);
        Assert.Equal(37m, debit.Amount);
        Assert.Equal(completed.Sale.Id, debit.SaleId);

        Assert.Equal(37m, await _book.GetBalanceAsync(customer.Id));

        // Stock still moved like any sale.
        Assert.Equal(8m, (await db.Products.SingleAsync()).CachedQuantity);
    }

    [Fact]
    public async Task ReturnOnBookSale_CreditsTheLedgerBackDown()
    {
        var customer = await AddCustomerAsync("Sipho");
        var product = new Product { Name = "Bread", SellPrice = 18.50m };
        await _inventory.SaveProductAsync(product);
        await _inventory.ReceiveGoodsAsync(product.Id, 10m, 14m);

        var (completed, _) = await _pos.CompleteSaleOnBookAsync(
            [new CartLine(product.Id, "Bread", 2m, 18.50m, 14m)], customer.Id);
        Assert.Equal(37m, await _book.GetBalanceAsync(customer.Id));

        // Return one loaf onto the book: no cash moves, the balance halves.
        var lineId = completed.Lines.Single().Id;
        await _pos.ReturnItemsAsync(
            completed.Sale.Id, [new ReturnRequest(lineId, 1m)], PaymentMethod.StoreCredit);

        Assert.Equal(18.50m, await _book.GetBalanceAsync(customer.Id));

        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.CashMovements.CountAsync());
        Assert.Equal(9m, (await db.Products.SingleAsync()).CachedQuantity);
    }

    [Fact]
    public async Task VoidOnBookSale_ClearsTheWholeDebt()
    {
        var customer = await AddCustomerAsync("Nomsa");
        var product = new Product { Name = "Sugar 2kg", SellPrice = 40m };
        await _inventory.SaveProductAsync(product);
        await _inventory.ReceiveGoodsAsync(product.Id, 6m, 30m);

        var (completed, _) = await _pos.CompleteSaleOnBookAsync(
            [new CartLine(product.Id, "Sugar 2kg", 1m, 40m, 30m)], customer.Id);
        Assert.Equal(40m, await _book.GetBalanceAsync(customer.Id));

        await _pos.VoidSaleAsync(completed.Sale.Id);

        // Whole debt cleared, no cash refund, stock back.
        Assert.Equal(0m, await _book.GetBalanceAsync(customer.Id));
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.CashMovements.CountAsync());
        Assert.Equal(6m, (await db.Products.SingleAsync()).CachedQuantity);
    }

    [Fact]
    public async Task LimitCheck_WarnsOverLimit()
    {
        var customer = await AddCustomerAsync("Thembi", limit: 100m);
        await _book.ChargeToBookAsync(customer.Id, 90m);

        Assert.Equal(CreditLimitCheck.OverLimit, await _book.CheckLimitAsync(customer.Id, 20m));
        Assert.Equal(CreditLimitCheck.Ok, await _book.CheckLimitAsync(customer.Id, 10m));
    }

    [Fact]
    public async Task ReminderLink_RequiresPhoneConsentAndDebt_AndLogsTheMessage()
    {
        var noPhone = await AddCustomerAsync("No Phone", phone: null, consent: true);
        await _book.ChargeToBookAsync(noPhone.Id, 50m);
        Assert.Null(await _book.BuildReminderLinkAsync(noPhone.Id));

        var noConsent = await AddCustomerAsync("No Consent", phone: "+27821111111", consent: false);
        await _book.ChargeToBookAsync(noConsent.Id, 50m);
        Assert.Null(await _book.BuildReminderLinkAsync(noConsent.Id));

        var paidUp = await AddCustomerAsync("Paid Up", phone: "+27822222222", consent: true);
        Assert.Null(await _book.BuildReminderLinkAsync(paidUp.Id));

        var debtor = await AddCustomerAsync("Gogo", phone: "+27823333333", consent: true);
        await _book.ChargeToBookAsync(debtor.Id, 145.50m);

        string? link = await _book.BuildReminderLinkAsync(debtor.Id);
        Assert.NotNull(link);
        Assert.StartsWith("sms:+27823333333?body=", link);
        Assert.Contains("145.50", Uri.UnescapeDataString(link!));
        Assert.Contains("Mama Thoko Spaza", Uri.UnescapeDataString(link!));

        await using var db = _factory.CreateDbContext();
        var message = await db.CustomerMessages.SingleAsync();
        Assert.Equal(MessageChannel.OwnerPhoneSms, message.Channel);
        Assert.Equal(debtor.Id, message.CustomerId);
        Assert.True(message.Body.Length <= 160);
    }

    [Fact]
    public async Task ConsentTimestamp_CapturedOnGrantAndClearedOnWithdrawal()
    {
        var customer = await AddCustomerAsync("Zanele", phone: "+27824444444", consent: true);

        await using (var db = _factory.CreateDbContext())
        {
            Assert.NotNull((await db.Customers.SingleAsync()).ConsentCapturedAtUtc);
        }

        customer.ReminderConsent = false;
        await _book.SaveCustomerAsync(customer);

        await using (var db = _factory.CreateDbContext())
        {
            Assert.Null((await db.Customers.SingleAsync()).ConsentCapturedAtUtc);
        }
    }

    [Fact]
    public async Task StockTake_WritesCorrectionsAndValuesShrinkage()
    {
        var bread = new Product { Name = "Bread", SellPrice = 18.50m };
        var sugar = new Product { Name = "Sugar", SellPrice = 52m };
        await _inventory.SaveProductAsync(bread);
        await _inventory.SaveProductAsync(sugar);
        await _inventory.ReceiveGoodsAsync(bread.Id, 10m, 14m);
        await _inventory.ReceiveGoodsAsync(sugar.Id, 10m, 40m);

        var sheet = await _stockTake.GetCountSheetAsync();
        // Ordered by shelf value: sugar (400) before bread (140).
        Assert.Equal("Sugar", sheet[0].ProductName);

        var result = await _stockTake.SubmitAsync(
        [
            new StockCount(bread.Id, "Bread", 10m, 10m, 14m),
            new StockCount(sugar.Id, "Sugar", 10m, 7m, 40m)
        ]);

        Assert.Equal(120m, result.TotalShrinkageValue);

        await using var db = _factory.CreateDbContext();
        var correction = await db.StockMovements
            .SingleAsync(m => m.Type == StockMovementType.StockTakeCorrection);
        Assert.Equal(-3m, correction.Quantity);
        Assert.Equal(7m, (await db.Products.SingleAsync(p => p.Id == sugar.Id)).CachedQuantity);

        // Corrections travel to the server like any movement.
        var pending = await _store.GetPendingItemsAsync(500);
        Assert.Contains(pending, i => i.EntityType == nameof(StockMovement)
            && i.PayloadJson.Contains("Stock take"));
    }
}

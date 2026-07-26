using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SpazaHub.Api.Identity;
using SpazaHub.Domain.Common;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;
using SpazaHub.Shared.Auth;

namespace SpazaHub.Api.Persistence;

/// <summary>
/// Development-only seed of one fully populated spaza shop: catalog, stock, cashiers,
/// Makhulu Book customers, and a fortnight of trading. Runs against whichever database
/// Database:Provider points at (SqlServer localdb by default). Idempotent: it does
/// nothing if the demo shop already exists, so it is safe to run repeatedly.
///
/// Trigger it with:  dotnet run --project src/SpazaHub.Api -- seed
///
/// There is no ambient tenant when seeding, so every ITenantOwned row sets TenantId
/// explicitly (the TenantStampInterceptor demands it outside an authenticated request).
/// </summary>
public static class DbSeeder
{
    /// <summary>Owner login: phone +27821234567, OTP is printed by the dev SMS provider. Owner PIN 4321.</summary>
    private const string OwnerPhone = "+27821234567";
    private const string OwnerPin = "4321";

    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbSeeder");

        // Bring the schema up to date first: Migrate for SQL Server, EnsureCreated for Sqlite.
        if (db.Database.IsSqlite())
        {
            await db.Database.EnsureCreatedAsync();
        }
        else
        {
            await db.Database.MigrateAsync();
        }

        // Tenant is not tenant-owned, so no query filter hides it: a plain existence check.
        if (await db.Tenants.AnyAsync(t => t.OwnerPhone == OwnerPhone))
        {
            logger.LogInformation("Demo shop already present; skipping seed.");
            return;
        }

        // A fixed anchor so the fortnight of history lands on stable, recent dates.
        DateTime now = DateTime.UtcNow;
        DateTime day0 = now.Date.AddDays(-13).AddHours(6); // 14 trading days back, opening at 06:00 UTC
        var rng = new Random(20260726);

        var tenant = new Tenant
        {
            Id = GuidV7.NewGuid(),
            ShopName = "Emzini Spaza",
            OwnerPhone = OwnerPhone,
            CreatedAtUtc = day0,
            IsActive = true
        };
        Guid tid = tenant.Id;
        db.Tenants.Add(tenant);

        db.TenantConfigs.Add(new TenantConfig
        {
            Id = GuidV7.NewGuid(),
            TenantId = tid,
            OwnerPinHash = PinHasher.Hash(OwnerPin),
            UpdatedAtUtc = day0
        });

        var wallet = new TenantWallet
        {
            Id = GuidV7.NewGuid(),
            TenantId = tid,
            Balance = 0m,
            UpdatedAtUtc = day0
        };
        db.TenantWallets.Add(wallet);

        // Owner Identity account. Mirrors AuthService: username is the E.164 phone.
        var owner = new ApplicationUser
        {
            Id = GuidV7.NewGuid(),
            UserName = OwnerPhone,
            PhoneNumber = OwnerPhone,
            PhoneNumberConfirmed = true,
            TenantId = tid,
            CreatedAtUtc = day0
        };
        var created = await userManager.CreateAsync(owner);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                "Could not create demo owner: " + string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        // Devices: the owner's phone and a counter tablet.
        var ownerDevice = new Device
        {
            Id = GuidV7.NewGuid(),
            TenantId = tid,
            Name = "Owner Phone",
            RegisteredAtUtc = day0,
            LastSeenAtUtc = now
        };
        var tabletDevice = new Device
        {
            Id = GuidV7.NewGuid(),
            TenantId = tid,
            Name = "Counter Tablet",
            RegisteredAtUtc = day0,
            LastSeenAtUtc = now
        };
        db.Devices.AddRange(ownerDevice, tabletDevice);

        // Cashiers. PINs are noted here for the demo; only hashes are stored.
        var nomsa = new Cashier
        {
            Id = GuidV7.NewGuid(),
            TenantId = tid,
            Name = "Nomsa",
            PinHash = PinHasher.Hash("1111"), // PIN 1111
            CanDoCashback = true,
            CreatedAtUtc = day0,
            UpdatedAtUtc = day0
        };
        var sipho = new Cashier
        {
            Id = GuidV7.NewGuid(),
            TenantId = tid,
            Name = "Sipho",
            PinHash = PinHasher.Hash("2222"), // PIN 2222
            CanDoCashback = false,
            CreatedAtUtc = day0,
            UpdatedAtUtc = day0
        };
        db.Cashiers.AddRange(nomsa, sipho);
        var cashiers = new[] { nomsa, sipho };

        // Catalog: name, barcode, cost, sell, opening qty, low-stock threshold.
        (string Name, string? Barcode, decimal Cost, decimal Sell, decimal Qty, decimal Low)[] catalog =
        [
            ("White Bread 700g",        "6001234500017", 12.50m, 16.00m,  30m, 6m),
            ("Fresh Milk 1L",           "6001234500024", 15.00m, 19.50m,  24m, 6m),
            ("Iwisa Maize Meal 5kg",    "6001234500031", 45.00m, 59.90m,  20m, 4m),
            ("Coca-Cola 2L",            "6001234500048", 18.00m, 24.00m,  36m, 8m),
            ("Sunlight Soap 175g",      "6001234500055",  8.00m, 11.50m,  40m, 8m),
            ("White Sugar 2kg",         "6001234500062", 32.00m, 42.00m,  18m, 4m),
            ("Eggs 6-pack",             "6001234500079", 14.00m, 19.00m,  25m, 6m),
            ("Tastic Rice 2kg",         "6001234500086", 30.00m, 39.90m,  16m, 4m),
            ("Sunfoil Cooking Oil 750ml","6001234500093",22.00m, 29.90m,  20m, 4m),
            ("Simba Chips 125g",        "6001234500109",  9.50m, 14.00m,  48m, 12m),
            ("Niknaks 55g",             "6001234500116",  4.50m,  7.00m,  60m, 12m),
            ("Amasi 2L",                "6001234500123", 24.00m, 31.00m,  15m, 4m),
            ("Joko Tea 26s",            "6001234500130", 26.00m, 34.00m,  12m, 3m),
            ("Lion Matches 10pk",       "6001234500147",  6.00m,  9.00m,  30m, 6m),
            ("Candles 6pk",             "6001234500154", 15.00m, 21.00m,  18m, 4m),
            ("Koo Baked Beans 410g",    "6001234500161", 11.00m, 15.50m,  30m, 6m),
            ("Lucky Star Pilchards 400g","6001234500178",20.00m, 27.00m,  22m, 4m),
            ("Vaseline 100ml",          "6001234500185", 18.00m, 25.00m,  14m, 3m),
            ("Maq Washing Powder 1kg",  "6001234500192", 28.00m, 37.00m,  16m, 4m),
            ("Russian Sausage each",    null,             7.00m, 12.00m,  40m, 8m),
        ];

        var products = new List<Product>();
        foreach (var c in catalog)
        {
            var p = new Product
            {
                Id = GuidV7.NewGuid(),
                TenantId = tid,
                Name = c.Name,
                Barcode = c.Barcode,
                SellPrice = c.Sell,
                WeightedAverageCost = c.Cost,
                CachedQuantity = c.Qty,
                LowStockThreshold = c.Low,
                IsActive = true,
                CreatedAtUtc = day0,
                UpdatedAtUtc = day0
            };
            products.Add(p);
            db.Products.Add(p);

            // Opening stock arrives as a goods-received batch, some with a best-before date.
            db.StockMovements.Add(new StockMovement
            {
                Id = GuidV7.NewGuid(),
                TenantId = tid,
                ProductId = p.Id,
                Type = StockMovementType.GoodsReceived,
                Quantity = c.Qty,
                UnitCost = c.Cost,
                ExpiryDate = IsPerishable(c.Name) ? DateOnly.FromDateTime(day0.AddDays(rng.Next(10, 60))) : null,
                Note = "Opening stock",
                OccurredAtUtc = day0
            });
        }

        // Makhulu Book customers. Phone is optional by design; some are name-only.
        var customers = new[]
        {
            NewCustomer(tid, day0, "Gogo Dlamini", "Gogo", "+27831112233", 500m, consent: true),
            NewCustomer(tid, day0, "Themba Nkosi", "Bra T", "+27842223344", 300m, consent: true),
            NewCustomer(tid, day0, "Nomvula Sithole", "Sisi", "+27853334455", 200m, consent: false),
            NewCustomer(tid, day0, "Mama Joyce", null, null, 400m, consent: false),
            NewCustomer(tid, day0, "Kagiso Mokoena", "Bhuti K", null, 250m, consent: true),
        };
        db.Customers.AddRange(customers);

        // Opening float for the drawer on the first trading day.
        db.CashMovements.Add(new CashMovement
        {
            Id = GuidV7.NewGuid(),
            TenantId = tid,
            Type = CashMovementType.OpeningFloat,
            Amount = 300m,
            CashierId = nomsa.Id,
            Note = "Opening float",
            OccurredAtUtc = day0
        });

        // A fortnight of trading. Each day gets a handful of sales; some go on the book.
        for (int day = 0; day < 14; day++)
        {
            DateTime tradingDay = day0.AddDays(day);
            int salesToday = rng.Next(3, 8);

            for (int s = 0; s < salesToday; s++)
            {
                DateTime at = tradingDay.AddHours(rng.Next(1, 12)).AddMinutes(rng.Next(0, 60));
                var cashier = cashiers[rng.Next(cashiers.Length)];
                Guid deviceId = rng.Next(4) == 0 ? ownerDevice.Id : tabletDevice.Id;

                var sale = new Sale
                {
                    Id = GuidV7.NewGuid(),
                    TenantId = tid,
                    DeviceId = deviceId,
                    CashierId = cashier.Id,
                    OccurredAtUtc = at
                };

                int lineCount = rng.Next(1, 5);
                decimal total = 0m;
                var picked = new HashSet<int>();
                for (int l = 0; l < lineCount; l++)
                {
                    int idx = rng.Next(products.Count);
                    if (!picked.Add(idx))
                    {
                        continue;
                    }

                    var product = products[idx];
                    decimal qty = rng.Next(1, 4);
                    decimal lineTotal = qty * product.SellPrice;
                    total += lineTotal;

                    sale.Lines.Add(new SaleLine
                    {
                        Id = GuidV7.NewGuid(),
                        TenantId = tid,
                        SaleId = sale.Id,
                        ProductId = product.Id,
                        Description = product.Name,
                        Quantity = qty,
                        UnitPrice = product.SellPrice,
                        UnitCostSnapshot = product.WeightedAverageCost,
                        LineTotal = lineTotal
                    });

                    product.CachedQuantity -= qty;

                    db.StockMovements.Add(new StockMovement
                    {
                        Id = GuidV7.NewGuid(),
                        TenantId = tid,
                        ProductId = product.Id,
                        Type = StockMovementType.Sale,
                        Quantity = -qty,
                        SaleId = sale.Id,
                        CashierId = cashier.Id,
                        OccurredAtUtc = at
                    });
                }

                if (sale.Lines.Count == 0)
                {
                    continue;
                }

                // Roughly one sale in six goes on the Makhulu Book; the rest are cash or card.
                int roll = rng.Next(6);
                if (roll == 0)
                {
                    var customer = customers[rng.Next(customers.Length)];
                    sale.Total = total;
                    sale.Payments.Add(new SalePayment
                    {
                        Id = GuidV7.NewGuid(),
                        TenantId = tid,
                        SaleId = sale.Id,
                        Method = PaymentMethod.StoreCredit,
                        Amount = total
                    });

                    db.CreditEntries.Add(new CreditEntry
                    {
                        Id = GuidV7.NewGuid(),
                        TenantId = tid,
                        CustomerId = customer.Id,
                        Type = CreditEntryType.Debit,
                        Amount = total,
                        SaleId = sale.Id,
                        CashierId = cashier.Id,
                        Note = "Goods on credit",
                        OccurredAtUtc = at
                    });
                }
                else if (roll == 1)
                {
                    // Card / SASSA card tender: no cash drawer impact.
                    var method = rng.Next(2) == 0 ? PaymentMethod.Card : PaymentMethod.SassaCard;
                    sale.Total = total;
                    sale.Payments.Add(new SalePayment
                    {
                        Id = GuidV7.NewGuid(),
                        TenantId = tid,
                        SaleId = sale.Id,
                        Method = method,
                        Amount = total
                    });
                }
                else
                {
                    // Cash: round to the configured 10c increment and record the drawer movement.
                    decimal rounded = RoundToIncrement(total, 0.10m);
                    sale.Total = total;
                    sale.CashRoundingAdjustment = rounded - total;
                    sale.Payments.Add(new SalePayment
                    {
                        Id = GuidV7.NewGuid(),
                        TenantId = tid,
                        SaleId = sale.Id,
                        Method = PaymentMethod.Cash,
                        Amount = rounded
                    });

                    db.CashMovements.Add(new CashMovement
                    {
                        Id = GuidV7.NewGuid(),
                        TenantId = tid,
                        Type = CashMovementType.CashSale,
                        Amount = rounded,
                        SaleId = sale.Id,
                        CashierId = cashier.Id,
                        OccurredAtUtc = at
                    });
                }

                db.Sales.Add(sale);
            }
        }

        // A couple of credit repayments landing in the drawer partway through the fortnight.
        AddCreditRepayment(db, tid, customers[0], nomsa.Id, day0.AddDays(6).AddHours(10), 150m);
        AddCreditRepayment(db, tid, customers[1], sipho.Id, day0.AddDays(9).AddHours(15), 100m);

        // Wallet: an airtime float top-up and a small SMS-reminder spend.
        db.WalletMovements.Add(new WalletMovement
        {
            Id = GuidV7.NewGuid(),
            TenantId = tid,
            Amount = 500m,
            Note = "Airtime float top-up",
            OccurredAtUtc = day0.AddDays(1)
        });
        db.WalletMovements.Add(new WalletMovement
        {
            Id = GuidV7.NewGuid(),
            TenantId = tid,
            Amount = -12m,
            Note = "Payment reminder SMS",
            OccurredAtUtc = day0.AddDays(8)
        });
        wallet.Balance = 488m;
        wallet.UpdatedAtUtc = day0.AddDays(8);

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Seeded '{Shop}' ({Tenant}): {Products} products, {Cashiers} cashiers, {Customers} customers, {Sales} sales.",
            tenant.ShopName, tid, products.Count, cashiers.Length, customers.Length,
            await db.Sales.IgnoreQueryFilters().CountAsync(x => x.TenantId == tid));
    }

    private static Customer NewCustomer(
        Guid tid, DateTime day0, string name, string? nickname, string? phone, decimal creditLimit, bool consent) =>
        new()
        {
            Id = GuidV7.NewGuid(),
            TenantId = tid,
            Name = name,
            Nickname = nickname,
            Phone = phone,
            ReminderConsent = consent && phone is not null,
            ConsentCapturedAtUtc = consent && phone is not null ? day0 : null,
            CreditLimit = creditLimit,
            IsActive = true,
            CreatedAtUtc = day0,
            UpdatedAtUtc = day0
        };

    private static void AddCreditRepayment(
        AppDbContext db, Guid tid, Customer customer, Guid cashierId, DateTime at, decimal amount)
    {
        db.CreditEntries.Add(new CreditEntry
        {
            Id = GuidV7.NewGuid(),
            TenantId = tid,
            CustomerId = customer.Id,
            Type = CreditEntryType.Credit,
            Amount = amount,
            CashierId = cashierId,
            Note = "Payment received",
            OccurredAtUtc = at
        });

        db.CashMovements.Add(new CashMovement
        {
            Id = GuidV7.NewGuid(),
            TenantId = tid,
            Type = CashMovementType.CreditPaymentReceived,
            Amount = amount,
            CashierId = cashierId,
            Note = $"Book payment: {customer.Name}",
            OccurredAtUtc = at
        });
    }

    private static decimal RoundToIncrement(decimal value, decimal increment) =>
        Math.Round(value / increment, MidpointRounding.AwayFromZero) * increment;

    private static bool IsPerishable(string name) =>
        name.Contains("Milk") || name.Contains("Bread") || name.Contains("Amasi")
        || name.Contains("Eggs") || name.Contains("Russian");
}

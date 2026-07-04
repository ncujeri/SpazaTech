using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SpazaHub.Application.Common.Interfaces;
using SpazaHub.Domain.Common;
using SpazaHub.Domain.Entities;
using SpazaHub.Infrastructure.Identity;

namespace SpazaHub.Infrastructure.Persistence;

/// <summary>
/// Single shared multi-tenant database. Every entity implementing ITenantOwned gets one
/// generically applied global query filter on TenantId; there is no per-entity filter
/// duplication. TenantId stamping on insert is done by TenantStampInterceptor.
/// </summary>
public class AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly ITenantProvider _tenantProvider;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantProvider tenantProvider)
        : base(options)
    {
        _tenantProvider = tenantProvider;
    }

    /// <summary>
    /// Tenant used by the global query filter. Guid.Empty (matching no rows) when the
    /// request carries no tenant context. Referenced through the context instance so EF
    /// evaluates it per query, not once at model build.
    /// </summary>
    public Guid CurrentTenantId => _tenantProvider.HasTenant ? _tenantProvider.TenantId : Guid.Empty;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantConfig> TenantConfigs => Set<TenantConfig>();
    public DbSet<TenantWallet> TenantWallets => Set<TenantWallet>();
    public DbSet<WalletMovement> WalletMovements => Set<WalletMovement>();
    public DbSet<Cashier> Cashiers => Set<Cashier>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleLine> SaleLines => Set<SaleLine>();
    public DbSet<SalePayment> SalePayments => Set<SalePayment>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CreditEntry> CreditEntries => Set<CreditEntry>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<CashUp> CashUps => Set<CashUp>();
    public DbSet<CashbackTransaction> CashbackTransactions => Set<CashbackTransaction>();
    public DbSet<FeeIncome> FeeIncomes => Set<FeeIncome>();
    public DbSet<CustomerMessage> CustomerMessages => Set<CustomerMessage>();
    public DbSet<VasTransaction> VasTransactions => Set<VasTransaction>();

    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<DeviceRefreshToken> DeviceRefreshTokens => Set<DeviceRefreshToken>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // All money as decimal(18,2) by default; quantities and unit costs override below.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Tenant>(e =>
        {
            e.HasIndex(t => t.OwnerPhone).IsUnique();
            e.Property(t => t.ShopName).HasMaxLength(100);
            e.Property(t => t.OwnerPhone).HasMaxLength(20);
        });

        builder.Entity<Product>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.Barcode).HasMaxLength(64);
            e.HasIndex(p => new { p.TenantId, p.Barcode });
            e.Property(p => p.WeightedAverageCost).HasPrecision(18, 4);
            e.Property(p => p.CachedQuantity).HasPrecision(18, 3);
            e.Property(p => p.LowStockThreshold).HasPrecision(18, 3);
        });

        builder.Entity<Sale>(e =>
        {
            e.HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.SaleId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(s => s.Payments).WithOne().HasForeignKey(p => p.SaleId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(s => new { s.TenantId, s.OccurredAtUtc });
        });

        builder.Entity<SaleLine>(e =>
        {
            e.Property(l => l.Description).HasMaxLength(200);
            e.Property(l => l.Quantity).HasPrecision(18, 3);
            e.Property(l => l.UnitCostSnapshot).HasPrecision(18, 4);
        });

        builder.Entity<StockMovement>(e =>
        {
            e.Property(m => m.Quantity).HasPrecision(18, 3);
            e.Property(m => m.UnitCost).HasPrecision(18, 4);
            e.Property(m => m.Note).HasMaxLength(500);
            e.HasIndex(m => new { m.TenantId, m.ProductId, m.OccurredAtUtc });
        });

        builder.Entity<Customer>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(120);
            e.Property(c => c.Nickname).HasMaxLength(80);
            e.Property(c => c.Phone).HasMaxLength(20);
            e.Property(c => c.PhotoPath).HasMaxLength(400);
        });

        builder.Entity<CreditEntry>(e =>
        {
            e.Property(c => c.Note).HasMaxLength(500);
            e.HasIndex(c => new { c.TenantId, c.CustomerId, c.OccurredAtUtc });
        });

        builder.Entity<CashMovement>(e =>
        {
            e.Property(m => m.Note).HasMaxLength(500);
            e.HasIndex(m => new { m.TenantId, m.OccurredAtUtc });
        });

        builder.Entity<Cashier>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(80);
            e.Property(c => c.PinHash).HasMaxLength(300);
        });

        builder.Entity<Device>(e => e.Property(d => d.Name).HasMaxLength(80));

        builder.Entity<CustomerMessage>(e =>
        {
            e.Property(m => m.TemplateKey).HasMaxLength(60);
            e.Property(m => m.Body).HasMaxLength(320);
            e.Property(m => m.ProviderMessageId).HasMaxLength(100);
            e.Property(m => m.FailureReason).HasMaxLength(500);
        });

        builder.Entity<VasTransaction>(e =>
        {
            e.HasIndex(v => new { v.TenantId, v.IdempotencyKey }).IsUnique();
            e.Property(v => v.TargetReference).HasMaxLength(60);
            e.Property(v => v.ProviderReference).HasMaxLength(100);
            e.Property(v => v.Token).HasMaxLength(200);
            e.Property(v => v.FailureReason).HasMaxLength(500);
        });

        builder.Entity<CashUp>(e => e.Property(c => c.Notes).HasMaxLength(1000));

        builder.Entity<WalletMovement>(e => e.Property(w => w.Note).HasMaxLength(500));

        builder.Entity<OtpChallenge>(e =>
        {
            e.Property(o => o.Phone).HasMaxLength(20);
            e.Property(o => o.CodeHash).HasMaxLength(300);
            e.Property(o => o.ShopName).HasMaxLength(100);
            e.HasIndex(o => new { o.Phone, o.CreatedAtUtc });
        });

        builder.Entity<DeviceRefreshToken>(e =>
        {
            e.Property(t => t.TokenHash).HasMaxLength(100);
            e.HasIndex(t => t.TokenHash).IsUnique();
        });

        ApplyTenantConventions(builder);
    }

    /// <summary>
    /// Applies the tenant global query filter and a TenantId index to every entity
    /// implementing ITenantOwned. Defined once, generically: no per-entity duplication.
    /// </summary>
    private void ApplyTenantConventions(ModelBuilder builder)
    {
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                ApplyTenantFilterMethod.MakeGenericMethod(entityType.ClrType)
                    .Invoke(this, [builder]);
            }
        }
    }

    private void ApplyTenantFilter<TEntity>(ModelBuilder builder)
        where TEntity : class, ITenantOwned
    {
        builder.Entity<TEntity>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        builder.Entity<TEntity>().HasIndex(e => e.TenantId);
    }
}

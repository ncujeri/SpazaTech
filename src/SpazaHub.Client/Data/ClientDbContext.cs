using Microsoft.EntityFrameworkCore;
using SpazaHub.Domain.Entities;

namespace SpazaHub.Client.Data;

/// <summary>
/// Local SQLite database in the browser. Shares the server's entity model (single
/// tenant locally, so no query filters) plus the sync outbox and state tables.
/// </summary>
public class ClientDbContext : DbContext
{
    public ClientDbContext(DbContextOptions<ClientDbContext> options) : base(options)
    {
    }

    public DbSet<TenantConfig> TenantConfigs => Set<TenantConfig>();
    public DbSet<TenantWallet> TenantWallets => Set<TenantWallet>();
    public DbSet<WalletMovement> WalletMovements => Set<WalletMovement>();
    public DbSet<Cashier> Cashiers => Set<Cashier>();
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

    public DbSet<SyncOutboxItem> SyncOutbox => Set<SyncOutboxItem>();
    public DbSet<SyncClientState> SyncState => Set<SyncClientState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Sale>(e =>
        {
            e.HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.SaleId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(s => s.Payments).WithOne().HasForeignKey(p => p.SaleId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SyncOutboxItem>(e =>
        {
            // The sequence is assigned by the app from SyncClientState, not by the database.
            e.HasKey(o => o.Sequence);
            e.Property(o => o.Sequence).ValueGeneratedNever();
        });

        builder.Entity<SyncClientState>(e => e.Property(s => s.Id).ValueGeneratedNever());
    }
}

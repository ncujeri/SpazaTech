using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SpazaHub.Api.Common.Interfaces;
using SpazaHub.Api.Persistence;

namespace SpazaHub.Api.Tests.Tenancy;

/// <summary>Mutable tenant provider so tests can switch tenant context mid-test.</summary>
public sealed class FakeTenantProvider : ITenantProvider
{
    public Guid? CurrentTenant { get; set; }

    public bool HasTenant => CurrentTenant is not null;

    public Guid TenantId => CurrentTenant
        ?? throw new InvalidOperationException("No tenant context in this test.");
}

/// <summary>
/// Spins up an AppDbContext on a shared in-memory Sqlite connection with the tenant
/// stamping interceptor attached, mirroring the production wiring.
/// </summary>
public sealed class TenancyTestHarness : IDisposable
{
    private readonly SqliteConnection _connection;

    public FakeTenantProvider TenantProvider { get; } = new();

    public TenancyTestHarness()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new TenantStampInterceptor(TenantProvider))
            .Options;

        return new AppDbContext(options, TenantProvider);
    }

    public void Dispose() => _connection.Dispose();
}

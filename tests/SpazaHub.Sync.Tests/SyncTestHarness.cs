using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SpazaHub.Application.Common.Interfaces;
using SpazaHub.Application.Sync;
using SpazaHub.Domain.Entities;
using SpazaHub.Infrastructure.Persistence;
using SpazaHub.Infrastructure.Sync;

namespace SpazaHub.Sync.Tests;

public sealed class FakeTenantProvider : ITenantProvider
{
    public Guid? CurrentTenant { get; set; }

    public bool HasTenant => CurrentTenant is not null;

    public Guid TenantId => CurrentTenant
        ?? throw new InvalidOperationException("No tenant context in this test.");
}

/// <summary>
/// Server-side sync test rig: real AppDbContext on shared in-memory Sqlite with both
/// production interceptors, a switchable tenant, and registered devices per tenant.
/// </summary>
public sealed class SyncTestHarness : IDisposable
{
    private readonly SqliteConnection _connection;

    public FakeTenantProvider TenantProvider { get; } = new();

    public SyncDeviceContext DeviceContext { get; } = new();

    public Guid TenantA { get; } = Guid.NewGuid();

    public Guid TenantB { get; } = Guid.NewGuid();

    public Guid DeviceA1 { get; }

    public Guid DeviceA2 { get; }

    public Guid DeviceB1 { get; }

    public SyncTestHarness()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using (var context = CreateContext())
        {
            context.Database.EnsureCreated();
        }

        DeviceA1 = RegisterDevice(TenantA, "Owner phone A");
        DeviceA2 = RegisterDevice(TenantA, "Counter tablet A");
        DeviceB1 = RegisterDevice(TenantB, "Owner phone B");
    }

    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(
                new TenantStampInterceptor(TenantProvider),
                new ChangeLogInterceptor(DeviceContext, TimeProvider.System))
            .Options;

        return new AppDbContext(options, TenantProvider);
    }

    /// <summary>Creates a SyncService over a fresh context, as one request scope would.</summary>
    public SyncService CreateSyncService(AppDbContext context)
        => new(context, TenantProvider, DeviceContext, TimeProvider.System,
            NullLogger<SyncService>.Instance);

    private Guid RegisterDevice(Guid tenantId, string name)
    {
        TenantProvider.CurrentTenant = tenantId;
        using var context = CreateContext();
        var device = new Device { Name = name, RegisteredAtUtc = DateTime.UtcNow };
        context.Devices.Add(device);
        context.SaveChanges();
        TenantProvider.CurrentTenant = null;
        return device.Id;
    }

    public void Dispose() => _connection.Dispose();
}

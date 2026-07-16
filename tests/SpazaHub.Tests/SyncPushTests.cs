using Microsoft.EntityFrameworkCore;
using SpazaHub.Api.Common.Exceptions;
using SpazaHub.Domain.Common;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;
using SpazaHub.Shared.Sync;

namespace SpazaHub.Sync.Tests;

public class SyncPushTests : IDisposable
{
    private readonly SyncTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static SyncItemDto Item(Entity entity, long sequence)
        => new(entity.Id, entity.GetType().Name, sequence, DateTime.UtcNow,
            SyncJson.Serialize(entity, entity.GetType()));

    private static (Sale Sale, SaleLine Line, SalePayment Payment) NewSaleGraph()
    {
        var sale = new Sale
        {
            DeviceId = Guid.NewGuid(),
            OccurredAtUtc = DateTime.UtcNow,
            Total = 25m
        };
        var line = new SaleLine
        {
            SaleId = sale.Id,
            Description = "Bread",
            Quantity = 1m,
            UnitPrice = 25m,
            LineTotal = 25m
        };
        var payment = new SalePayment { SaleId = sale.Id, Method = PaymentMethod.Cash, Amount = 25m };
        return (sale, line, payment);
    }

    [Fact]
    public async Task Push_AppliesOrderedBatchAndAcksHighestSequence()
    {
        var (sale, line, payment) = NewSaleGraph();
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;

        await using var context = _harness.CreateContext();
        var sync = _harness.CreateSyncService(context);

        var response = await sync.PushAsync(_harness.DeviceA1,
            [Item(sale, 1), Item(line, 2), Item(payment, 3)]);

        Assert.Equal(3, response.HighestAckedSequence);
        Assert.Equal(3, response.AppliedCount);
        Assert.Equal(0, response.DuplicateCount);

        await using var verify = _harness.CreateContext();
        Assert.Equal(1, await verify.Sales.CountAsync());
        Assert.Equal(1, await verify.SaleLines.CountAsync());
        Assert.Equal(1, await verify.SalePayments.CountAsync());
        Assert.Equal(_harness.TenantA, (await verify.Sales.SingleAsync()).TenantId);
    }

    [Fact]
    public async Task Push_ReplayedBatch_IsIdempotent()
    {
        // The classic lost-ack case: the device pushes, the ack never arrives,
        // the device pushes the identical batch again.
        var (sale, line, payment) = NewSaleGraph();
        var batch = new[] { Item(sale, 1), Item(line, 2), Item(payment, 3) };
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;

        await using (var context = _harness.CreateContext())
        {
            await _harness.CreateSyncService(context).PushAsync(_harness.DeviceA1, batch);
        }

        long changeLogBefore;
        await using (var verify = _harness.CreateContext())
        {
            changeLogBefore = await verify.TenantChangeLog.CountAsync();
        }

        await using (var context = _harness.CreateContext())
        {
            var replay = await _harness.CreateSyncService(context).PushAsync(_harness.DeviceA1, batch);

            Assert.Equal(3, replay.HighestAckedSequence);
            Assert.Equal(0, replay.AppliedCount);
            Assert.Equal(3, replay.DuplicateCount);
        }

        await using (var verify = _harness.CreateContext())
        {
            Assert.Equal(1, await verify.Sales.CountAsync());
            Assert.Equal(1, await verify.SaleLines.CountAsync());
            Assert.Equal(1, await verify.SalePayments.CountAsync());
            Assert.Equal(changeLogBefore, await verify.TenantChangeLog.CountAsync());
        }
    }

    [Fact]
    public async Task Push_OutOfOrderSequences_AreAppliedInSequenceOrder()
    {
        var (sale, line, payment) = NewSaleGraph();
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;

        await using var context = _harness.CreateContext();
        // Batch arrives shuffled; the sale must still be inserted before its children.
        var response = await _harness.CreateSyncService(context).PushAsync(_harness.DeviceA1,
            [Item(payment, 3), Item(sale, 1), Item(line, 2)]);

        Assert.Equal(3, response.AppliedCount);

        await using var verify = _harness.CreateContext();
        Assert.Equal(1, await verify.Sales.CountAsync());
        Assert.Equal(1, await verify.SalePayments.CountAsync());
    }

    [Fact]
    public async Task Push_WeekOfflineBacklog_DrainsInChunks()
    {
        // A device offline for a week: 150 sales, three chunks of 100 items.
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        var items = new List<SyncItemDto>();
        long seq = 0;
        for (int i = 0; i < 150; i++)
        {
            var sale = new Sale { DeviceId = Guid.NewGuid(), OccurredAtUtc = DateTime.UtcNow, Total = i };
            items.Add(Item(sale, ++seq));
            items.Add(Item(new SalePayment { SaleId = sale.Id, Method = PaymentMethod.Cash, Amount = i }, ++seq));
        }

        long lastAck = 0;
        foreach (var chunk in items.Chunk(100))
        {
            await using var context = _harness.CreateContext();
            var response = await _harness.CreateSyncService(context)
                .PushAsync(_harness.DeviceA1, chunk);
            Assert.Equal(chunk[^1].DeviceSequence, response.HighestAckedSequence);
            Assert.True(response.HighestAckedSequence > lastAck);
            lastAck = response.HighestAckedSequence;
        }

        await using var verify = _harness.CreateContext();
        Assert.Equal(150, await verify.Sales.CountAsync());
        Assert.Equal(150, await verify.SalePayments.CountAsync());
    }

    [Fact]
    public async Task Push_UnknownEntityType_IsRejected()
    {
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        await using var context = _harness.CreateContext();
        var sync = _harness.CreateSyncService(context);

        var item = new SyncItemDto(Guid.NewGuid(), "Tenant", 1, DateTime.UtcNow, "{}");

        await Assert.ThrowsAsync<AppValidationException>(
            () => sync.PushAsync(_harness.DeviceA1, [item]));
    }

    [Fact]
    public async Task Push_ServerAuthoritativeType_CannotBePushed()
    {
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        await using var context = _harness.CreateContext();
        var sync = _harness.CreateSyncService(context);

        var wallet = new TenantWallet { Balance = 999_999m, UpdatedAtUtc = DateTime.UtcNow };
        var item = Item(wallet, 1);

        await Assert.ThrowsAsync<AppValidationException>(
            () => sync.PushAsync(_harness.DeviceA1, [item]));
    }

    [Fact]
    public async Task Push_UnknownDevice_IsRejected()
    {
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        await using var context = _harness.CreateContext();
        var sync = _harness.CreateSyncService(context);

        var (sale, _, _) = NewSaleGraph();

        // Device from another tenant is invisible through the filter.
        await Assert.ThrowsAsync<AuthenticationFailedException>(
            () => sync.PushAsync(_harness.DeviceB1, [Item(sale, 1)]));
    }

    [Fact]
    public async Task Push_PayloadCannotSpoofTenantId()
    {
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        var sale = new Sale { DeviceId = Guid.NewGuid(), OccurredAtUtc = DateTime.UtcNow, Total = 5m };

        // Hand-craft a payload that tries to smuggle another tenant's id.
        string payload = SyncJson.Serialize(sale, typeof(Sale))
            .TrimEnd('}') + $",\"tenantId\":\"{_harness.TenantB}\"}}";
        var item = new SyncItemDto(sale.Id, nameof(Sale), 1, DateTime.UtcNow, payload);

        await using var context = _harness.CreateContext();
        await _harness.CreateSyncService(context).PushAsync(_harness.DeviceA1, [item]);

        await using var verify = _harness.CreateContext();
        var stored = await verify.Sales.SingleAsync();
        Assert.Equal(_harness.TenantA, stored.TenantId);
    }
}

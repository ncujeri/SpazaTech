using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SpazaHub.Api.Messaging;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;
using SpazaHub.Shared.Auth;

namespace SpazaHub.Tests;
using SpazaHub.Sync.Tests;

/// <summary>SMSFlow webhook handling: delivery receipts and POPIA STOP opt-outs.</summary>
public class SmsWebhookTests : IDisposable
{
    private readonly SyncTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private async Task<CustomerMessage> SeedMessageAsync()
    {
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        await using var context = _harness.CreateContext();
        var message = new CustomerMessage
        {
            CustomerId = Guid.NewGuid(),
            TemplateKey = "reminder-en",
            Body = "Hello",
            Channel = MessageChannel.ProviderSms,
            Status = MessageStatus.Submitted,
            QueuedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        context.CustomerMessages.Add(message);
        await context.SaveChangesAsync();
        return message;
    }

    [Fact]
    public async Task DeliveryReceipt_MovesStatusAndFlowsDownTheChangeLog()
    {
        var message = await SeedMessageAsync();

        // Webhooks arrive with no tenant context.
        _harness.TenantProvider.CurrentTenant = null;
        await using (var context = _harness.CreateContext())
        {
            var service = new SmsWebhookService(context, TimeProvider.System, NullLogger<SmsWebhookService>.Instance);
            bool applied = await service.ApplyDeliveryReceiptAsync(message.Id.ToString(), "delivered", "SF-123");
            Assert.True(applied);
        }

        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        await using (var verify = _harness.CreateContext())
        {
            var stored = await verify.CustomerMessages.SingleAsync();
            Assert.Equal(MessageStatus.Delivered, stored.Status);
            Assert.Equal("SF-123", stored.ProviderMessageId);
            Assert.NotNull(stored.DeliveredAtUtc);

            // The status change was logged, so devices receive it on pull.
            Assert.Contains(
                await verify.TenantChangeLog.ToListAsync(),
                c => c.EntityType == nameof(CustomerMessage) && c.PayloadJson.Contains("\"status\":2"));
        }
    }

    [Fact]
    public async Task DeliveryReceipt_FailedStatusAndUnknownReference()
    {
        var message = await SeedMessageAsync();
        _harness.TenantProvider.CurrentTenant = null;

        await using var context = _harness.CreateContext();
        var service = new SmsWebhookService(context, TimeProvider.System, NullLogger<SmsWebhookService>.Instance);

        Assert.False(await service.ApplyDeliveryReceiptAsync(Guid.NewGuid().ToString(), "delivered", null));
        Assert.False(await service.ApplyDeliveryReceiptAsync("not-a-guid", "delivered", null));

        Assert.True(await service.ApplyDeliveryReceiptAsync(message.Id.ToString(), "failed", null));

        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        await using var verify = _harness.CreateContext();
        Assert.Equal(MessageStatus.Failed, (await verify.CustomerMessages.SingleAsync()).Status);
    }

    [Fact]
    public async Task InboundStop_WithdrawsConsentAcrossTenants()
    {
        const string phone = "+27825550001";

        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        await using (var context = _harness.CreateContext())
        {
            context.Customers.Add(new Customer
            {
                Name = "Gogo", Phone = phone, ReminderConsent = true,
                ConsentCapturedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        _harness.TenantProvider.CurrentTenant = null;
        await using (var context = _harness.CreateContext())
        {
            var service = new SmsWebhookService(context, TimeProvider.System, NullLogger<SmsWebhookService>.Instance);

            Assert.Equal(0, await service.ApplyInboundAsync(phone, "How much do I owe?"));
            Assert.Equal(1, await service.ApplyInboundAsync(phone, " stop "));
        }

        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        await using (var verify = _harness.CreateContext())
        {
            var customer = await verify.Customers.SingleAsync();
            Assert.False(customer.ReminderConsent);
            Assert.Null(customer.ConsentCapturedAtUtc);
        }
    }
}

/// <summary>The PIN hasher now lives in Shared so devices verify shifts offline.</summary>
public class PinHasherTests
{
    [Fact]
    public void HashAndVerify_RoundTripsAndRejectsWrongPin()
    {
        string hash = PinHasher.Hash("1234");

        Assert.True(PinHasher.Verify("1234", hash));
        Assert.False(PinHasher.Verify("4321", hash));
        Assert.False(PinHasher.Verify("1234", "garbage"));
    }

    [Fact]
    public void Hash_IsSaltedPerCall()
    {
        Assert.NotEqual(PinHasher.Hash("1234"), PinHasher.Hash("1234"));
    }
}

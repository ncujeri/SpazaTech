using Microsoft.EntityFrameworkCore;
using SpazaHub.Domain.Entities;

namespace SpazaHub.Application.Tests.Tenancy;

public class TenantIsolationTests : IDisposable
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private readonly TenancyTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private void Seed(Guid tenantId, string productName)
    {
        _harness.TenantProvider.CurrentTenant = tenantId;
        using var context = _harness.CreateContext();
        context.Products.Add(new Product { Name = productName, SellPrice = 10m });
        context.SaveChanges();
    }

    [Fact]
    public void Insert_StampsTenantIdFromProvider()
    {
        Seed(TenantA, "Bread");

        _harness.TenantProvider.CurrentTenant = TenantA;
        using var context = _harness.CreateContext();
        var product = context.Products.Single();

        Assert.Equal(TenantA, product.TenantId);
    }

    [Fact]
    public void Insert_OverridesClientSuppliedTenantId()
    {
        // A hostile client sets someone else's TenantId; the server must stamp over it.
        _harness.TenantProvider.CurrentTenant = TenantA;
        using (var context = _harness.CreateContext())
        {
            context.Products.Add(new Product { Name = "Milk", SellPrice = 15m, TenantId = TenantB });
            context.SaveChanges();
        }

        using (var context = _harness.CreateContext())
        {
            var product = context.Products.IgnoreQueryFilters().Single();
            Assert.Equal(TenantA, product.TenantId);
        }
    }

    [Fact]
    public void Query_FilterHidesOtherTenantsRows()
    {
        Seed(TenantA, "Bread");
        Seed(TenantB, "Airtime voucher");

        _harness.TenantProvider.CurrentTenant = TenantA;
        using var context = _harness.CreateContext();
        var visible = context.Products.ToList();

        Assert.Single(visible);
        Assert.Equal("Bread", visible[0].Name);
    }

    [Fact]
    public void Query_NoTenantContext_SeesNothing()
    {
        Seed(TenantA, "Bread");

        _harness.TenantProvider.CurrentTenant = null;
        using var context = _harness.CreateContext();

        Assert.Empty(context.Products.ToList());
    }

    [Fact]
    public void Query_FilterAppliesToEveryTenantOwnedEntity()
    {
        _harness.TenantProvider.CurrentTenant = TenantA;
        using (var context = _harness.CreateContext())
        {
            context.Customers.Add(new Customer { Name = "Gogo Dlamini" });
            context.StockMovements.Add(new StockMovement
            {
                ProductId = Guid.NewGuid(),
                Quantity = 5m,
                OccurredAtUtc = DateTime.UtcNow
            });
            context.SaveChanges();
        }

        _harness.TenantProvider.CurrentTenant = TenantB;
        using (var context = _harness.CreateContext())
        {
            Assert.Empty(context.Customers.ToList());
            Assert.Empty(context.StockMovements.ToList());
        }
    }

    [Fact]
    public void Insert_WithoutTenantContextOrExplicitTenant_Throws()
    {
        _harness.TenantProvider.CurrentTenant = null;
        using var context = _harness.CreateContext();
        context.Products.Add(new Product { Name = "Orphan", SellPrice = 1m });

        Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
    }

    [Fact]
    public void Insert_SystemFlowWithExplicitTenant_IsAllowed()
    {
        // Registration runs before any authenticated tenant exists and sets TenantId itself.
        _harness.TenantProvider.CurrentTenant = null;
        using (var context = _harness.CreateContext())
        {
            context.TenantConfigs.Add(new TenantConfig { TenantId = TenantA, UpdatedAtUtc = DateTime.UtcNow });
            context.SaveChanges();
        }

        _harness.TenantProvider.CurrentTenant = TenantA;
        using (var context = _harness.CreateContext())
        {
            Assert.Single(context.TenantConfigs.ToList());
        }
    }

    [Fact]
    public void Update_CannotMoveRowToAnotherTenant()
    {
        Seed(TenantA, "Bread");

        _harness.TenantProvider.CurrentTenant = TenantA;
        Guid productId;
        using (var context = _harness.CreateContext())
        {
            var product = context.Products.Single();
            productId = product.Id;
            product.TenantId = TenantB;
            product.SellPrice = 11m;
            context.SaveChanges();
        }

        using (var context = _harness.CreateContext())
        {
            var product = context.Products.IgnoreQueryFilters().Single(p => p.Id == productId);
            Assert.Equal(TenantA, product.TenantId);
            Assert.Equal(11m, product.SellPrice);
        }
    }
}

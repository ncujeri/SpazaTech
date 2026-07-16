using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SpazaHub.Api.Common.Interfaces;

namespace SpazaHub.Api.Persistence;

/// <summary>
/// Used only by dotnet-ef at design time to generate SQL Server migrations. The
/// connection string is a placeholder; migrations are generated offline.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(designtime);Database=SpazaHub;Trusted_Connection=True;")
            .Options;

        return new AppDbContext(options, new DesignTimeTenantProvider());
    }

    private sealed class DesignTimeTenantProvider : ITenantProvider
    {
        public bool HasTenant => false;

        public Guid TenantId => throw new InvalidOperationException("No tenant at design time.");
    }
}

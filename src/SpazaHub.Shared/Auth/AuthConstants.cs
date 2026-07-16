namespace SpazaHub.Shared.Auth;

/// <summary>Role names carried in the JWT role claim.</summary>
public static class Roles
{
    public const string Owner = "Owner";
    public const string Cashier = "Cashier";
}

/// <summary>Custom claim type names used across client and server.</summary>
public static class AppClaimTypes
{
    public const string TenantId = "tenant_id";
    public const string CashierId = "cashier_id";
    public const string Role = "role";
}

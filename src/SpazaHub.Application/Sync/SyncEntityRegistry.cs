using SpazaHub.Domain.Common;
using SpazaHub.Domain.Entities;

namespace SpazaHub.Application.Sync;

/// <summary>How an entity type behaves under sync.</summary>
public enum SyncEntityKind
{
    /// <summary>Immutable events: dedupe on Id, never updated, cannot conflict.</summary>
    AppendOnly = 0,

    /// <summary>Mutable reference data: last-write-wins on UpdatedAtUtc with server audit.</summary>
    MutableLastWriteWins = 1
}

public sealed record SyncEntityDescriptor(
    string Name,
    Type ClrType,
    SyncEntityKind Kind,
    bool PushAllowed);

/// <summary>
/// The whitelist of entity types that participate in sync. Anything not listed here is
/// rejected at the push endpoint and never written to the change log.
/// </summary>
public static class SyncEntityRegistry
{
    private static readonly Dictionary<string, SyncEntityDescriptor> ByName;
    private static readonly Dictionary<Type, SyncEntityDescriptor> ByType;

    static SyncEntityRegistry()
    {
        var descriptors = new List<SyncEntityDescriptor>
        {
            AppendOnly<Sale>(),
            AppendOnly<SaleLine>(),
            AppendOnly<SalePayment>(),
            AppendOnly<StockMovement>(),
            AppendOnly<CreditEntry>(),
            AppendOnly<CashMovement>(),
            AppendOnly<CashbackTransaction>(),
            AppendOnly<FeeIncome>(),
            AppendOnly<WalletMovement>(),
            Mutable<Product>(),
            Mutable<Customer>(),
            Mutable<TenantConfig>(),
            Mutable<CashUp>(),
            Mutable<Cashier>(),
            // Wallet balance is server-authoritative: it syncs down but is never pushed.
            Mutable<TenantWallet>(pushAllowed: false)
        };

        foreach (var descriptor in descriptors)
        {
            if (descriptor.Kind == SyncEntityKind.MutableLastWriteWins
                && !typeof(IMutableSynced).IsAssignableFrom(descriptor.ClrType))
            {
                throw new InvalidOperationException(
                    $"{descriptor.Name} is registered as mutable but does not implement IMutableSynced.");
            }
        }

        ByName = descriptors.ToDictionary(d => d.Name, StringComparer.Ordinal);
        ByType = descriptors.ToDictionary(d => d.ClrType);
    }

    public static IReadOnlyCollection<SyncEntityDescriptor> All => ByName.Values;

    public static bool TryGet(string name, out SyncEntityDescriptor descriptor)
        => ByName.TryGetValue(name, out descriptor!);

    public static bool TryGet(Type clrType, out SyncEntityDescriptor descriptor)
        => ByType.TryGetValue(clrType, out descriptor!);

    private static SyncEntityDescriptor AppendOnly<T>() where T : Entity, IAppendOnly, ITenantOwned
        => new(typeof(T).Name, typeof(T), SyncEntityKind.AppendOnly, PushAllowed: true);

    private static SyncEntityDescriptor Mutable<T>(bool pushAllowed = true) where T : Entity, ITenantOwned
        => new(typeof(T).Name, typeof(T), SyncEntityKind.MutableLastWriteWins, pushAllowed);
}

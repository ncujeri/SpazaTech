using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using SpazaHub.Client.Data;
using SpazaHub.Client.Sync;
using SpazaHub.Domain.Entities;
using SpazaHub.Shared.Auth;

namespace SpazaHub.Client.Services;

/// <summary>Who is behind the counter right now, persisted across reloads.</summary>
public sealed record ShiftState(string Role, Guid? CashierId, string? CashierName, bool CanDoCashback);

/// <summary>
/// Shift sessions on the device. The owner sets up the device; cashiers unlock a shift
/// with their 4-digit PIN, verified locally against the synced hash so shift changes
/// work with zero signal. Switching back to owner requires the owner PIN once one is
/// set. Wrong PINs back off after five tries.
/// </summary>
public class SessionService
{
    private const string Key = "spazahub.shift";
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(2);

    private readonly IDbContextFactory<ClientDbContext> _contextFactory;
    private readonly LocalStore _store;
    private readonly IJSRuntime _js;
    private int _failedAttempts;
    private DateTime? _lockedUntilUtc;

    public SessionService(
        IDbContextFactory<ClientDbContext> contextFactory, LocalStore store, IJSRuntime js)
    {
        _contextFactory = contextFactory;
        _store = store;
        _js = js;
    }

    public ShiftState Current { get; private set; } = new(Roles.Owner, null, null, true);

    public bool IsOwner => Current.Role == Roles.Owner;

    public Guid? CurrentCashierId => Current.CashierId;

    /// <summary>Raised when the shift changes so the layout can re-render.</summary>
    public event Action? ShiftChanged;

    public async Task RestoreAsync()
    {
        try
        {
            string? json = await _js.InvokeAsync<string?>("localStorage.getItem", Key);
            if (json is not null)
            {
                Current = JsonSerializer.Deserialize<ShiftState>(json) ?? Current;
            }
        }
        catch
        {
            // No stored shift: owner mode.
        }
    }

    public async Task<IReadOnlyList<Cashier>> GetCashiersAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        return await db.Cashiers.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    /// <summary>Owner creates a cashier on the device; the hash syncs to other devices.</summary>
    public async Task<Cashier> CreateCashierAsync(string name, string pin, bool canDoCashback)
    {
        if (!IsOwner)
        {
            throw new InvalidOperationException("Only the owner adds cashiers.");
        }

        var cashier = new Cashier
        {
            Name = name.Trim(),
            PinHash = PinHasher.Hash(pin),
            CanDoCashback = canDoCashback,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await _store.SaveLocalWriteAsync(cashier);
        return cashier;
    }

    /// <summary>Starts a cashier shift after a local PIN check. Works fully offline.</summary>
    public async Task<bool> StartCashierShiftAsync(Guid cashierId, string pin)
    {
        EnsureNotLockedOut();

        await using var db = await _contextFactory.CreateDbContextAsync();
        var cashier = await db.Cashiers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cashierId && c.IsActive);

        if (cashier is null || !PinHasher.Verify(pin, cashier.PinHash))
        {
            RegisterFailure();
            return false;
        }

        _failedAttempts = 0;
        Current = new ShiftState(Roles.Cashier, cashier.Id, cashier.Name, cashier.CanDoCashback);
        await PersistAsync();
        return true;
    }

    /// <summary>True when no owner PIN has been set yet on this shop.</summary>
    public async Task<bool> OwnerPinMissingAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var config = await db.TenantConfigs.AsNoTracking().FirstOrDefaultAsync();
        return string.IsNullOrEmpty(config?.OwnerPinHash);
    }

    /// <summary>Owner sets or changes the PIN that unlocks owner mode.</summary>
    public async Task SetOwnerPinAsync(string pin)
    {
        if (!IsOwner)
        {
            throw new InvalidOperationException("Only the owner sets the owner PIN.");
        }

        await using var db = await _contextFactory.CreateDbContextAsync();
        var config = await db.TenantConfigs.AsNoTracking().FirstOrDefaultAsync()
            ?? new TenantConfig();

        config.OwnerPinHash = PinHasher.Hash(pin);
        config.UpdatedAtUtc = DateTime.UtcNow;
        await _store.SaveLocalWriteAsync(config);
    }

    /// <summary>
    /// Back to owner mode. Requires the owner PIN when one is set; a shop without one
    /// gets through free (and the shift screen nags the owner to set it).
    /// </summary>
    public async Task<bool> SwitchToOwnerAsync(string? pin)
    {
        EnsureNotLockedOut();

        await using var db = await _contextFactory.CreateDbContextAsync();
        var config = await db.TenantConfigs.AsNoTracking().FirstOrDefaultAsync();

        if (!string.IsNullOrEmpty(config?.OwnerPinHash)
            && (pin is null || !PinHasher.Verify(pin, config.OwnerPinHash)))
        {
            RegisterFailure();
            return false;
        }

        _failedAttempts = 0;
        Current = new ShiftState(Roles.Owner, null, null, true);
        await PersistAsync();
        return true;
    }

    /// <summary>Seconds until PIN attempts unlock again; zero when not locked.</summary>
    public int LockoutSecondsLeft()
        => _lockedUntilUtc is DateTime until && until > DateTime.UtcNow
            ? (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds)
            : 0;

    private void EnsureNotLockedOut()
    {
        if (LockoutSecondsLeft() > 0)
        {
            throw new InvalidOperationException("Too many wrong PINs. Wait a bit and try again.");
        }
    }

    private void RegisterFailure()
    {
        _failedAttempts++;
        if (_failedAttempts >= MaxAttempts)
        {
            _lockedUntilUtc = DateTime.UtcNow.Add(Lockout);
            _failedAttempts = 0;
        }
    }

    private async Task PersistAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", Key, JsonSerializer.Serialize(Current));
        }
        catch
        {
            // Shift falls back to owner on reload; not fatal.
        }

        ShiftChanged?.Invoke();
    }
}

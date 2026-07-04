using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpazaHub.Application.Common.Exceptions;
using SpazaHub.Application.Common.Interfaces;
using SpazaHub.Domain.Common;
using SpazaHub.Domain.Entities;
using SpazaHub.Infrastructure.Identity;
using SpazaHub.Infrastructure.Persistence;
using SpazaHub.Shared.Auth;

namespace SpazaHub.Infrastructure.Auth;

/// <summary>
/// Implements owner phone+OTP registration, device refresh tokens, cashier PIN shift
/// login, and cashier management. Auth flows run before a tenant context exists, so
/// tenant scoping here is explicit (IgnoreQueryFilters plus TenantId predicates from
/// the trusted token or claim, never from client input).
/// </summary>
public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IMessagingProvider _messaging;
    private readonly JwtTokenService _tokens;
    private readonly ITenantProvider _tenantProvider;
    private readonly TimeProvider _clock;
    private readonly AuthOptions _options;
    private readonly JwtOptions _jwtOptions;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        AppDbContext db,
        UserManager<ApplicationUser> userManager,
        IMessagingProvider messaging,
        JwtTokenService tokens,
        ITenantProvider tenantProvider,
        TimeProvider clock,
        IOptions<AuthOptions> options,
        IOptions<JwtOptions> jwtOptions,
        ILogger<AuthService> logger)
    {
        _db = db;
        _userManager = userManager;
        _messaging = messaging;
        _tokens = tokens;
        _tenantProvider = tenantProvider;
        _clock = clock;
        _options = options.Value;
        _jwtOptions = jwtOptions.Value;
        _logger = logger;
    }

    public async Task<RegisterOwnerResponse> RegisterOwnerAsync(
        string phoneE164, string shopName, CancellationToken cancellationToken = default)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;

        // Invalidate any previous open challenge for this phone.
        var openChallenges = await _db.OtpChallenges
            .Where(c => c.Phone == phoneE164 && c.ConsumedAtUtc == null && c.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);
        foreach (var open in openChallenges)
        {
            open.ConsumedAtUtc = now;
        }

        string code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        _db.OtpChallenges.Add(new OtpChallenge
        {
            Id = GuidV7.NewGuid(),
            Phone = phoneE164,
            CodeHash = PinHasher.Hash(code),
            ShopName = shopName,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(_options.OtpLifetimeSeconds)
        });

        await _db.SaveChangesAsync(cancellationToken);

        int minutes = Math.Max(1, _options.OtpLifetimeSeconds / 60);
        await _messaging.SendSmsAsync(
            phoneE164,
            $"SpazaHub code: {code}. Valid for {minutes} minutes. Do not share it.",
            clientReference: $"otp-{phoneE164}",
            cancellationToken);

        return new RegisterOwnerResponse(true, _options.OtpLifetimeSeconds);
    }

    public async Task<AuthTokensResponse> VerifyOtpAsync(
        string phoneE164, string code, string deviceName, CancellationToken cancellationToken = default)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;

        var challenge = await _db.OtpChallenges
            .Where(c => c.Phone == phoneE164 && c.ConsumedAtUtc == null && c.ExpiresAtUtc > now)
            .OrderByDescending(c => c.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new AuthenticationFailedException("No active code for this number. Request a new one.");

        if (challenge.FailedAttempts >= _options.MaxOtpAttempts)
        {
            throw new AuthenticationFailedException("Too many wrong attempts. Request a new code.");
        }

        if (!PinHasher.Verify(code, challenge.CodeHash))
        {
            challenge.FailedAttempts++;
            await _db.SaveChangesAsync(cancellationToken);
            throw new AuthenticationFailedException("That code is not correct.");
        }

        challenge.ConsumedAtUtc = now;

        var user = await _userManager.FindByNameAsync(phoneE164);
        if (user is null)
        {
            user = await CreateTenantAndOwnerAsync(phoneE164, challenge.ShopName, now);
        }

        var device = new Device
        {
            TenantId = user.TenantId,
            Name = deviceName,
            RegisteredAtUtc = now,
            LastSeenAtUtc = now
        };
        _db.Devices.Add(device);

        string refreshToken = TokenHasher.NewToken();
        _db.DeviceRefreshTokens.Add(new DeviceRefreshToken
        {
            Id = GuidV7.NewGuid(),
            TokenHash = TokenHasher.HashToken(refreshToken),
            UserId = user.Id,
            TenantId = user.TenantId,
            DeviceId = device.Id,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(_jwtOptions.DeviceRefreshTokenDays)
        });

        await _db.SaveChangesAsync(cancellationToken);

        var (accessToken, expiresIn) = _tokens.CreateOwnerToken(user.Id, user.TenantId);
        return new AuthTokensResponse(accessToken, expiresIn, refreshToken, user.TenantId, Roles.Owner, null);
    }

    public async Task<AuthTokensResponse> RefreshAsync(
        string refreshToken, CancellationToken cancellationToken = default)
    {
        var (tokenRow, _) = await ValidateDeviceTokenAsync(refreshToken, cancellationToken);

        var (accessToken, expiresIn) = _tokens.CreateOwnerToken(tokenRow.UserId, tokenRow.TenantId);
        return new AuthTokensResponse(accessToken, expiresIn, refreshToken, tokenRow.TenantId, Roles.Owner, null);
    }

    public async Task<AuthTokensResponse> CashierLoginAsync(
        string deviceRefreshToken, Guid cashierId, string pin, CancellationToken cancellationToken = default)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        var (tokenRow, _) = await ValidateDeviceTokenAsync(deviceRefreshToken, cancellationToken);

        // No ambient tenant during shift login: scope explicitly by the device token's tenant.
        var cashier = await _db.Cashiers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.Id == cashierId && c.TenantId == tokenRow.TenantId && c.IsActive,
                cancellationToken)
            ?? throw new AuthenticationFailedException("Cashier not found on this shop.");

        if (cashier.LockedOutUntilUtc is not null && cashier.LockedOutUntilUtc > now)
        {
            throw new AuthenticationFailedException("PIN locked. Try again later or ask the owner.");
        }

        if (!PinHasher.Verify(pin, cashier.PinHash))
        {
            cashier.FailedPinAttempts++;
            if (cashier.FailedPinAttempts >= _options.MaxPinAttempts)
            {
                cashier.LockedOutUntilUtc = now.AddMinutes(_options.PinLockoutMinutes);
                cashier.FailedPinAttempts = 0;
                _logger.LogWarning("Cashier {CashierId} locked out after repeated wrong PINs.", cashier.Id);
            }

            await _db.SaveChangesAsync(cancellationToken);
            throw new AuthenticationFailedException("Wrong PIN.");
        }

        cashier.FailedPinAttempts = 0;
        cashier.LockedOutUntilUtc = null;
        await _db.SaveChangesAsync(cancellationToken);

        var (accessToken, expiresIn) = _tokens.CreateCashierToken(tokenRow.UserId, tokenRow.TenantId, cashier.Id);
        return new AuthTokensResponse(accessToken, expiresIn, null, tokenRow.TenantId, Roles.Cashier, cashier.Id);
    }

    public async Task<CashierDto> CreateCashierAsync(
        string name, string pin, bool canDoCashback, CancellationToken cancellationToken = default)
    {
        if (!_tenantProvider.HasTenant)
        {
            throw new AuthenticationFailedException("Sign in as the owner first.");
        }

        var cashier = new Cashier
        {
            Name = name,
            PinHash = PinHasher.Hash(pin),
            CanDoCashback = canDoCashback,
            CreatedAtUtc = _clock.GetUtcNow().UtcDateTime
        };

        _db.Cashiers.Add(cashier);
        await _db.SaveChangesAsync(cancellationToken);

        return new CashierDto(cashier.Id, cashier.Name, cashier.IsActive, cashier.CanDoCashback);
    }

    public async Task<IReadOnlyList<CashierDto>> ListCashiersAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Cashiers
            .OrderBy(c => c.Name)
            .Select(c => new CashierDto(c.Id, c.Name, c.IsActive, c.CanDoCashback))
            .ToListAsync(cancellationToken);
    }

    private async Task<ApplicationUser> CreateTenantAndOwnerAsync(string phoneE164, string shopName, DateTime now)
    {
        var tenant = new Tenant
        {
            ShopName = string.IsNullOrWhiteSpace(shopName) ? "My Spaza" : shopName,
            OwnerPhone = phoneE164,
            CreatedAtUtc = now
        };
        _db.Tenants.Add(tenant);

        _db.TenantConfigs.Add(new TenantConfig { TenantId = tenant.Id, UpdatedAtUtc = now });
        _db.TenantWallets.Add(new TenantWallet { TenantId = tenant.Id, Balance = 0m, UpdatedAtUtc = now });

        var user = new ApplicationUser
        {
            Id = GuidV7.NewGuid(),
            UserName = phoneE164,
            PhoneNumber = phoneE164,
            PhoneNumberConfirmed = true,
            TenantId = tenant.Id,
            CreatedAtUtc = now
        };

        var result = await _userManager.CreateAsync(user);
        if (!result.Succeeded)
        {
            string errors = string.Join("; ", result.Errors.Select(e => e.Description));
            throw new ConflictException($"Could not create the owner account: {errors}");
        }

        _logger.LogInformation("New tenant {TenantId} registered for {Phone}.", tenant.Id, phoneE164);
        return user;
    }

    private async Task<(DeviceRefreshToken Token, Device Device)> ValidateDeviceTokenAsync(
        string refreshToken, CancellationToken cancellationToken)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        string hash = TokenHasher.HashToken(refreshToken);

        var tokenRow = await _db.DeviceRefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken)
            ?? throw new AuthenticationFailedException("This device is not registered. Sign in with OTP.");

        if (tokenRow.RevokedAtUtc is not null || tokenRow.ExpiresAtUtc <= now)
        {
            throw new AuthenticationFailedException("Device session expired. Sign in with OTP again.");
        }

        var device = await _db.Devices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == tokenRow.DeviceId && d.TenantId == tokenRow.TenantId, cancellationToken)
            ?? throw new AuthenticationFailedException("Device record missing. Sign in with OTP again.");

        if (device.IsRevoked)
        {
            throw new AuthenticationFailedException("This device was removed by the owner.");
        }

        device.LastSeenAtUtc = now;
        await _db.SaveChangesAsync(cancellationToken);

        return (tokenRow, device);
    }
}

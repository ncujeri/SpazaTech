using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SpazaHub.Shared.Auth;

namespace SpazaHub.Api.Auth;

/// <summary>
/// Mints access tokens. Every token carries tenant_id and role claims; cashier tokens
/// additionally carry cashier_id.
/// </summary>
public class JwtTokenService
{
    private readonly JwtOptions _options;
    private readonly TimeProvider _clock;

    public JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;

        if (string.IsNullOrWhiteSpace(_options.SigningKey) || _options.SigningKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey is missing or too short. Configure it via user-secrets locally or Key Vault in production.");
        }
    }

    public (string Token, int ExpiresInSeconds) CreateOwnerToken(Guid userId, Guid tenantId)
        => Create(userId, tenantId, Roles.Owner, cashierId: null, _options.OwnerAccessTokenMinutes);

    public (string Token, int ExpiresInSeconds) CreateCashierToken(Guid userId, Guid tenantId, Guid cashierId)
        => Create(userId, tenantId, Roles.Cashier, cashierId, _options.CashierAccessTokenMinutes);

    private (string Token, int ExpiresInSeconds) Create(
        Guid userId, Guid tenantId, string role, Guid? cashierId, int lifetimeMinutes)
    {
        var now = _clock.GetUtcNow();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(AppClaimTypes.TenantId, tenantId.ToString()),
            new(ClaimTypes.Role, role)
        };

        if (cashierId is not null)
        {
            claims.Add(new Claim(AppClaimTypes.CashierId, cashierId.Value.ToString()));
        }

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.UtcDateTime.AddMinutes(lifetimeMinutes),
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), lifetimeMinutes * 60);
    }
}

using MediatR;
using SpazaHub.Shared.Auth;

namespace SpazaHub.Application.Auth;

public sealed record RegisterOwnerCommand(string Phone, string ShopName)
    : IRequest<RegisterOwnerResponse>;

public sealed record VerifyOtpCommand(string Phone, string Code, string DeviceName)
    : IRequest<AuthTokensResponse>;

public sealed record RefreshTokenCommand(string RefreshToken)
    : IRequest<AuthTokensResponse>;

public sealed record CashierLoginCommand(string DeviceRefreshToken, Guid CashierId, string Pin)
    : IRequest<AuthTokensResponse>;

public sealed record CreateCashierCommand(string Name, string Pin, bool CanDoCashback)
    : IRequest<CashierDto>;

public sealed record ListCashiersQuery : IRequest<IReadOnlyList<CashierDto>>;

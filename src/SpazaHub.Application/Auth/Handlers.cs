using MediatR;
using SpazaHub.Application.Common.Interfaces;
using SpazaHub.Shared.Auth;

namespace SpazaHub.Application.Auth;

/// <summary>Thin handlers dispatching to the IAuthService port after validation.</summary>
public sealed class RegisterOwnerCommandHandler : IRequestHandler<RegisterOwnerCommand, RegisterOwnerResponse>
{
    private readonly IAuthService _auth;

    public RegisterOwnerCommandHandler(IAuthService auth) => _auth = auth;

    public Task<RegisterOwnerResponse> Handle(RegisterOwnerCommand request, CancellationToken ct)
    {
        PhoneNumberRules.TryNormalize(request.Phone, out string phone);
        return _auth.RegisterOwnerAsync(phone, request.ShopName.Trim(), ct);
    }
}

public sealed class VerifyOtpCommandHandler : IRequestHandler<VerifyOtpCommand, AuthTokensResponse>
{
    private readonly IAuthService _auth;

    public VerifyOtpCommandHandler(IAuthService auth) => _auth = auth;

    public Task<AuthTokensResponse> Handle(VerifyOtpCommand request, CancellationToken ct)
    {
        PhoneNumberRules.TryNormalize(request.Phone, out string phone);
        return _auth.VerifyOtpAsync(phone, request.Code, request.DeviceName.Trim(), ct);
    }
}

public sealed class RefreshTokenCommandHandler : IRequestHandler<RefreshTokenCommand, AuthTokensResponse>
{
    private readonly IAuthService _auth;

    public RefreshTokenCommandHandler(IAuthService auth) => _auth = auth;

    public Task<AuthTokensResponse> Handle(RefreshTokenCommand request, CancellationToken ct)
        => _auth.RefreshAsync(request.RefreshToken, ct);
}

public sealed class CashierLoginCommandHandler : IRequestHandler<CashierLoginCommand, AuthTokensResponse>
{
    private readonly IAuthService _auth;

    public CashierLoginCommandHandler(IAuthService auth) => _auth = auth;

    public Task<AuthTokensResponse> Handle(CashierLoginCommand request, CancellationToken ct)
        => _auth.CashierLoginAsync(request.DeviceRefreshToken, request.CashierId, request.Pin, ct);
}

public sealed class CreateCashierCommandHandler : IRequestHandler<CreateCashierCommand, CashierDto>
{
    private readonly IAuthService _auth;

    public CreateCashierCommandHandler(IAuthService auth) => _auth = auth;

    public Task<CashierDto> Handle(CreateCashierCommand request, CancellationToken ct)
        => _auth.CreateCashierAsync(request.Name.Trim(), request.Pin, request.CanDoCashback, ct);
}

public sealed class ListCashiersQueryHandler : IRequestHandler<ListCashiersQuery, IReadOnlyList<CashierDto>>
{
    private readonly IAuthService _auth;

    public ListCashiersQueryHandler(IAuthService auth) => _auth = auth;

    public Task<IReadOnlyList<CashierDto>> Handle(ListCashiersQuery request, CancellationToken ct)
        => _auth.ListCashiersAsync(ct);
}

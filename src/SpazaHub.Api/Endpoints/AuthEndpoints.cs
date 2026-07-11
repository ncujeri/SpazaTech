using MediatR;
using SpazaHub.Api.Auth;
using SpazaHub.Shared.Auth;

namespace SpazaHub.Api.Endpoints;

/// <summary>Thin auth endpoints: each dispatches one MediatR command.</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register-owner", async (RegisterOwnerRequest request, ISender sender, CancellationToken ct)
            => Results.Ok(await sender.Send(new RegisterOwnerCommand(request.Phone, request.ShopName), ct)));

        group.MapPost("/verify-otp", async (VerifyOtpRequest request, ISender sender, CancellationToken ct)
            => Results.Ok(await sender.Send(new VerifyOtpCommand(request.Phone, request.Code, request.DeviceName), ct)));

        group.MapPost("/refresh", async (RefreshTokenRequest request, ISender sender, CancellationToken ct)
            => Results.Ok(await sender.Send(new RefreshTokenCommand(request.RefreshToken), ct)));

        group.MapPost("/cashier-login", async (CashierLoginRequest request, ISender sender, CancellationToken ct)
            => Results.Ok(await sender.Send(
                new CashierLoginCommand(request.DeviceRefreshToken, request.CashierId, request.Pin), ct)));

        return app;
    }
}

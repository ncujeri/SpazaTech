using MediatR;
using SpazaHub.Application.Auth;
using SpazaHub.Shared.Auth;

namespace SpazaHub.Api.Endpoints;

/// <summary>Cashier management. Owner role required.</summary>
public static class CashierEndpoints
{
    public static IEndpointRouteBuilder MapCashierEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cashiers")
            .WithTags("Cashiers")
            .RequireAuthorization("OwnerOnly");

        group.MapPost("/", async (CreateCashierRequest request, ISender sender, CancellationToken ct)
            => Results.Ok(await sender.Send(
                new CreateCashierCommand(request.Name, request.Pin, request.CanDoCashback), ct)));

        group.MapGet("/", async (ISender sender, CancellationToken ct)
            => Results.Ok(await sender.Send(new ListCashiersQuery(), ct)));

        return app;
    }
}

using MediatR;
using SpazaHub.Application.Sync;
using SpazaHub.Shared.Sync;

namespace SpazaHub.Api.Endpoints;

/// <summary>
/// Sync protocol endpoints. Push is idempotent; pull is cursor-based and resumable.
/// Both run under the caller's tenant context from the JWT.
/// </summary>
public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sync")
            .WithTags("Sync")
            .RequireAuthorization("CashierOrOwner");

        group.MapPost("/push", async (SyncPushRequest request, ISender sender, CancellationToken ct)
            => Results.Ok(await sender.Send(new PushSyncBatchCommand(request.DeviceId, request.Items), ct)));

        group.MapGet("/pull", async (long cursor, int? pageSize, Guid? deviceId, ISender sender, CancellationToken ct)
            => Results.Ok(await sender.Send(
                new PullSyncChangesQuery(cursor, pageSize ?? 200, deviceId), ct)));

        return app;
    }
}

using FluentValidation;
using MediatR;
using SpazaHub.Shared.Sync;

namespace SpazaHub.Api.Sync;

public sealed record PushSyncBatchCommand(Guid DeviceId, IReadOnlyList<SyncItemDto> Items)
    : IRequest<SyncPushResponse>;

public sealed record PullSyncChangesQuery(long Cursor, int PageSize, Guid? ExcludeDeviceId)
    : IRequest<SyncPullResponse>;

public sealed class PushSyncBatchCommandValidator : AbstractValidator<PushSyncBatchCommand>
{
    public const int MaxBatchSize = 500;

    public PushSyncBatchCommandValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();

        RuleFor(x => x.Items)
            .NotEmpty()
            .Must(items => items.Count <= MaxBatchSize)
            .WithMessage($"A push batch holds at most {MaxBatchSize} items; chunk larger backlogs.");

        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.EntityId).NotEmpty();
            item.RuleFor(i => i.EntityType).NotEmpty();
            item.RuleFor(i => i.PayloadJson).NotEmpty();
            item.RuleFor(i => i.DeviceSequence).GreaterThan(0);
        });
    }
}

public sealed class PullSyncChangesQueryValidator : AbstractValidator<PullSyncChangesQuery>
{
    public const int MaxPageSize = 500;

    public PullSyncChangesQueryValidator()
    {
        RuleFor(x => x.Cursor).GreaterThanOrEqualTo(0);
        RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize);
    }
}

public sealed class PushSyncBatchCommandHandler : IRequestHandler<PushSyncBatchCommand, SyncPushResponse>
{
    private readonly ISyncService _sync;

    public PushSyncBatchCommandHandler(ISyncService sync) => _sync = sync;

    public Task<SyncPushResponse> Handle(PushSyncBatchCommand request, CancellationToken ct)
        => _sync.PushAsync(request.DeviceId, request.Items, ct);
}

public sealed class PullSyncChangesQueryHandler : IRequestHandler<PullSyncChangesQuery, SyncPullResponse>
{
    private readonly ISyncService _sync;

    public PullSyncChangesQueryHandler(ISyncService sync) => _sync = sync;

    public Task<SyncPullResponse> Handle(PullSyncChangesQuery request, CancellationToken ct)
        => _sync.PullAsync(request.Cursor, request.PageSize, request.ExcludeDeviceId, ct);
}

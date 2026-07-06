using SpazaHub.Shared.Sync;

namespace SpazaHub.Client.Sync;

/// <summary>
/// Background push/pull loop. Connectivity is an occasional bonus: every cycle simply
/// attempts a push of pending outbox batches followed by a cursor pull, and any network
/// failure is swallowed until the next tick. A device returning after a week offline
/// drains its backlog in ordered chunks and resumes the pull cursor across pages.
/// </summary>
public class BackgroundSyncService : IAsyncDisposable
{
    public const int PushChunkSize = 200;
    public const int PullPageSize = 200;

    private readonly LocalStore _store;
    private readonly SyncApiClient _api;
    private readonly AccessTokenStore _tokens;
    private readonly ILogger<BackgroundSyncService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public BackgroundSyncService(
        LocalStore store, SyncApiClient api, AccessTokenStore tokens, ILogger<BackgroundSyncService> logger)
    {
        _store = store;
        _api = api;
        _tokens = tokens;
        _logger = logger;
    }

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(20);

    public void Start()
    {
        _loop ??= RunLoopAsync(_cts.Token);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await SyncOnceAsync(cancellationToken);
        }
    }

    /// <summary>One full cycle: drain the outbox, then pull to the head of the change log.</summary>
    public async Task SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_tokens.HasToken)
        {
            return;
        }

        var state = await _store.GetStateAsync();
        if (state is null)
        {
            return;
        }

        try
        {
            // Push in ordered chunks until the outbox is empty.
            while (true)
            {
                var pending = await _store.GetPendingItemsAsync(PushChunkSize);
                if (pending.Count == 0)
                {
                    break;
                }

                var response = await _api.PushAsync(
                    new SyncPushRequest(state.DeviceId, pending), cancellationToken);
                if (response is null)
                {
                    break;
                }

                await _store.ClearAckedAsync(response.HighestAckedSequence);

                if (pending.Count < PushChunkSize)
                {
                    break;
                }
            }

            // Pull pages until caught up; the cursor advances per page so an
            // interruption resumes where it stopped.
            bool hasMore = true;
            while (hasMore)
            {
                state = await _store.GetStateAsync();
                if (state is null)
                {
                    break;
                }

                var page = await _api.PullAsync(
                    state.LastPullCursor, PullPageSize, state.DeviceId, cancellationToken);
                if (page is null)
                {
                    break;
                }

                await _store.ApplyRemoteChangesAsync(page.Changes, page.NextCursor);
                hasMore = page.HasMore;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline or server unreachable: normal life; try again next tick.
            _logger.LogDebug(ex, "Sync cycle skipped: {Message}", ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}

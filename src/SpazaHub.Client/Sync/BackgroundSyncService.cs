using System.Net;
using SpazaHub.Client.Services;
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
    private readonly AuthApiClient _auth;
    private readonly ILogger<BackgroundSyncService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task? _loop;

    public BackgroundSyncService(
        LocalStore store, SyncApiClient api, AccessTokenStore tokens, AuthApiClient auth,
        ILogger<BackgroundSyncService> logger)
    {
        _store = store;
        _api = api;
        _tokens = tokens;
        _auth = auth;
        _logger = logger;
    }

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>True while a push/pull cycle is running (manual or on the timer).</summary>
    public bool IsSyncing { get; private set; }

    /// <summary>When the last cycle reached the server. Null until the first success.</summary>
    public DateTimeOffset? LastSyncedAtUtc { get; private set; }

    /// <summary>Short reason the last cycle could not sync (offline, server error), or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised when IsSyncing, LastSyncedAtUtc, or LastError change, so the UI can refresh.</summary>
    public event Action? StatusChanged;

    public void Start()
    {
        _loop ??= RunLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Triggers a cycle now (the "Sync now" button) and completes when it finishes. Waits
    /// for any in-flight timer cycle first so the caller always sees a fresh result.
    /// </summary>
    public async Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        await RunGuardedAsync(cancellationToken);
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke();

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await SyncOnceAsync(cancellationToken);
        }
    }

    /// <summary>
    /// One timer tick. Skips silently if a cycle (manual or timer) is already running, so
    /// overlapping ticks never double-push. Manual runs go through <see cref="SyncNowAsync"/>.
    /// </summary>
    public async Task SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        await RunGuardedAsync(cancellationToken);
    }

    /// <summary>Runs one cycle under the gate, tracking the syncing flag for the UI.</summary>
    private async Task RunGuardedAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsSyncing = true;
            RaiseStatusChanged();
            await RunCycleAsync(cancellationToken);
        }
        finally
        {
            IsSyncing = false;
            RaiseStatusChanged();
            _gate.Release();
        }
    }

    /// <summary>One full cycle: drain the outbox, then pull to the head of the change log.</summary>
    private async Task RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var state = await _store.GetStateAsync();
        if (state is null)
        {
            return;
        }

        // A live access token is needed to reach the server. On a fresh boot, or when the
        // boot-time refresh ran while offline, the device holds a durable refresh token but
        // no access token yet — exchange it here so the loop reconnects on its own once
        // there is signal, instead of staying "offline" until the next full reload.
        if (!_tokens.HasToken)
        {
            await _auth.RefreshAccessTokenAsync();
            if (!_tokens.HasToken)
            {
                // Still nothing: either offline with a session to resume, or a device that
                // was never signed in (demo). Only the former is an error worth showing.
                LastError = _auth.RefreshToken is not null ? "Could not reach the server." : null;
                return;
            }
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

            // Push and pull both completed: we reached the server this cycle.
            LastSyncedAtUtc = DateTimeOffset.UtcNow;
            LastError = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The hourly access token expired mid-loop: refresh and let the next tick retry.
            await _auth.RefreshAccessTokenAsync();
            LastError = "Session expired, retrying…";
        }
        catch (Exception ex)
        {
            // Offline or server unreachable: normal life; try again next tick.
            _logger.LogDebug(ex, "Sync cycle skipped: {Message}", ex.Message);
            LastError = "Could not reach the server.";
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}

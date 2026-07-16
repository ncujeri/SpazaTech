using Microsoft.JSInterop;

namespace SpazaHub.Client.Data;

/// <summary>
/// Persists the local SQLite file to the browser's Origin Private File System.
/// SQLite itself runs against the in-memory emscripten file system; after commits the
/// database file bytes are copied into OPFS, and restored before first open on boot.
/// When OPFS is unavailable the app still works fully, but local data does not survive
/// a page reload (known risk; IndexedDB fallback is the proposed alternative).
/// </summary>
public class OpfsDbPersistence : IAsyncDisposable
{
    public const string DatabaseFileName = "spazahub.db";

    private readonly IJSRuntime _js;
    private readonly ILogger<OpfsDbPersistence> _logger;
    private IJSObjectReference? _module;
    private bool _available;

    public OpfsDbPersistence(IJSRuntime js, ILogger<OpfsDbPersistence> logger)
    {
        _js = js;
        _logger = logger;
    }

    /// <summary>Loads the JS module and restores the database file from OPFS if present.</summary>
    public async Task RestoreAsync()
    {
        try
        {
            _module = await _js.InvokeAsync<IJSObjectReference>("import", "./js/opfs-db.js");
            _available = await _module.InvokeAsync<bool>("isAvailable");

            if (!_available)
            {
                _logger.LogWarning("OPFS is not available; local data will not survive a reload.");
                return;
            }

            byte[]? bytes = await _module.InvokeAsync<byte[]?>("load", DatabaseFileName);
            if (bytes is { Length: > 0 })
            {
                await File.WriteAllBytesAsync(DatabaseFileName, bytes);

                byte[]? walBytes = await _module.InvokeAsync<byte[]?>("load", DatabaseFileName + "-wal");
                if (walBytes is { Length: > 0 })
                {
                    await File.WriteAllBytesAsync(DatabaseFileName + "-wal", walBytes);
                }

                _logger.LogInformation("Restored local database from OPFS ({Bytes} bytes).", bytes.Length);
            }
        }
        catch (Exception ex)
        {
            _available = false;
            _logger.LogWarning(ex, "OPFS restore failed; continuing with in-memory storage only.");
        }
    }

    /// <summary>
    /// Copies the database into OPFS after commits. The WAL side file is included
    /// defensively: the store runs on the rollback journal, but a copy taken during
    /// the journal-mode transition must never lose committed rows.
    /// </summary>
    public async Task SaveAsync()
    {
        if (!_available || _module is null || !File.Exists(DatabaseFileName))
        {
            return;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(DatabaseFileName);
            await _module.InvokeAsync<bool>("save", DatabaseFileName, bytes);

            string walFile = DatabaseFileName + "-wal";
            if (File.Exists(walFile))
            {
                byte[] walBytes = await File.ReadAllBytesAsync(walFile);
                await _module.InvokeAsync<bool>("save", walFile, walBytes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OPFS save failed; local data may not survive a reload.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            await _module.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

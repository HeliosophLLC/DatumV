using DatumIngest.Catalog;
using DatumIngest.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DatumIngest.Web.Catalog;

/// <summary>
/// Watches the catalog directory tree for out-of-band changes (VS Code
/// save, git checkout, hand-edit) and pushes a debounced
/// <see cref="ICatalogHubClient.OnFilesChanged"/> notification to all
/// connected clients. The Project Explorer panel uses this to live-update
/// without polling.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Hint semantics.</strong> The pushed event carries no payload.
/// Clients refetch <c>/api/files</c> rather than try to apply per-file
/// deltas — <see cref="FileSystemWatcher"/> can drop events under load,
/// so treating it as "something changed; ask again" is the only safe
/// model.
/// </para>
/// <para>
/// <strong>Self-trigger redundancy.</strong> The app's own writes
/// (CREATE FUNCTION → write <c>.sql</c> → FS event) coalesce with the
/// matching <see cref="ICatalogHubClient.OnCatalogChanged"/> notification
/// because both push within the client-side debounce window. Mild
/// redundancy on the wire; no correctness impact.
/// </para>
/// <para>
/// <strong>No watcher when in-memory.</strong> A catalog with no on-disk
/// path (test fixtures, in-process scratch) has nothing to watch.
/// <see cref="StartAsync"/> exits early without constructing the
/// <see cref="FileSystemWatcher"/> in that case.
/// </para>
/// </remarks>
internal sealed class CatalogDirectoryWatcher : IHostedService, IDisposable
{
    // Server-side debounce for FS-event bursts (git checkout switches
    // branches → dozens of events in milliseconds). Mirrors the client's
    // 250ms window so a burst still produces ~one push.
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(250);

    private readonly TableCatalog _catalog;
    private readonly IHubContext<CatalogHub, ICatalogHubClient> _signalR;
    private readonly ILogger<CatalogDirectoryWatcher> _logger;

    private FileSystemWatcher? _watcher;
    private readonly Lock _debounceLock = new();
    private Timer? _debounceTimer;

    public CatalogDirectoryWatcher(
        TableCatalog catalog,
        IHubContext<CatalogHub, ICatalogHubClient> signalR,
        ILogger<CatalogDirectoryWatcher> logger)
    {
        _catalog = catalog;
        _signalR = signalR;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        string? directory = _catalog.CatalogDirectory;
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            _logger.LogInformation(
                "Catalog directory watcher not started: no catalog path or directory missing.");
            return Task.CompletedTask;
        }

        // 64KB buffer (8× the default) absorbs bulk operations like a
        // `git checkout` switching between very different branches.
        // Beyond that we still get an Error event and treat the overflow
        // as a single "something changed" push, which is correct because
        // clients refetch from scratch anyway.
        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter =
                NotifyFilters.FileName |
                NotifyFilters.DirectoryName |
                NotifyFilters.LastWrite |
                NotifyFilters.Size |
                NotifyFilters.CreationTime,
        };
        _watcher.Created += OnFsEvent;
        _watcher.Changed += OnFsEvent;
        _watcher.Deleted += OnFsEvent;
        _watcher.Renamed += OnFsRenamed;
        _watcher.Error += OnFsError;
        _watcher.EnableRaisingEvents = true;

        _logger.LogInformation("Catalog directory watcher started at '{Directory}'.", directory);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFsEvent;
            _watcher.Changed -= OnFsEvent;
            _watcher.Deleted -= OnFsEvent;
            _watcher.Renamed -= OnFsRenamed;
            _watcher.Error -= OnFsError;
            _watcher.Dispose();
            _watcher = null;
        }
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (IsNoisePath(e.Name)) return;
        ScheduleBroadcast();
    }

    private void OnFsRenamed(object sender, RenamedEventArgs e)
    {
        // A rename matters when *either* side is a user-meaningful path —
        // atomic-write rename of <file>.tmp → <file> is what makes the
        // new file appear, and it's not noise even though the .tmp side
        // is. Suppress only when both endpoints are noise.
        if (IsNoisePath(e.Name) && IsNoisePath(e.OldName)) return;
        ScheduleBroadcast();
    }

    private void OnFsError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow → some events were lost. Treat as a generic
        // "something changed" and push so clients refetch.
        _logger.LogWarning(e.GetException(), "FileSystemWatcher reported an error; pushing refetch hint.");
        ScheduleBroadcast();
    }

    /// <summary>
    /// True for paths that don't reflect a user-meaningful change — the
    /// atomic-write <c>.tmp</c> sidecars produced by the catalog's own
    /// writes are the only noise we need to filter. The <c>.tmp</c> file
    /// flaps Created → Changed → Deleted on every Save; without this
    /// filter every UDF/proc/model/manifest write would trigger an
    /// unnecessary FilesChanged push.
    /// </summary>
    private static bool IsNoisePath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return false;
        return relativePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleBroadcast()
    {
        lock (_debounceLock)
        {
            _debounceTimer ??= new Timer(_ => Broadcast(), state: null,
                dueTime: Timeout.InfiniteTimeSpan,
                period: Timeout.InfiniteTimeSpan);
            _debounceTimer.Change(DebounceWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void Broadcast()
    {
        // Fire-and-forget mirroring CatalogEventBroadcastService — a torn
        // transport shouldn't bubble out of the watcher callback. Logged
        // catches keep the failure visible.
        _ = Task.Run(async () =>
        {
            try
            {
                await _signalR.Clients.All.OnFilesChanged().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast files-changed event.");
            }
        });
    }
}

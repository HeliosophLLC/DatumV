// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace DatumIngest.ModelLibrary;

internal sealed class ModelDownloadService : IModelDownloadService
{
    private readonly IManifestStore _store;
    private readonly HfHubClient _hub;
    private readonly ILicenseAcceptanceService _licenses;
    private readonly IDownloadProgressReporter _reporter;
    private readonly IModelInstaller _installer;
    private readonly ILogger<ModelDownloadService> _logger;
    private readonly string _modelsDirectory;

    // Prevents two concurrent installs of the same modelId. The value is a
    // CancellationTokenSource so a future "cancel download" RPC can flip
    // it; today we only block re-entry.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public ModelDownloadService(
        IManifestStore store,
        HfHubClient hub,
        ILicenseAcceptanceService licenses,
        IDownloadProgressReporter reporter,
        IModelInstaller installer,
        ModelLibraryOptions options,
        ILogger<ModelDownloadService> logger)
    {
        _store = store;
        _hub = hub;
        _licenses = licenses;
        _reporter = reporter;
        _installer = installer;
        _logger = logger;
        _modelsDirectory = options.ModelsDirectory;
    }

    public async Task<ModelInstallState> ProbeAsync(string modelId, CancellationToken ct = default)
    {
        CatalogModel model = ResolveModel(modelId);

        // Placeholders short-circuit before any I/O. Their HF repo doesn't
        // exist yet (Heliosoph/* not uploaded), so a tree probe would 404
        // and waste a network call. A local directory with the same name —
        // empty leftover, prior failed download — doesn't change the fact
        // that there's nothing to compare against.
        if (model.Placeholder) return ModelInstallState.NotDownloaded;

        string modelDir = Path.Combine(_modelsDirectory, model.Id);

        if (!Directory.Exists(modelDir)) return ModelInstallState.NotDownloaded;

        // For ship-quality probes we'd cache the tree result. For v1 the
        // probe-on-list-refresh case is acceptable; each probe is one HTTP
        // call to HF and the response is ~10KB. We'd be over-engineering to
        // cache before it's a hot path.
        IReadOnlyList<HfTreeEntry> tree;
        try
        {
            IReadOnlyList<HfTreeEntry> raw = await _hub.GetTreeAsync(
                model.Source.Repo, model.Source.Revision, ct).ConfigureAwait(false);
            tree = HfHubClient.FilterByIncludes(raw, model.Source.Include);
        }
        catch (Exception ex)
        {
            // Network issue, gated repo, deleted revision — we can't tell
            // installed-vs-not without the tree. Surface as Partial so the
            // UI shows "needs attention" rather than a false-positive
            // Downloaded.
            _logger.LogWarning(ex, "Probe of {ModelId} could not reach HF tree", modelId);
            return ModelInstallState.Partial;
        }

        int present = 0;
        foreach (HfTreeEntry entry in tree)
        {
            string localPath = Path.Combine(modelDir, entry.Path);
            if (File.Exists(localPath) && new FileInfo(localPath).Length == entry.Size) present++;
        }

        if (present == 0) return ModelInstallState.NotDownloaded;
        if (present < tree.Count) return ModelInstallState.Partial;

        // All files present on disk. For entries with no InstallSql,
        // NullModelInstaller answers true and we land at Installed — the
        // UI treats that as terminal ready-to-use. For entries with
        // InstallSql, the Web-side installer asks the SQL catalog whether
        // the conventional model name (id with '-' → '_', schema "public")
        // is registered. Missing registration means files-are-there-but-
        // install-hasn't-been-run; we surface that as Downloaded so the UI
        // can offer an Install affordance.
        bool registered = await _installer.IsInstalledAsync(model, ct).ConfigureAwait(false);
        return registered ? ModelInstallState.Installed : ModelInstallState.Downloaded;
    }

    public async Task InstallAsync(string modelId, CancellationToken ct = default)
    {
        CatalogModel model = ResolveModel(modelId);

        if (model.Placeholder)
        {
            throw new InvalidOperationException(
                $"Model {modelId} is a placeholder — its HF repo has not been uploaded yet.");
        }

        // Gate: every license that requiresAcceptance must be accepted.
        foreach (string licenseId in model.LicenseIds)
        {
            if (!_store.Manifest.Licenses.TryGetValue(licenseId, out CatalogLicense? license))
            {
                throw new InvalidOperationException(
                    $"Model {modelId} references unknown license {licenseId}.");
            }
            if (license.RequiresAcceptance &&
                !await _licenses.IsAcceptedAsync(licenseId, ct).ConfigureAwait(false))
            {
                throw new LicenseNotAcceptedException(licenseId, license.Title);
            }
        }

        // Single-flight guard.
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_active.TryAdd(modelId, cts))
        {
            throw new InvalidOperationException($"Model {modelId} is already downloading.");
        }

        // Don't await — run in background. The caller's HTTP request returns
        // immediately; progress is pushed over SignalR. Failures are reported
        // via OnModelDownloadFailed; we don't surface them through the HTTP
        // response since by the time the upload finishes the request is gone.
        _ = Task.Run(() => RunDownloadAsync(model, cts.Token), CancellationToken.None);
    }

    public Task UninstallAsync(string modelId, CancellationToken ct = default)
    {
        CatalogModel model = ResolveModel(modelId);
        string modelDir = Path.Combine(_modelsDirectory, model.Id);
        if (Directory.Exists(modelDir)) Directory.Delete(modelDir, recursive: true);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyDictionary<string, ModelInstallState>> ProbeAllAsync(
        CancellationToken ct = default)
    {
        // Parallel probe. ProbeAsync short-circuits to NotInstalled when the
        // model directory doesn't exist (no HF tree call), so for a fresh
        // host with no installed models this is essentially N directory
        // existence checks — fast.
        IReadOnlyList<CatalogModel> models = _store.Manifest.Models;
        Task<(string Id, ModelInstallState State)>[] tasks = new Task<(string, ModelInstallState)>[models.Count];
        for (int i = 0; i < models.Count; i++)
        {
            CatalogModel m = models[i];
            tasks[i] = ProbeOne(m.Id, ct);
        }
        (string Id, ModelInstallState State)[] results = await Task.WhenAll(tasks).ConfigureAwait(false);

        Dictionary<string, ModelInstallState> map = new(results.Length, StringComparer.Ordinal);
        foreach ((string id, ModelInstallState state) in results)
        {
            map[id] = state;
        }
        return map;

        async Task<(string, ModelInstallState)> ProbeOne(string id, CancellationToken token)
            => (id, await ProbeAsync(id, token).ConfigureAwait(false));
    }

    public Task<long> GetPartialBytesAsync(string modelId, CancellationToken ct = default)
    {
        CatalogModel model = ResolveModel(modelId);
        string modelDir = Path.Combine(_modelsDirectory, model.Id);
        if (!Directory.Exists(modelDir)) return Task.FromResult(0L);

        long total = 0;
        // Recursive — some manifests include nested paths like `onnx/model.onnx`,
        // so .part files can live below the model root.
        foreach (string file in Directory.EnumerateFiles(modelDir, "*.part", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { /* file vanished between enumerate and stat; ignore */ }
        }
        return Task.FromResult(total);
    }

    public async Task<IReadOnlyDictionary<string, long>> GetAllPartialBytesAsync(
        CancellationToken ct = default)
    {
        IReadOnlyList<CatalogModel> models = _store.Manifest.Models;
        Dictionary<string, long> map = new(StringComparer.Ordinal);
        // Sequential — pure filesystem reads, no benefit from parallelism for
        // dozens of models, and the order is bounded so we keep this simple.
        foreach (CatalogModel m in models)
        {
            long bytes = await GetPartialBytesAsync(m.Id, ct).ConfigureAwait(false);
            if (bytes > 0) map[m.Id] = bytes;
        }
        return map;
    }

    public Task DeletePartialsAsync(string modelId, CancellationToken ct = default)
    {
        CatalogModel model = ResolveModel(modelId);
        string modelDir = Path.Combine(_modelsDirectory, model.Id);
        if (!Directory.Exists(modelDir)) return Task.CompletedTask;

        foreach (string file in Directory.EnumerateFiles(modelDir, "*.part", SearchOption.AllDirectories))
        {
            try { File.Delete(file); }
            catch (IOException ex)
            {
                // Don't fail the whole operation if one file is locked; the
                // caller's intent is "wipe partials," and a subsequent
                // Install will overwrite anything left behind. Log and move on.
                _logger.LogWarning(ex, "Could not delete partial {File}", file);
            }
        }
        return Task.CompletedTask;
    }

    private async Task RunDownloadAsync(CatalogModel model, CancellationToken ct)
    {
        string modelDir = Path.Combine(_modelsDirectory, model.Id);
        Directory.CreateDirectory(modelDir);

        try
        {
            IReadOnlyList<HfTreeEntry> raw = await _hub.GetTreeAsync(
                model.Source.Repo, model.Source.Revision, ct).ConfigureAwait(false);
            IReadOnlyList<HfTreeEntry> files = HfHubClient.FilterByIncludes(raw, model.Source.Include);

            long totalBytes = files.Sum(f => f.Size);
            await _reporter.OnStartedAsync(
                new ModelDownloadStarted(model.Id, files.Count, totalBytes), ct)
                .ConfigureAwait(false);

            long bytesAcrossModel = 0;
            for (int i = 0; i < files.Count; i++)
            {
                HfTreeEntry file = files[i];
                string destPath = Path.Combine(modelDir, file.Path);

                int index = i;
                long fileSize = file.Size;
                long bytesAtStart = bytesAcrossModel;
                CancellationToken progressCt = ct;
                var progress = new Progress<DownloadByteProgress>(p =>
                {
                    // Fire-and-forget: convert to Task so a ValueTask backed
                    // by IValueTaskSource (some custom reporter impls) isn't
                    // discarded improperly. The Progress<T> callback is sync;
                    // we don't want to block the download loop per emit.
                    _ = _reporter.OnProgressAsync(new ModelDownloadProgress(
                        ModelId: model.Id,
                        CurrentFile: file.Path,
                        FileIndex: index + 1,
                        FileCount: files.Count,
                        BytesReadInFile: p.BytesRead,
                        BytesTotalInFile: p.BytesTotal ?? fileSize,
                        BytesReadTotal: bytesAtStart + p.BytesRead,
                        BytesTotalAcrossModel: totalBytes), progressCt).AsTask();
                });

                string actualSha = await _hub.DownloadFileAsync(
                    model.Source.Repo, model.Source.Revision, file.Path,
                    destPath, progress, ct).ConfigureAwait(false);

                // Verify against the HF tree's lfs.sha256 if present. Small
                // non-LFS files (config.json, tokenizer.json) have no
                // sha256 in the tree response — we trust HTTPS for those.
                if (file.Lfs is not null &&
                    !string.Equals(file.Lfs.Sha256Hex, actualSha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"sha256 mismatch on {file.Path}: expected {file.Lfs.Sha256Hex}, got {actualSha}");
                }

                bytesAcrossModel += file.Size;
            }

            await _reporter.OnCompleteAsync(
                new ModelDownloadComplete(model.Id), CancellationToken.None)
                .ConfigureAwait(false);

            // Run the SQL install glue, if any. Failures here surface as
            // ModelDownloadFailed so the UI shows a single "this didn't
            // work" path — from the user's perspective the model isn't
            // usable until both halves succeed. We deliberately don't
            // delete the downloaded files on install failure: the bytes
            // are valid and re-trying install is cheaper than re-downloading.
            if (!string.IsNullOrEmpty(model.InstallSql))
            {
                await _reporter.OnInstallingAsync(
                    new ModelInstalling(model.Id), CancellationToken.None)
                    .ConfigureAwait(false);

                await _installer.InstallAsync(model, ct).ConfigureAwait(false);

                await _reporter.OnInstalledAsync(
                    new ModelInstalled(model.Id), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await _reporter.OnFailedAsync(
                new ModelDownloadFailed(model.Id, "cancelled"), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download of {ModelId} failed", model.Id);
            await _reporter.OnFailedAsync(
                new ModelDownloadFailed(model.Id, ex.Message), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _active.TryRemove(model.Id, out _);
        }
    }

    private CatalogModel ResolveModel(string modelId)
    {
        CatalogModel? match = _store.Manifest.Models.FirstOrDefault(m => m.Id == modelId);
        return match ?? throw new KeyNotFoundException($"Unknown model id: {modelId}");
    }
}

// Surfaces "you must accept license X before installing" to controller code,
// which maps it to HTTP 412 Precondition Failed.
public sealed class LicenseNotAcceptedException : Exception
{
    public string LicenseId { get; }
    public string LicenseTitle { get; }

    public LicenseNotAcceptedException(string licenseId, string licenseTitle)
        : base($"License '{licenseTitle}' ({licenseId}) has not been accepted.")
    {
        LicenseId = licenseId;
        LicenseTitle = licenseTitle;
    }
}

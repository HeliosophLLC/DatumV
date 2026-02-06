using System.Collections.Concurrent;
using DatumIngest.Web.Hosting;
using DatumIngest.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DatumIngest.Web.ModelLibrary;

internal sealed class ModelDownloadService : IModelDownloadService
{
    private readonly IManifestStore _store;
    private readonly HfHubClient _hub;
    private readonly ILicenseAcceptanceService _licenses;
    private readonly IHubContext<StreamHub, IStreamHubClient> _signalR;
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
        IHubContext<StreamHub, IStreamHubClient> signalR,
        WebHostOptions options,
        ILogger<ModelDownloadService> logger)
    {
        _store = store;
        _hub = hub;
        _licenses = licenses;
        _signalR = signalR;
        _logger = logger;

        _modelsDirectory = options.ModelsDirectory
            ?? Environment.GetEnvironmentVariable("DATUM_MODELS")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DatumIngest", "models");
    }

    public async Task<ModelInstallState> ProbeAsync(string modelId, CancellationToken ct = default)
    {
        CatalogModel model = ResolveModel(modelId);
        string modelDir = Path.Combine(_modelsDirectory, model.Id);

        if (!Directory.Exists(modelDir)) return ModelInstallState.NotInstalled;

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
            // UI shows "needs attention" rather than a false Installed.
            _logger.LogWarning(ex, "Probe of {ModelId} could not reach HF tree", modelId);
            return ModelInstallState.Partial;
        }

        int present = 0;
        foreach (HfTreeEntry entry in tree)
        {
            string localPath = Path.Combine(modelDir, entry.Path);
            if (File.Exists(localPath) && new FileInfo(localPath).Length == entry.Size) present++;
        }

        if (present == tree.Count) return ModelInstallState.Installed;
        if (present == 0) return ModelInstallState.NotInstalled;
        return ModelInstallState.Partial;
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
            await _signalR.Clients.All.OnModelDownloadStarted(
                new ModelDownloadStarted(model.Id, files.Count, totalBytes)).ConfigureAwait(false);

            long bytesAcrossModel = 0;
            for (int i = 0; i < files.Count; i++)
            {
                HfTreeEntry file = files[i];
                string destPath = Path.Combine(modelDir, file.Path);

                int index = i;
                long fileSize = file.Size;
                long bytesAtStart = bytesAcrossModel;
                var progress = new Progress<DownloadByteProgress>(p =>
                {
                    _ = _signalR.Clients.All.OnModelDownloadProgress(new ModelDownloadProgress(
                        ModelId: model.Id,
                        CurrentFile: file.Path,
                        FileIndex: index + 1,
                        FileCount: files.Count,
                        BytesReadInFile: p.BytesRead,
                        BytesTotalInFile: p.BytesTotal ?? fileSize,
                        BytesReadTotal: bytesAtStart + p.BytesRead,
                        BytesTotalAcrossModel: totalBytes));
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

            await _signalR.Clients.All.OnModelDownloadComplete(
                new ModelDownloadComplete(model.Id)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _signalR.Clients.All.OnModelDownloadFailed(
                new ModelDownloadFailed(model.Id, "cancelled")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download of {ModelId} failed", model.Id);
            await _signalR.Clients.All.OnModelDownloadFailed(
                new ModelDownloadFailed(model.Id, ex.Message)).ConfigureAwait(false);
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

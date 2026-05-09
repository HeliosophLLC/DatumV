// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Collections.Concurrent;
using System.Text;

using DatumIngest.Ingestion;
using DatumIngest.ModelLibrary;
using DatumIngest.Pooling;
using DatumIngest.Serialization;

using Microsoft.Extensions.Logging;

namespace DatumIngest.DatasetLibrary;

internal sealed class DatasetDownloadService : IDatasetDownloadService
{
    private readonly IManifestStore _store;
    private readonly IReadOnlyDictionary<string, IModelSourceClient> _sources;
    private readonly ILicenseAcceptanceService _licenses;
    private readonly ILicenseRegistry _licenseRegistry;
    private readonly IDatasetDownloadProgressReporter _reporter;
    private readonly IDatasetPathResolver _paths;
    private readonly IEnumerable<IFileFormat> _fileFormats;
    private readonly Pool _pool;
    private readonly IKeepRawDownloadsPolicy _keepRawPolicy;
    private readonly ILogger<DatasetDownloadService> _logger;

    // Prevents two concurrent installs of the same datasetId.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    /// <inheritdoc/>
    public Func<CancellationToken, Task>? OnVariantsChanged { get; set; }

    /// <inheritdoc/>
    public Action<string>? OnVariantUninstalling { get; set; }

    public DatasetDownloadService(
        IManifestStore store,
        IEnumerable<IModelSourceClient> sourceClients,
        ILicenseAcceptanceService licenses,
        ILicenseRegistry licenseRegistry,
        IDatasetDownloadProgressReporter reporter,
        IDatasetPathResolver paths,
        IEnumerable<IFileFormat> fileFormats,
        Pool pool,
        IKeepRawDownloadsPolicy keepRawPolicy,
        ILogger<DatasetDownloadService> logger)
    {
        _store = store;
        _licenses = licenses;
        _licenseRegistry = licenseRegistry;
        _reporter = reporter;
        _paths = paths;
        _fileFormats = fileFormats;
        _pool = pool;
        _keepRawPolicy = keepRawPolicy;
        _logger = logger;

        // Build the per-type dispatch table once. The same IModelSourceClient
        // family used for model downloads (HuggingFace / GitHub release /
        // HTTPS) is reused here — the clients are agnostic to whether the
        // caller is fetching weights or data. A duplicate SupportedType
        // registration is a DI misconfig — fail loudly.
        Dictionary<string, IModelSourceClient> map = new(StringComparer.Ordinal);
        foreach (IModelSourceClient client in sourceClients)
        {
            if (!map.TryAdd(client.SupportedType, client))
            {
                throw new InvalidOperationException(
                    $"Multiple IModelSourceClient registrations for type '{client.SupportedType}'. " +
                    "Each source type may have exactly one client implementation.");
            }
        }
        _sources = map;
    }

    public Task<DatasetInstallState> ProbeAsync(string variantId, CancellationToken ct = default)
    {
        (_, DatasetVariant variant) = ResolveVariant(variantId);
        string version = variant.Versions[0].Version;
        string ingestedDir = _paths.GetIngestedRoot(variant.Id, version);

        if (!Directory.Exists(ingestedDir))
        {
            return Task.FromResult(DatasetInstallState.NotDownloaded);
        }

        int present = 0;
        foreach (CatalogIngestJob job in variant.Versions[0].Ingest)
        {
            string datumPath = Path.Combine(ingestedDir, $"{job.TableName}.datum");
            if (File.Exists(datumPath)) present++;
        }

        if (present == 0) return Task.FromResult(DatasetInstallState.NotDownloaded);
        if (present < variant.Versions[0].Ingest.Count)
        {
            return Task.FromResult(DatasetInstallState.Partial);
        }
        return Task.FromResult(DatasetInstallState.Installed);
    }

    public async Task<IReadOnlyDictionary<string, DatasetInstallState>> ProbeAllAsync(
        CancellationToken ct = default)
    {
        Dictionary<string, DatasetInstallState> map = new(StringComparer.Ordinal);
        foreach (DatasetEntry e in _store.Manifest.Datasets)
        {
            foreach (DatasetVariant v in e.Variants)
            {
                map[v.Id] = await ProbeAsync(v.Id, ct).ConfigureAwait(false);
            }
        }
        return map;
    }

    public Task InstallAsync(string variantId, CancellationToken ct = default)
    {
        (DatasetEntry entry, DatasetVariant variant) = ResolveVariant(variantId);
        CatalogDatasetVersion version = variant.Versions[0];

        // License gating mirrors the model side. Licenses live at the
        // entry level — every variant inherits the entry's license set.
        // Each license that requiresAcceptance must be accepted before
        // any bytes move.
        foreach (string licenseId in entry.LicenseIds)
        {
            ModelLibrary.CatalogLicense? license = _licenseRegistry.GetMetadata(licenseId);
            if (license is null)
            {
                throw new InvalidOperationException(
                    $"Dataset entry '{entry.Name}' references unknown license {licenseId}.");
            }
            if (license.RequiresAcceptance)
            {
                if (!_licenses.IsAcceptedAsync(licenseId, ct).GetAwaiter().GetResult())
                {
                    throw new LicenseNotAcceptedException(licenseId, license.Title);
                }
            }
        }

        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_active.TryAdd(variantId, cts))
        {
            throw new InvalidOperationException($"Dataset variant {variantId} is already installing.");
        }

        _ = Task.Run(
            () => RunInstallAsync(variant, version, cts.Token),
            CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task UninstallAsync(string variantId, CancellationToken ct = default)
    {
        (_, DatasetVariant variant) = ResolveVariant(variantId);

        // Release the binder's provider for this variant before the file
        // system delete — DatumFileTableProviderV2 keeps the .datum
        // handle open, and Directory.Delete would otherwise throw a
        // sharing violation. Swallow any subscriber exception so a
        // misbehaving binder can't block uninstall.
        Action<string>? handler = OnVariantUninstalling;
        if (handler is not null)
        {
            try { handler(variantId); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Dataset OnVariantUninstalling subscriber threw for {VariantId}; " +
                    "proceeding with directory delete (may hit sharing violation).",
                    variantId);
            }
        }

        string idRoot = Path.Combine(_paths.IngestedDatasetsRoot, variant.Id);
        if (Directory.Exists(idRoot)) Directory.Delete(idRoot, recursive: true);
        await NotifyVariantsChangedAsync().ConfigureAwait(false);
    }

    private async Task NotifyVariantsChangedAsync()
    {
        Func<CancellationToken, Task>? handler = OnVariantsChanged;
        if (handler is null) return;
        try
        {
            await handler(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Dataset OnVariantsChanged subscriber threw; binder may be out of sync.");
        }
    }

    public Task<long> GetPartialBytesAsync(string variantId, CancellationToken ct = default)
    {
        (_, DatasetVariant variant) = ResolveVariant(variantId);
        string rawRoot = Path.Combine(_paths.DatasetsCacheRoot, variant.Id);
        if (!Directory.Exists(rawRoot)) return Task.FromResult(0L);

        long total = 0;
        foreach (string file in Directory.EnumerateFiles(rawRoot, "*.part", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { /* file vanished between enumerate and stat; ignore */ }
        }
        return Task.FromResult(total);
    }

    public async Task<IReadOnlyDictionary<string, long>> GetAllPartialBytesAsync(
        CancellationToken ct = default)
    {
        Dictionary<string, long> map = new(StringComparer.Ordinal);
        foreach (DatasetEntry e in _store.Manifest.Datasets)
        {
            foreach (DatasetVariant v in e.Variants)
            {
                long bytes = await GetPartialBytesAsync(v.Id, ct).ConfigureAwait(false);
                if (bytes > 0) map[v.Id] = bytes;
            }
        }
        return map;
    }

    public Task DeletePartialsAsync(string variantId, CancellationToken ct = default)
    {
        (_, DatasetVariant variant) = ResolveVariant(variantId);
        string rawRoot = Path.Combine(_paths.DatasetsCacheRoot, variant.Id);
        if (!Directory.Exists(rawRoot)) return Task.CompletedTask;

        foreach (string file in Directory.EnumerateFiles(rawRoot, "*.part", SearchOption.AllDirectories))
        {
            try { File.Delete(file); }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not delete partial {File}", file);
            }
        }
        return Task.CompletedTask;
    }

    // ────────────────────── orchestration ──────────────────────

    // Sequential source-fallback loop, then per-job ingest. On failure at
    // any phase, OnFailed fires and partials stay on disk so a retry can
    // reuse them. Mirrors the spirit of ModelDownloadService.RunDownloadAsync,
    // minus the model-side installSql step — datasets bind directly into
    // the configured schema via the catalog (see DatasetEntry.Schema), so
    // once every job's .datum lands on disk the variant is callable.
    private async Task RunInstallAsync(
        DatasetVariant variant,
        CatalogDatasetVersion version,
        CancellationToken ct)
    {
        string rawDir = _paths.GetRawCacheRoot(variant.Id, version.Version);
        string ingestedDir = _paths.GetIngestedRoot(variant.Id, version.Version);
        Directory.CreateDirectory(rawDir);

        StringBuilder failureLog = new();
        bool anySourceSucceeded = false;

        try
        {
            // ── 1. Download phase ──
            for (int sourceIndex = 0; sourceIndex < version.Sources.Count; sourceIndex++)
            {
                ct.ThrowIfCancellationRequested();

                ModelLibrary.CatalogSource source = version.Sources[sourceIndex];
                IModelSourceClient client;
                try
                {
                    client = ResolveClient(source);
                }
                catch (Exception ex)
                {
                    failureLog.AppendLine($"source[{sourceIndex}] ({source.GetType().Name}): {ex.Message}");
                    continue;
                }

                try
                {
                    await TryDownloadFromSourceAsync(
                        variant, rawDir, source, client, sourceIndex, ct).ConfigureAwait(false);
                    anySourceSucceeded = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    string label = $"source[{sourceIndex}] ({client.SupportedType})";
                    _logger.LogWarning(ex, "{VariantId}: {Label} failed; advancing to next source",
                        variant.Id, label);
                    failureLog.AppendLine($"{label}: {ex.Message}");
                }
            }

            if (!anySourceSucceeded)
            {
                string aggregate = failureLog.Length == 0
                    ? "no sources configured"
                    : "all sources failed: " + failureLog.ToString().TrimEnd();
                await _reporter.OnFailedAsync(
                    new DatasetDownloadFailed(variant.Id, aggregate), CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            await _reporter.OnCompleteAsync(
                new DatasetDownloadComplete(variant.Id), CancellationToken.None)
                .ConfigureAwait(false);

            // ── 2. Ingest phase ──
            Directory.CreateDirectory(ingestedDir);
            int jobCount = version.Ingest.Count;
            for (int jobIndex = 0; jobIndex < jobCount; jobIndex++)
            {
                ct.ThrowIfCancellationRequested();
                CatalogIngestJob job = version.Ingest[jobIndex];

                await _reporter.OnIngestingAsync(
                    new DatasetIngesting(variant.Id, job.TableName, jobIndex + 1, jobCount), ct)
                    .ConfigureAwait(false);

                IngestionResult result = await RunIngestJobAsync(
                    rawDir, ingestedDir, job, ct).ConfigureAwait(false);

                await _reporter.OnTableIngestedAsync(
                    new DatasetTableIngested(
                        variant.Id, job.TableName, result.RowCount, result.BytesWritten),
                    CancellationToken.None).ConfigureAwait(false);
            }

            // ── 3. Raw-cache cleanup per user preference ──
            KeepRawDownloadsMode keepMode = await _keepRawPolicy.GetAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (keepMode == KeepRawDownloadsMode.Never && Directory.Exists(rawDir))
            {
                try { Directory.Delete(rawDir, recursive: true); }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex,
                        "{VariantId}: keepRawDownloads=never but could not delete raw cache at {RawDir}",
                        variant.Id, rawDir);
                }
            }

            await _reporter.OnInstalledAsync(
                new DatasetInstalled(variant.Id), CancellationToken.None)
                .ConfigureAwait(false);
            await NotifyVariantsChangedAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _reporter.OnFailedAsync(
                new DatasetDownloadFailed(variant.Id, "cancelled"), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Post-download step failed for {VariantId}", variant.Id);
            await _reporter.OnFailedAsync(
                new DatasetDownloadFailed(variant.Id, ex.Message), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _active.TryRemove(variant.Id, out _);
        }
    }

    // One ingest job: route the named source path through the engine's
    // FormatRegistry (e.g. MediaBagDeserializer for a homogeneous media
    // archive, ParquetFormat for a columnar source) and write the resulting
    // RowBatches to `<ingestedDir>/<tableName>.datum` (+ optional sidecar
    // `.datum-blob`).
    private async Task<IngestionResult> RunIngestJobAsync(
        string rawDir,
        string ingestedDir,
        CatalogIngestJob job,
        CancellationToken ct)
    {
        string sourcePath = Path.Combine(rawDir, job.SourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"Ingest job '{job.TableName}' expected source at '{sourcePath}' but no file was " +
                "found. The download phase should have produced it — check the source declaration in " +
                "the dataset catalog.",
                sourcePath);
        }

        string destPath = Path.Combine(ingestedDir, $"{job.TableName}.datum");

        // FormatRegistry is per-context (not a singleton) — build a fresh
        // one per job so registered formats stay isolated. The dependency
        // surface is one IEnumerable<IFileFormat>; whichever formats are
        // wired in DI get picked up.
        FormatRegistry registry = new(_fileFormats);
        Ingester ingester = new(registry, _pool);

        using FileFormatDescriptor source = new(sourcePath);
        OutputDescriptor destination = new(destPath);

        return await ingester.IngestAsync(source, destination, ct).ConfigureAwait(false);
    }

    // Single-source download attempt. Throws on any failure — the outer
    // loop catches and advances to the next source. Mirrors
    // ModelDownloadService.TryDownloadFromSourceAsync for the per-file
    // download bookkeeping + reporter wiring.
    private async Task TryDownloadFromSourceAsync(
        DatasetVariant variant,
        string rawDir,
        ModelLibrary.CatalogSource source,
        IModelSourceClient client,
        int sourceIndex,
        CancellationToken ct)
    {
        IReadOnlyList<SourceFile> files = await client.ListFilesAsync(source, ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"source[{sourceIndex}] returned an empty file inventory");
        }

        long totalBytes = files.Sum(f => f.Size);

        await _reporter.OnStartedAsync(
            new DatasetDownloadStarted(variant.Id, files.Count, totalBytes), ct)
            .ConfigureAwait(false);

        long bytesAcrossDataset = 0;
        for (int i = 0; i < files.Count; i++)
        {
            SourceFile file = files[i];
            string destPath = Path.Combine(rawDir, file.Path);

            if (File.Exists(destPath))
            {
                bool sizeMatches = file.Size == 0 ||
                    new FileInfo(destPath).Length == file.Size;
                if (sizeMatches)
                {
                    long countedSize = file.Size > 0 ? file.Size : 0;
                    bytesAcrossDataset += countedSize;
                    _ = _reporter.OnProgressAsync(new DatasetDownloadProgress(
                        DatasetId: variant.Id,
                        CurrentFile: file.Path,
                        FileIndex: i + 1,
                        FileCount: files.Count,
                        BytesReadInFile: countedSize,
                        BytesTotalInFile: countedSize,
                        BytesReadTotal: bytesAcrossDataset,
                        BytesTotalAcrossDataset: totalBytes), ct).AsTask();
                    continue;
                }
            }

            int index = i;
            long fileSize = file.Size;
            long bytesAtStart = bytesAcrossDataset;
            CancellationToken progressCt = ct;
            var progress = new Progress<DownloadByteProgress>(p =>
            {
                _ = _reporter.OnProgressAsync(new DatasetDownloadProgress(
                    DatasetId: variant.Id,
                    CurrentFile: file.Path,
                    FileIndex: index + 1,
                    FileCount: files.Count,
                    BytesReadInFile: p.BytesRead,
                    BytesTotalInFile: p.BytesTotal ?? fileSize,
                    BytesReadTotal: bytesAtStart + p.BytesRead,
                    BytesTotalAcrossDataset: totalBytes), progressCt).AsTask();
            });

            string actualSha = await client.DownloadFileAsync(
                source, file, destPath, progress, ct).ConfigureAwait(false);

            if (file.Sha256 is { } expected &&
                !string.Equals(expected, actualSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"sha256 mismatch on {file.Path}: expected {expected}, got {actualSha}");
            }

            bytesAcrossDataset += file.Size > 0 ? file.Size : 0;
        }
    }

    private IModelSourceClient ResolveClient(ModelLibrary.CatalogSource source)
    {
        string type = source switch
        {
            ModelLibrary.HuggingFaceSource => "huggingface",
            ModelLibrary.GithubReleaseSource => "github-release",
            ModelLibrary.HttpsSource => "https",
            _ => throw new InvalidOperationException(
                $"Unknown CatalogSource subtype '{source.GetType().Name}'. " +
                "Add the type to DatasetDownloadService.ResolveClient's switch and register " +
                "an IModelSourceClient for its discriminator.")
        };
        if (!_sources.TryGetValue(type, out IModelSourceClient? client))
        {
            throw new InvalidOperationException(
                $"No IModelSourceClient registered for source type '{type}'. " +
                "Check the IServiceCollection wiring in AddDatasetLibrary.");
        }
        return client;
    }

    // Looks up a variant by its id and returns the (entry, variant) pair.
    // The entry side carries license + display metadata; the variant side
    // is the install handle (sources, ingest jobs, paths). The lookup
    // table is populated once at manifest-load time so the per-call cost
    // is a dictionary hit.
    private (DatasetEntry Entry, DatasetVariant Variant) ResolveVariant(string variantId)
    {
        return _store.FindVariant(variantId)
            ?? throw new KeyNotFoundException($"Unknown dataset variant id: {variantId}");
    }
}

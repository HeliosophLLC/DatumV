// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Collections.Concurrent;
using System.Text;

using DatumIngest.Execution;
using DatumIngest.Ingestion;
using DatumIngest.Manifest;
using DatumIngest.Model;
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
    private readonly SqlIngestExecutor _sqlIngestExecutor;
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
        SqlIngestExecutor sqlIngestExecutor,
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
        _sqlIngestExecutor = sqlIngestExecutor;
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

        // Count .datum files that are non-empty. A zero-byte file means the
        // writer never made it past Initialize — probably a crash mid-ingest
        // before the staging→version rename was wired. We treat it as Partial
        // so the UI surfaces a reinstall affordance instead of letting the
        // catalog binder fail to mount a "ghost" variant.
        int present = 0;
        foreach (CatalogIngestJob job in variant.Versions[0].Ingest)
        {
            string datumPath = Path.Combine(ingestedDir, $"{job.TableName}.datum");
            if (File.Exists(datumPath) && new FileInfo(datumPath).Length > 0)
            {
                present++;
            }
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

    public Task<int> SweepStagingDirsAsync(CancellationToken ct = default)
    {
        string root = _paths.IngestedDatasetsRoot;
        if (!Directory.Exists(root)) return Task.FromResult(0);

        int swept = 0;
        foreach (string variantDir in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            foreach (string staging in Directory.EnumerateDirectories(variantDir, ".staging-*"))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                    swept++;
                    _logger.LogInformation("Swept orphan ingest staging dir {StagingDir}", staging);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex,
                        "Could not delete orphan staging dir {StagingDir}; will retry on next boot",
                        staging);
                }
            }
        }
        return Task.FromResult(swept);
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
            // Atomic install: write the .datum + .datum-blob files to a
            // sibling `.staging-<guid>/` dir, then Directory.Move it onto
            // the final version path after every job has finalized. On
            // Windows the move is a single rename within the same volume
            // (same parent dir), so the catalog binder either sees the
            // full version dir or nothing — no half-written .datums for
            // RebuildAsync to choke on. Process kills mid-ingest leave the
            // staging dir orphaned; SweepStagingDirs (called at startup
            // from the catalog init service) reaps those before the binder
            // probes.
            string variantDir = Path.GetDirectoryName(ingestedDir)!;
            Directory.CreateDirectory(variantDir);
            string stagingDir = Path.Combine(variantDir, $".staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDir);
            bool stagingCommitted = false;

            try
            {
                int jobCount = version.Ingest.Count;
                for (int jobIndex = 0; jobIndex < jobCount; jobIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    CatalogIngestJob job = version.Ingest[jobIndex];

                    await _reporter.OnIngestingAsync(
                        new DatasetIngesting(variant.Id, job.TableName, jobIndex + 1, jobCount), ct)
                        .ConfigureAwait(false);

                    IngestionResult result = await RunIngestJobAsync(
                        variant.Id, rawDir, stagingDir, job, ct).ConfigureAwait(false);

                    await _reporter.OnTableIngestedAsync(
                        new DatasetTableIngested(
                            variant.Id, job.TableName, result.RowCount, result.BytesWritten),
                        CancellationToken.None).ConfigureAwait(false);
                }

                // All jobs landed in staging — flip into place. If the
                // destination already exists (rare: a concurrent install
                // got there first), drop our staging dir and keep theirs
                // rather than racing the move.
                if (Directory.Exists(ingestedDir))
                {
                    _logger.LogInformation(
                        "{VariantId}: version dir already present at {Dest}; discarding staging dir",
                        variant.Id, ingestedDir);
                }
                else
                {
                    Directory.Move(stagingDir, ingestedDir);
                }
                stagingCommitted = true;
            }
            finally
            {
                if (!stagingCommitted && Directory.Exists(stagingDir))
                {
                    try { Directory.Delete(stagingDir, recursive: true); }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex,
                            "{VariantId}: failed to clean up staging dir at {StagingDir}; " +
                            "the startup sweep will retry on next boot",
                            variant.Id, stagingDir);
                    }
                }
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
        string variantId,
        string rawDir,
        string ingestedDir,
        CatalogIngestJob job,
        CancellationToken ct)
    {
        // SQL-shape jobs route through SqlIngestExecutor; direct-shape
        // through the engine's FormatRegistry-driven Ingester below. The
        // manifest validator guarantees exactly one shape is set per
        // job, so the branch is total.
        if (!string.IsNullOrWhiteSpace(job.SqlFile))
        {
            return await RunSqlIngestJobAsync(variantId, rawDir, ingestedDir, job, ct).ConfigureAwait(false);
        }
        string sourcePath = Path.Combine(rawDir, job.SourcePath!);
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

    // SQL-shape ingest. Reads the script from `<manifestDir>/<job.SqlFile>`
    // and binds either:
    //   - `$archive` + `$archive_stem` for single-archive jobs (legacy
    //     shape, used by LJSpeech), or
    //   - `$<name>` for each entry in `archives` for multi-archive jobs
    //     (used by MNIST / Fashion-MNIST).
    // Streams rows from the planner straight into a fresh .datum. The
    // IngestionResult uses synthetic stats — Manifest and Schema stay
    // empty; collection for SQL ingest is a follow-up.
    private async Task<IngestionResult> RunSqlIngestJobAsync(
        string variantId,
        string rawDir,
        string ingestedDir,
        CatalogIngestJob job,
        CancellationToken ct)
    {
        string sqlScriptPath = Path.Combine(_store.ManifestDirectory, job.SqlFile!);
        if (!File.Exists(sqlScriptPath))
        {
            throw new FileNotFoundException(
                $"Ingest job '{job.TableName}' references sqlFile '{job.SqlFile}' " +
                $"but no file was found at '{sqlScriptPath}'.",
                sqlScriptPath);
        }
        string sql = await File.ReadAllTextAsync(sqlScriptPath, ct).ConfigureAwait(false);

        Dictionary<string, ParameterValue> parameters = new(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(job.Archive))
        {
            string archivePath = Path.Combine(rawDir, job.Archive);
            if (!File.Exists(archivePath))
            {
                throw new FileNotFoundException(
                    $"Ingest job '{job.TableName}' SQL targets archive '{job.Archive}' " +
                    $"but the file '{archivePath}' is missing after download.",
                    archivePath);
            }
            parameters["archive"] = new StringParameter(archivePath);
            // Only bind $archive_stem if the recipe references it — the
            // ParameterBinder rejects unreferenced parameters as a typo guard,
            // and folder-of-classes recipes (EuroSAT, Oxford Pets) don't need
            // a stem since the class lives inside the path, not the archive
            // basename. Substring check is good enough — the token is unique.
            if (sql.Contains("$archive_stem", StringComparison.Ordinal))
            {
                string archiveStem = StripCompoundArchiveExtensions(job.Archive);
                parameters["archive_stem"] = new StringParameter(archiveStem);
            }
        }
        else
        {
            // Multi-archive shape — manifest validator guarantees Archives
            // is non-null and non-empty when Archive is absent.
            foreach ((string paramName, string archiveRel) in job.Archives!)
            {
                string archivePath = Path.Combine(rawDir, archiveRel);
                if (!File.Exists(archivePath))
                {
                    throw new FileNotFoundException(
                        $"Ingest job '{job.TableName}' SQL targets archives['{paramName}'] = " +
                        $"'{archiveRel}' but the file '{archivePath}' is missing after download.",
                        archivePath);
                }
                parameters[paramName] = new StringParameter(archivePath);
            }
        }

        string destPath = Path.Combine(ingestedDir, $"{job.TableName}.datum");
        // Fan the executor's row-count callback out to the reporter so the
        // UI sees a live counter while the SQL ingest is running. Fire-and-
        // forget — the executor invokes onRowProgress synchronously and we
        // don't want to block batch processing on a SignalR round-trip.
        // OnIngestProgressAsync is responsible for its own ordering across
        // ticks (the reporter implementation can drop / coalesce as needed).
        void EmitRowProgress(long rowsSoFar)
        {
            _ = _reporter.OnIngestProgressAsync(
                new DatasetIngestProgress(variantId, job.TableName, rowsSoFar),
                CancellationToken.None);
        }
        SqlIngestResult result = await _sqlIngestExecutor.ExecuteAsync(
            sql, parameters, destPath, onRowProgress: EmitRowProgress, ct).ConfigureAwait(false);

        long bytesWritten;
        try { bytesWritten = new FileInfo(destPath).Length; }
        catch (FileNotFoundException) { bytesWritten = 0; }

        // Construct a minimally-populated IngestionResult. The
        // OnTableIngested event reads only RowCount + BytesWritten; the
        // rest (schema / manifest / sample / scan pass metrics) stays
        // empty in this v1 SQL path — adding stats / sampling collection
        // for SQL ingest is a follow-up.
        QueryResultsManifest emptyManifest = new()
        {
            RowCount = result.RowCount,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = Array.Empty<FeatureManifest>(),
        };
        PassMetrics ingestPass = new(
            RowCount: result.RowCount,
            BatchCount: result.BatchCount,
            BytesRead: 0,
            ArenaBytesWritten: 0,
            Elapsed: TimeSpan.Zero);
        return new IngestionResult(
            OutputPath: destPath,
            RowCount: result.RowCount,
            BytesWritten: bytesWritten,
            Schema: new Schema([]),
            Manifest: emptyManifest,
            Sample: null,
            ScanPass: null,
            IngestPass: ingestPass);
    }

    // Strip compound archive extensions in the order they appear, so
    // `LJSpeech-1.1.tar.gz` → `LJSpeech-1.1`. Single extensions handled
    // by ChangeExtension. Anything not on the recognised list is left
    // alone (the manifest validator should reject those at load time
    // once we tighten the schema; for now, pass-through is safer than
    // partial stripping).
    private static string StripCompoundArchiveExtensions(string archiveName)
    {
        string name = Path.GetFileName(archiveName);
        string[] doubleExtensions = [".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst"];
        foreach (string ext in doubleExtensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^ext.Length];
            }
        }
        string[] singleExtensions = [".zip", ".tgz", ".tbz2", ".gz", ".bz2", ".xz", ".zst", ".tar"];
        foreach (string ext in singleExtensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^ext.Length];
            }
        }
        return name;
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

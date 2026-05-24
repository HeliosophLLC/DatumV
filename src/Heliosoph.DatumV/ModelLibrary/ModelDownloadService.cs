// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Collections.Concurrent;
using System.Text;

using Heliosoph.DatumV.Models.Python;

using Microsoft.Extensions.Logging;

namespace Heliosoph.DatumV.ModelLibrary;

internal sealed class ModelDownloadService : IModelDownloadService
{
    private readonly IManifestStore _store;
    private readonly IReadOnlyDictionary<string, IModelSourceClient> _sources;
    private readonly ILicenseAcceptanceService _licenses;
    private readonly ILicenseRegistry _licenseRegistry;
    private readonly IDownloadProgressReporter _reporter;
    private readonly IModelInstaller _installer;
    private readonly IPythonEnvironmentManager _python;
    private readonly IModelPathResolver _paths;
    private readonly ILogger<ModelDownloadService> _logger;

    // Prevents two concurrent installs of the same variantId. The value is a
    // CancellationTokenSource so a future "cancel download" RPC can flip
    // it; today we only block re-entry.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public ModelDownloadService(
        IManifestStore store,
        IEnumerable<IModelSourceClient> sourceClients,
        ILicenseAcceptanceService licenses,
        ILicenseRegistry licenseRegistry,
        IDownloadProgressReporter reporter,
        IModelInstaller installer,
        IPythonEnvironmentManager python,
        IModelPathResolver paths,
        ILogger<ModelDownloadService> logger)
    {
        _store = store;
        _licenses = licenses;
        _licenseRegistry = licenseRegistry;
        _reporter = reporter;
        _installer = installer;
        _python = python;
        _logger = logger;
        _paths = paths;

        // Build the per-type dispatch table once.
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

    public async Task<ModelInstallState> ProbeAsync(string variantId, CancellationToken ct = default)
    {
        (CatalogEntry entry, CatalogVariant variant) = ResolveVariant(variantId);

        // Placeholders short-circuit before any I/O.
        if (variant.Placeholder) return ModelInstallState.NotDownloaded;

        string recommendedVersion = variant.Versions[0].Version;
        string variantDir = _paths.GetModelRoot(variant.Id, recommendedVersion);
        if (!Directory.Exists(variantDir)) return ModelInstallState.NotDownloaded;

        CatalogSource primary = variant.Sources[0];
        IModelSourceClient client = ResolveClient(primary);

        IReadOnlyList<SourceFile> inventory;
        try
        {
            inventory = await client.ListFilesAsync(primary, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Probe of {VariantId} could not list files from primary source ({Type})",
                variantId, client.SupportedType);
            return ModelInstallState.Partial;
        }

        int present = 0;
        foreach (SourceFile sf in inventory)
        {
            string localPath = Path.Combine(variantDir, sf.Path);
            if (!File.Exists(localPath)) continue;
            if (sf.Size == 0) { present++; continue; }
            if (new FileInfo(localPath).Length == sf.Size) present++;
        }

        if (present == 0) return ModelInstallState.NotDownloaded;
        if (present < inventory.Count) return ModelInstallState.Partial;

        bool registered = await _installer.IsInstalledAsync(variant, ct).ConfigureAwait(false);
        return registered ? ModelInstallState.Installed : ModelInstallState.Downloaded;
    }

    public async Task InstallAsync(string variantId, CancellationToken ct = default)
    {
        (CatalogEntry entry, CatalogVariant variant) = ResolveVariant(variantId);

        if (variant.Placeholder)
        {
            throw new InvalidOperationException(
                $"Variant {variantId} is a placeholder — its source repo has not been uploaded yet.");
        }

        // Gate: every license that requiresAcceptance must be accepted.
        // License terms live at entry level (every variant shares).
        foreach (string licenseId in entry.LicenseIds)
        {
            CatalogLicense? license = _licenseRegistry.GetMetadata(licenseId);
            if (license is null)
            {
                throw new InvalidOperationException(
                    $"Entry '{entry.Name}' references unknown license {licenseId}.");
            }
            if (license.RequiresAcceptance &&
                !await _licenses.IsAcceptedAsync(licenseId, ct).ConfigureAwait(false))
            {
                throw new LicenseNotAcceptedException(licenseId, license.Title);
            }
        }

        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_active.TryAdd(variantId, cts))
        {
            throw new InvalidOperationException($"Variant {variantId} is already downloading.");
        }

        _ = Task.Run(
            () => RunDownloadAsync(variant, variant.Versions[0], pinnedMode: false, cts.Token),
            CancellationToken.None);
    }

    public async Task InstallPinnedAsync(string variantId, string version, CancellationToken ct = default)
    {
        (CatalogEntry entry, CatalogVariant variant) = ResolveVariant(variantId);
        if (variant.Placeholder)
        {
            throw new InvalidOperationException(
                $"Variant {variantId} is a placeholder — its source repo has not been uploaded yet.");
        }

        CatalogVersion? pinned = null;
        foreach (CatalogVersion v in variant.Versions)
        {
            if (string.Equals(v.Version, version, StringComparison.Ordinal))
            {
                pinned = v;
                break;
            }
        }
        if (pinned is null)
        {
            throw new InvalidOperationException(
                $"Variant {variantId} has no version '{version}'. Available versions: " +
                string.Join(", ", variant.Versions.Select(v => v.Version)));
        }

        foreach (string licenseId in entry.LicenseIds)
        {
            CatalogLicense? license = _licenseRegistry.GetMetadata(licenseId);
            if (license is null)
            {
                throw new InvalidOperationException(
                    $"Entry '{entry.Name}' references unknown license {licenseId}.");
            }
            if (license.RequiresAcceptance &&
                !await _licenses.IsAcceptedAsync(licenseId, ct).ConfigureAwait(false))
            {
                throw new LicenseNotAcceptedException(licenseId, license.Title);
            }
        }

        string key = $"{variantId}@{version}";
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_active.TryAdd(key, cts))
        {
            throw new InvalidOperationException($"Variant {variantId} version '{version}' is already downloading.");
        }

        _ = Task.Run(
            () => RunDownloadAsync(variant, pinned, pinnedMode: true, cts.Token),
            CancellationToken.None);
    }

    public Task UninstallAsync(string variantId, CancellationToken ct = default)
    {
        (CatalogEntry _, CatalogVariant variant) = ResolveVariant(variantId);
        string idRoot = Path.Combine(_paths.ModelsRoot, variant.Id);
        if (Directory.Exists(idRoot)) Directory.Delete(idRoot, recursive: true);
        return Task.CompletedTask;
    }

    public async Task ActivateVersionAsync(
        string variantId, string version, CancellationToken ct = default)
    {
        (CatalogEntry _, CatalogVariant variant) = ResolveVariant(variantId);
        CatalogVersion target = FindVersion(variant, version);

        if (!_paths.IsVersionOnDisk(variantId, version))
        {
            throw new InvalidOperationException(
                $"Variant {variantId} version '{version}' is not on disk. Install it first.");
        }

        string? currentActive = _paths.GetActiveVersion(variantId);
        if (string.Equals(currentActive, version, StringComparison.Ordinal))
        {
            return;
        }

        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_active.TryAdd(variantId, cts))
        {
            throw new InvalidOperationException(
                $"Variant {variantId} has an install in flight; try again shortly.");
        }

        try
        {
            if (currentActive is not null)
            {
                CatalogVersion? outgoing = TryFindVersion(variant, currentActive);
                if (outgoing?.Models is { Count: > 0 } outgoingModels)
                {
                    List<string> outgoingBare = new(outgoingModels.Count);
                    foreach (CatalogVersionModel vm in outgoingModels)
                    {
                        outgoingBare.Add(vm.Identifier);
                    }
                    await _installer.DropModelsAsync(outgoingBare, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                if (outgoing is not null
                    && !string.IsNullOrEmpty(outgoing.InstallSql)
                    && _paths.IsVersionOnDisk(variantId, currentActive))
                {
                    string? previousPin = ModelInstallContext.CurrentVersionPin;
                    ModelInstallContext.CurrentVersionPin = currentActive;
                    try
                    {
                        await _installer.InstallAsync(
                            variant, outgoing, pinnedMode: true, cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Activate {VariantId} {Version}: pinned re-register of outgoing {Outgoing} failed",
                            variantId, version, currentActive);
                    }
                    finally
                    {
                        ModelInstallContext.CurrentVersionPin = previousPin;
                    }
                }
            }

            if (!string.IsNullOrEmpty(target.InstallSql))
            {
                string? previousPin = ModelInstallContext.CurrentVersionPin;
                IReadOnlyList<string> observed;
                ModelInstallContext.CurrentVersionPin = version;
                try
                {
                    observed = await _installer.InstallAsync(
                        variant, target, pinnedMode: false, cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    ModelInstallContext.CurrentVersionPin = previousPin;
                }

                if (target.Models is { Count: > 0 } expectedDeclarations)
                {
                    string mismatch = CrossCheckObservedIdentifiers(
                        expectedDeclarations, observed, version, pinnedMode: false);
                    if (mismatch.Length > 0)
                    {
                        await _installer.DropModelsAsync(observed, CancellationToken.None)
                            .ConfigureAwait(false);
                        throw new InvalidOperationException(mismatch);
                    }
                }
            }
        }
        finally
        {
            _active.TryRemove(variantId, out _);
        }
    }

    public async Task DeleteVersionAsync(
        string variantId, string version, CancellationToken ct = default)
    {
        (CatalogEntry _, CatalogVariant variant) = ResolveVariant(variantId);

        if (!_paths.IsVersionOnDisk(variantId, version)
            && !string.Equals(_paths.GetActiveVersion(variantId), version, StringComparison.Ordinal))
        {
            return;
        }

        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_active.TryAdd(variantId, cts))
        {
            throw new InvalidOperationException(
                $"Variant {variantId} has an install in flight; try again shortly.");
        }

        try
        {
            CatalogVersion? versionEntry = TryFindVersion(variant, version);
            bool isActive = string.Equals(
                _paths.GetActiveVersion(variantId), version, StringComparison.Ordinal);

            if (versionEntry?.Models is { Count: > 0 } declaredModels)
            {
                List<string> toDrop = new(declaredModels.Count);
                foreach (CatalogVersionModel vm in declaredModels)
                {
                    toDrop.Add(isActive ? vm.Identifier : vm.EffectivePinnedAs(version));
                }
                await _installer.DropModelsAsync(toDrop, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            string versionDir = Path.Combine(_paths.ModelsRoot, variantId, version);
            if (Directory.Exists(versionDir))
            {
                Directory.Delete(versionDir, recursive: true);
            }
        }
        finally
        {
            _active.TryRemove(variantId, out _);
        }
    }

    private static CatalogVersion FindVersion(CatalogVariant variant, string version)
        => TryFindVersion(variant, version)
            ?? throw new InvalidOperationException(
                $"Variant {variant.Id} has no version '{version}'. Available versions: " +
                string.Join(", ", variant.Versions.Select(v => v.Version)));

    private static CatalogVersion? TryFindVersion(CatalogVariant variant, string version)
    {
        foreach (CatalogVersion v in variant.Versions)
        {
            if (string.Equals(v.Version, version, StringComparison.Ordinal)) return v;
        }
        return null;
    }

    public async Task<IReadOnlyDictionary<string, ModelInstallState>> ProbeAllAsync(
        CancellationToken ct = default)
    {
        List<CatalogVariant> variants = [];
        foreach (CatalogEntry e in _store.Manifest.Entries)
        {
            foreach (CatalogVariant v in e.Variants) variants.Add(v);
        }
        Task<(string Id, ModelInstallState State)>[] tasks =
            new Task<(string, ModelInstallState)>[variants.Count];
        for (int i = 0; i < variants.Count; i++)
        {
            CatalogVariant v = variants[i];
            tasks[i] = ProbeOne(v.Id, ct);
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

    public Task<long> GetPartialBytesAsync(string variantId, CancellationToken ct = default)
    {
        (CatalogEntry _, CatalogVariant variant) = ResolveVariant(variantId);
        string variantDir = Path.Combine(_paths.ModelsRoot, variant.Id);
        if (!Directory.Exists(variantDir)) return Task.FromResult(0L);

        long total = 0;
        foreach (string file in Directory.EnumerateFiles(variantDir, "*.part", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { /* file vanished; ignore */ }
        }
        return Task.FromResult(total);
    }

    public async Task<IReadOnlyDictionary<string, long>> GetAllPartialBytesAsync(
        CancellationToken ct = default)
    {
        Dictionary<string, long> map = new(StringComparer.Ordinal);
        foreach (CatalogEntry e in _store.Manifest.Entries)
        {
            foreach (CatalogVariant v in e.Variants)
            {
                long bytes = await GetPartialBytesAsync(v.Id, ct).ConfigureAwait(false);
                if (bytes > 0) map[v.Id] = bytes;
            }
        }
        return map;
    }

    public Task DeletePartialsAsync(string variantId, CancellationToken ct = default)
    {
        (CatalogEntry _, CatalogVariant variant) = ResolveVariant(variantId);
        string variantDir = Path.Combine(_paths.ModelsRoot, variant.Id);
        if (!Directory.Exists(variantDir)) return Task.CompletedTask;

        foreach (string file in Directory.EnumerateFiles(variantDir, "*.part", SearchOption.AllDirectories))
        {
            try { File.Delete(file); }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not delete partial {File}", file);
            }
        }
        return Task.CompletedTask;
    }

    private async Task RunDownloadAsync(
        CatalogVariant variant,
        CatalogVersion target,
        bool pinnedMode,
        CancellationToken ct)
    {
        string version = target.Version;
        string variantDir = _paths.GetModelRoot(variant.Id, version);
        Directory.CreateDirectory(variantDir);
        string activeKey = pinnedMode ? $"{variant.Id}@{version}" : variant.Id;

        StringBuilder failureLog = new();
        bool anySourceSucceeded = target.Sources.Count == 0;

        if (target.Sources.Count == 0)
        {
            await _reporter.OnStartedAsync(
                new ModelDownloadStarted(variant.Id, FileCount: 0, TotalBytes: 0),
                ct).ConfigureAwait(false);
        }

        try
        {
            for (int sourceIndex = 0; sourceIndex < target.Sources.Count; sourceIndex++)
            {
                ct.ThrowIfCancellationRequested();

                CatalogSource source = target.Sources[sourceIndex];
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
                        variant, variantDir, source, client, sourceIndex, ct).ConfigureAwait(false);
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
                    _logger.LogWarning(ex, "{VariantId}: {Label} failed; advancing to next source", variant.Id, label);
                    failureLog.AppendLine($"{label}: {ex.Message}");
                }
            }

            if (!anySourceSucceeded)
            {
                string aggregate = failureLog.Length == 0
                    ? "no sources configured"
                    : "all sources failed: " + failureLog.ToString().TrimEnd();
                await _reporter.OnFailedAsync(
                    new ModelDownloadFailed(variant.Id, aggregate), CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            await _reporter.OnCompleteAsync(
                new ModelDownloadComplete(variant.Id), CancellationToken.None)
                .ConfigureAwait(false);

            if (variant.Python is { } pythonSpec)
            {
                await _reporter.OnInstallingAsync(
                    new ModelInstalling(variant.Id), CancellationToken.None)
                    .ConfigureAwait(false);

                await _python.EnsureVenvAsync(
                    venvName: variant.Id,
                    pythonVersion: pythonSpec.PythonVersion,
                    requirements: pythonSpec.Requirements,
                    cancellationToken: ct).ConfigureAwait(false);

                await _reporter.OnInstalledAsync(
                    new ModelInstalled(variant.Id), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(target.InstallSql))
            {
                await _reporter.OnInstallingAsync(
                    new ModelInstalling(variant.Id), CancellationToken.None)
                    .ConfigureAwait(false);

                string? previousPin = ModelInstallContext.CurrentVersionPin;
                IReadOnlyList<string> observed;
                ModelInstallContext.CurrentVersionPin = version;
                try
                {
                    observed = await _installer.InstallAsync(
                        variant, target, pinnedMode, ct).ConfigureAwait(false);
                }
                finally
                {
                    ModelInstallContext.CurrentVersionPin = previousPin;
                }

                if (target.Models is { Count: > 0 } expectedDeclarations)
                {
                    string mismatch = CrossCheckObservedIdentifiers(
                        expectedDeclarations, observed, target.Version, pinnedMode);
                    if (mismatch.Length > 0)
                    {
                        await _installer.DropModelsAsync(observed, CancellationToken.None)
                            .ConfigureAwait(false);
                        throw new InvalidOperationException(mismatch);
                    }
                }

                await _reporter.OnInstalledAsync(
                    new ModelInstalled(variant.Id), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await _reporter.OnFailedAsync(
                new ModelDownloadFailed(variant.Id, "cancelled"), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Post-download step failed for {VariantId}", variant.Id);
            await _reporter.OnFailedAsync(
                new ModelDownloadFailed(variant.Id, ex.Message), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _active.TryRemove(activeKey, out _);
        }
    }

    private static string CrossCheckObservedIdentifiers(
        IReadOnlyList<CatalogVersionModel> declared,
        IReadOnlyList<string> observed,
        string versionString,
        bool pinnedMode)
    {
        HashSet<string> expected = new(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogVersionModel vm in declared)
        {
            expected.Add(pinnedMode ? vm.EffectivePinnedAs(versionString) : vm.Identifier);
        }

        HashSet<string> actual = new(observed, StringComparer.OrdinalIgnoreCase);

        List<string> missing = [];
        foreach (string id in expected)
        {
            if (!actual.Contains(id)) { missing.Add(id); }
        }
        List<string> extra = [];
        foreach (string id in actual)
        {
            if (!expected.Contains(id)) { extra.Add(id); }
        }
        if (missing.Count == 0 && extra.Count == 0) { return string.Empty; }

        List<string> parts = [];
        if (missing.Count > 0) { parts.Add($"missing [{string.Join(", ", missing)}]"); }
        if (extra.Count > 0) { parts.Add($"unexpected [{string.Join(", ", extra)}]"); }
        return $"installSql identifier set does not match catalog.json declaration: {string.Join("; ", parts)}.";
    }

    private async Task TryDownloadFromSourceAsync(
        CatalogVariant variant,
        string variantDir,
        CatalogSource source,
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
            new ModelDownloadStarted(variant.Id, files.Count, totalBytes), ct)
            .ConfigureAwait(false);

        long bytesAcrossModel = 0;
        for (int i = 0; i < files.Count; i++)
        {
            SourceFile file = files[i];
            string destPath = Path.Combine(variantDir, file.Path);

            if (File.Exists(destPath))
            {
                bool sizeMatches = file.Size == 0 ||
                    new FileInfo(destPath).Length == file.Size;
                if (sizeMatches)
                {
                    long countedSize = file.Size > 0 ? file.Size : 0;
                    bytesAcrossModel += countedSize;
                    _ = _reporter.OnProgressAsync(new ModelDownloadProgress(
                        ModelId: variant.Id,
                        CurrentFile: file.Path,
                        FileIndex: i + 1,
                        FileCount: files.Count,
                        BytesReadInFile: countedSize,
                        BytesTotalInFile: countedSize,
                        BytesReadTotal: bytesAcrossModel,
                        BytesTotalAcrossModel: totalBytes), ct).AsTask();
                    continue;
                }
            }

            int index = i;
            long fileSize = file.Size;
            long bytesAtStart = bytesAcrossModel;
            CancellationToken progressCt = ct;
            var progress = new Progress<DownloadByteProgress>(p =>
            {
                _ = _reporter.OnProgressAsync(new ModelDownloadProgress(
                    ModelId: variant.Id,
                    CurrentFile: file.Path,
                    FileIndex: index + 1,
                    FileCount: files.Count,
                    BytesReadInFile: p.BytesRead,
                    BytesTotalInFile: p.BytesTotal ?? fileSize,
                    BytesReadTotal: bytesAtStart + p.BytesRead,
                    BytesTotalAcrossModel: totalBytes), progressCt).AsTask();
            });

            string actualSha = await client.DownloadFileAsync(
                source, file, destPath, progress, ct).ConfigureAwait(false);

            if (file.Sha256 is { } expected &&
                !string.Equals(expected, actualSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"sha256 mismatch on {file.Path}: expected {expected}, got {actualSha}");
            }

            bytesAcrossModel += file.Size > 0 ? file.Size : 0;
        }
    }

    private IModelSourceClient ResolveClient(CatalogSource source)
    {
        string type = source switch
        {
            HuggingFaceSource => "huggingface",
            GithubReleaseSource => "github-release",
            HttpsSource => "https",
            _ => throw new InvalidOperationException(
                $"Unknown CatalogSource subtype '{source.GetType().Name}'. " +
                "Add the type to ModelDownloadService.ResolveClient's switch and register " +
                "an IModelSourceClient for its discriminator.")
        };
        if (!_sources.TryGetValue(type, out IModelSourceClient? client))
        {
            throw new InvalidOperationException(
                $"No IModelSourceClient registered for source type '{type}'. " +
                "Check the IServiceCollection wiring in AddModelLibrary.");
        }
        return client;
    }

    private (CatalogEntry Entry, CatalogVariant Variant) ResolveVariant(string variantId)
    {
        (CatalogEntry Entry, CatalogVariant Variant)? hit = _store.TryResolveVariant(variantId);
        if (hit is null)
        {
            throw new KeyNotFoundException($"Unknown variant id: {variantId}");
        }
        return hit.Value;
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

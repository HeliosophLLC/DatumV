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

    // Prevents two concurrent installs of the same modelId. The value is a
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

        // Build the per-type dispatch table once. Multiple clients with the
        // same SupportedType is a DI misconfig — let it surface immediately
        // instead of silently picking one.
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

    public async Task<ModelInstallState> ProbeAsync(string modelId, CancellationToken ct = default)
    {
        CatalogModel model = ResolveModel(modelId);

        // Placeholders short-circuit before any I/O. Their primary source
        // doesn't exist yet (Heliosoph.DatumV/* not uploaded), so a list probe
        // would 404 and waste a network call. A local directory with the
        // same name — empty leftover, prior failed download — doesn't
        // change the fact that there's nothing to compare against.
        if (model.Placeholder) return ModelInstallState.NotDownloaded;

        // Probe the recommended version's folder explicitly — pinning the
        // version means the probe is "is the catalog's current cut
        // installed?" rather than "is something installed?" When the
        // user reverts to an older version through the model card, that
        // version's folder gets its own state transition.
        string recommendedVersion = model.Versions[0].Version;
        string modelDir = _paths.GetModelRoot(model.Id, recommendedVersion);
        if (!Directory.Exists(modelDir)) return ModelInstallState.NotDownloaded;

        // Probe uses the first source's inventory as authoritative — for
        // the on-disk presence check, we don't need to walk every source.
        // If the first source can't list (network, gated repo without a
        // token, deleted revision), we surface Partial rather than chasing
        // the fallback list: probe is informational, the user can hit
        // Install to trigger the real multi-source attempt.
        CatalogSource primary = model.Sources[0];
        IModelSourceClient client = ResolveClient(primary);

        IReadOnlyList<SourceFile> inventory;
        try
        {
            inventory = await client.ListFilesAsync(primary, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Probe of {ModelId} could not list files from primary source ({Type})",
                modelId, client.SupportedType);
            return ModelInstallState.Partial;
        }

        int present = 0;
        foreach (SourceFile entry in inventory)
        {
            string localPath = Path.Combine(modelDir, entry.Path);
            if (!File.Exists(localPath)) continue;
            // Size-of-zero in the inventory means "source didn't report a
            // size" (github-release / https). We can't compare against
            // anything, so trust the file's existence as proof of presence.
            if (entry.Size == 0) { present++; continue; }
            if (new FileInfo(localPath).Length == entry.Size) present++;
        }

        if (present == 0) return ModelInstallState.NotDownloaded;
        if (present < inventory.Count) return ModelInstallState.Partial;

        // All files present on disk. For entries with no InstallSql,
        // NullModelInstaller answers true and we land at Installed — the
        // UI treats that as terminal ready-to-use. For entries with
        // InstallSql, the Web-side installer asks the SQL catalog whether
        // the conventional model name (id with '-' → '_', schema "models")
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
                $"Model {modelId} is a placeholder — its source repo has not been uploaded yet.");
        }

        // Gate: every license that requiresAcceptance must be accepted.
        foreach (string licenseId in model.LicenseIds)
        {
            CatalogLicense? license = _licenseRegistry.GetMetadata(licenseId);
            if (license is null)
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
        _ = Task.Run(
            () => RunDownloadAsync(model, model.Versions[0], pinnedMode: false, cts.Token),
            CancellationToken.None);
    }

    public async Task InstallPinnedAsync(string modelId, string version, CancellationToken ct = default)
    {
        CatalogModel model = ResolveModel(modelId);
        if (model.Placeholder)
        {
            throw new InvalidOperationException(
                $"Model {modelId} is a placeholder — its source repo has not been uploaded yet.");
        }

        CatalogVersion? pinned = null;
        foreach (CatalogVersion v in model.Versions)
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
                $"Model {modelId} has no version '{version}'. Available versions: " +
                string.Join(", ", model.Versions.Select(v => v.Version)));
        }

        // License gating mirrors the bare install path. License terms
        // don't vary across versions — same model, same licenses — so we
        // reuse the entry-level license set without per-version lookups.
        foreach (string licenseId in model.LicenseIds)
        {
            CatalogLicense? license = _licenseRegistry.GetMetadata(licenseId);
            if (license is null)
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

        // Single-flight guard keyed on `<modelId>@<version>` so a pinned
        // install + a concurrent active install (or a different pinned
        // version) don't collide. Bare installs key on the plain modelId
        // — same model, same key, mutual exclusion holds.
        string key = $"{modelId}@{version}";
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_active.TryAdd(key, cts))
        {
            throw new InvalidOperationException($"Model {modelId} version '{version}' is already downloading.");
        }

        _ = Task.Run(
            () => RunDownloadAsync(model, pinned, pinnedMode: true, cts.Token),
            CancellationToken.None);
    }

    public Task UninstallAsync(string modelId, CancellationToken ct = default)
    {
        CatalogModel model = ResolveModel(modelId);
        // Wipe the whole <id>/ tree — every version folder + the active
        // pointer. Per-version uninstall lives on the model card's
        // "Previous versions" disclosure, not on this top-level
        // surface.
        string idRoot = Path.Combine(_paths.ModelsRoot, model.Id);
        if (Directory.Exists(idRoot)) Directory.Delete(idRoot, recursive: true);
        // No active pointer to invalidate — the catalog row registration
        // (which the installer torn down during the SQL DROP MODEL path)
        // was the active-version signal, and it's already gone.
        return Task.CompletedTask;
    }

    public async Task ActivateVersionAsync(
        string modelId, string version, CancellationToken ct = default)
    {
        CatalogModel model = ResolveModel(modelId);
        CatalogVersion target = FindVersion(model, version);

        if (!_paths.IsVersionOnDisk(modelId, version))
        {
            throw new InvalidOperationException(
                $"Model {modelId} version '{version}' is not on disk. Install it first.");
        }

        string? currentActive = _paths.GetActiveVersion(modelId);
        if (string.Equals(currentActive, version, StringComparison.Ordinal))
        {
            return;
        }

        // Single-flight lock — concurrent activate / install / delete on
        // the same model id would race against the install-context
        // ambient pin and the cross-check.
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_active.TryAdd(modelId, cts))
        {
            throw new InvalidOperationException(
                $"Model {modelId} has an install in flight; try again shortly.");
        }

        try
        {
            // Outgoing version: drop its bare identifiers so they stop
            // routing through the active pointer, and re-register them in
            // pinned form (best-effort) so callers can still reach them via
            // `models.<bare>@<digits-of-outgoing>` syntax. The pinned
            // re-register requires the outgoing version's files on disk;
            // if they're not (rare — would mean a manual filesystem edit),
            // skip the pinned step and only drop bare.
            if (currentActive is not null)
            {
                CatalogVersion? outgoing = TryFindVersion(model, currentActive);
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
                    && _paths.IsVersionOnDisk(modelId, currentActive))
                {
                    string? previousPin = ModelInstallContext.CurrentVersionPin;
                    ModelInstallContext.CurrentVersionPin = currentActive;
                    try
                    {
                        await _installer.InstallAsync(
                            model, outgoing, pinnedMode: true, cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Pinned re-register is best-effort: failure leaves
                        // the outgoing version unreachable via pinned
                        // syntax, but the user can recover by hitting
                        // Install on that version's row (which re-runs the
                        // same path). Don't fail the whole activate.
                        _logger.LogWarning(ex,
                            "Activate {ModelId} {Version}: pinned re-register of outgoing {Outgoing} failed",
                            modelId, version, currentActive);
                    }
                    finally
                    {
                        ModelInstallContext.CurrentVersionPin = previousPin;
                    }
                }
            }

            // Incoming version: run its installSql in bare mode. Shared
            // identifiers with the outgoing version that survived the drop
            // (none — we dropped them all) come back via CREATE statements;
            // CREATE OR REPLACE in the installSql handles either case.
            if (!string.IsNullOrEmpty(target.InstallSql))
            {
                string? previousPin = ModelInstallContext.CurrentVersionPin;
                IReadOnlyList<string> observed;
                ModelInstallContext.CurrentVersionPin = version;
                try
                {
                    observed = await _installer.InstallAsync(
                        model, target, pinnedMode: false, cts.Token).ConfigureAwait(false);
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

            // No active pointer to flip — registering the incoming version's
            // bare identifiers (which happened during the installer call
            // above) is itself the activation. The lookup picks up the new
            // CatalogVersion from the freshly-registered descriptor.
        }
        finally
        {
            _active.TryRemove(modelId, out _);
        }
    }

    public async Task DeleteVersionAsync(
        string modelId, string version, CancellationToken ct = default)
    {
        CatalogModel model = ResolveModel(modelId);

        // Idempotent: a version that isn't on disk has no folder to wipe
        // and (for non-active versions) no registered identifiers to
        // drop. Active-pointer-without-folder is a rare manual-edit
        // case; the pointer gets cleared anyway via the active-version
        // branch below if the user happens to point at it.
        if (!_paths.IsVersionOnDisk(modelId, version)
            && !string.Equals(_paths.GetActiveVersion(modelId), version, StringComparison.Ordinal))
        {
            return;
        }

        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_active.TryAdd(modelId, cts))
        {
            throw new InvalidOperationException(
                $"Model {modelId} has an install in flight; try again shortly.");
        }

        try
        {
            CatalogVersion? versionEntry = TryFindVersion(model, version);
            bool isActive = string.Equals(
                _paths.GetActiveVersion(modelId), version, StringComparison.Ordinal);

            if (versionEntry?.Models is { Count: > 0 } declaredModels)
            {
                // Active version's identifiers live under bare names;
                // non-active on-disk versions' identifiers (if previously
                // installed via pinned) live under the suffixed form.
                List<string> toDrop = new(declaredModels.Count);
                foreach (CatalogVersionModel vm in declaredModels)
                {
                    toDrop.Add(isActive ? vm.Identifier : vm.EffectivePinnedAs(version));
                }
                await _installer.DropModelsAsync(toDrop, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            string versionDir = Path.Combine(_paths.ModelsRoot, modelId, version);
            if (Directory.Exists(versionDir))
            {
                Directory.Delete(versionDir, recursive: true);
            }

            // No auto-flip to a sibling version — that would be a
            // surprise. The entry returns to a "no active" state via
            // the installer's DROP MODEL above removing the catalog row
            // (or the pinned row for non-active deletes), and ProbeAsync
            // surfaces it as NotDownloaded once the recommended-version
            // folder is gone. No filesystem active pointer to maintain.
        }
        finally
        {
            _active.TryRemove(modelId, out _);
        }
    }

    private static CatalogVersion FindVersion(CatalogModel model, string version)
        => TryFindVersion(model, version)
            ?? throw new InvalidOperationException(
                $"Model {model.Id} has no version '{version}'. Available versions: " +
                string.Join(", ", model.Versions.Select(v => v.Version)));

    private static CatalogVersion? TryFindVersion(CatalogModel model, string version)
    {
        foreach (CatalogVersion v in model.Versions)
        {
            if (string.Equals(v.Version, version, StringComparison.Ordinal)) return v;
        }
        return null;
    }

    public async Task<IReadOnlyDictionary<string, ModelInstallState>> ProbeAllAsync(
        CancellationToken ct = default)
    {
        // Parallel probe. ProbeAsync short-circuits to NotDownloaded when
        // the model directory doesn't exist (no list call), so for a fresh
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
        // Walk the whole <id>/ tree (every version folder) — partial
        // downloads live in whichever version is currently being
        // installed, which may not be the active one yet.
        string modelDir = Path.Combine(_paths.ModelsRoot, model.Id);
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
        // Walk the whole <id>/ tree; see GetPartialBytesAsync for the rationale.
        string modelDir = Path.Combine(_paths.ModelsRoot, model.Id);
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

    // Sequential source-fallback loop. For each source in order:
    //   1. Ask the client to list the file inventory.
    //   2. Emit OnStarted with that inventory's totals (resets the UI's
    //      progress bar for any prior failed-source attempt).
    //   3. Download every file with per-file progress; verify sha256 if
    //      the source surfaced one (HuggingFace LFS only today).
    //   4. On any failure (list, download, hash mismatch) — log the
    //      reason, append to the aggregate error, advance to the next
    //      source. On final source's failure, emit OnFailed with the
    //      collected reasons.
    //   5. On success, emit OnComplete and (if InstallSql is set) the
    //      OnInstalling / OnInstalled lifecycle around the SQL apply.
    //
    // Cancellation aborts the whole loop — no point trying the next source
    // when the user pulled the plug.
    private async Task RunDownloadAsync(
        CatalogModel model,
        CatalogVersion target,
        bool pinnedMode,
        CancellationToken ct)
    {
        // Bare installs target versions[0]; pinned installs target a
        // specific older cut. Either way the bytes land in
        // <root>/<id>/<version>/ — the per-version folder is the unit of
        // atomicity; partials never get promoted, and a pinned install
        // doesn't disturb the active one.
        string version = target.Version;
        string modelDir = _paths.GetModelRoot(model.Id, version);
        Directory.CreateDirectory(modelDir);
        string activeKey = pinnedMode ? $"{model.Id}@{version}" : model.Id;

        StringBuilder failureLog = new();
        // Models with no file sources are valid for kind="python" entries
        // whose weights live in an external cache (HF cache for
        // transformers / diffusers pipelines) rather than under
        // $DATUMV_MODELS. The download phase is then a no-op success;
        // the venv-install phase below does the real work. Seed
        // `anySourceSucceeded` true when there are no sources so the
        // post-loop "no sources, treat as failure" branch doesn't fire
        // for these intentionally file-less entries.
        bool anySourceSucceeded = target.Sources.Count == 0;

        // Always emit OnStarted so the UI's install dialog opens, even
        // when there's nothing to download (kind="python" with no
        // files). FileCount + TotalBytes are zero in that case; the
        // install dialog renders a determinate-but-instantly-complete
        // download bar and proceeds to the venv-install phase.
        if (target.Sources.Count == 0)
        {
            await _reporter.OnStartedAsync(
                new ModelDownloadStarted(model.Id, FileCount: 0, TotalBytes: 0),
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
                        model, modelDir, source, client, sourceIndex, ct).ConfigureAwait(false);
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
                    _logger.LogWarning(ex, "{ModelId}: {Label} failed; advancing to next source", model.Id, label);
                    failureLog.AppendLine($"{label}: {ex.Message}");
                }
            }

            if (!anySourceSucceeded)
            {
                string aggregate = failureLog.Length == 0
                    ? "no sources configured"
                    : "all sources failed: " + failureLog.ToString().TrimEnd();
                await _reporter.OnFailedAsync(
                    new ModelDownloadFailed(model.Id, aggregate), CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            await _reporter.OnCompleteAsync(
                new ModelDownloadComplete(model.Id), CancellationToken.None)
                .ConfigureAwait(false);

            // The `<id>/active` pointer is left untouched until both
            // installSql and the declared/observed cross-check succeed.
            // Mid-install USING-path resolution flows through
            // `ModelInstallContext.CurrentVersionPin` instead, which
            // names the version-being-installed independently of the
            // active pointer. Effect: in-flight queries observe one
            // consistent active pointer throughout the install (either
            // the prior version's pointer or — on success — the new
            // one); a cross-check failure has nothing to revert.

            // Python venv install step. Runs for kind="python" entries
            // after the file download completes — sets up the engine-
            // managed Python interpreter + per-model venv + declared
            // requirements. Wrapped in OnInstalling/OnInstalled events
            // so the UI's install dialog shows "installing Python
            // environment" alongside the download. Granular per-stage
            // progress (uv download, python install, deps) flows
            // separately through IPythonEnvironmentReporter to the
            // status-bar chip surface when that lands.
            if (model.Python is { } pythonSpec)
            {
                await _reporter.OnInstallingAsync(
                    new ModelInstalling(model.Id), CancellationToken.None)
                    .ConfigureAwait(false);

                await _python.EnsureVenvAsync(
                    venvName: model.Id,
                    pythonVersion: pythonSpec.PythonVersion,
                    requirements: pythonSpec.Requirements,
                    cancellationToken: ct).ConfigureAwait(false);

                await _reporter.OnInstalledAsync(
                    new ModelInstalled(model.Id), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // Run the SQL install glue, if any. Failures here surface as
            // ModelDownloadFailed so the UI shows a single "this didn't
            // work" path — from the user's perspective the model isn't
            // usable until both halves succeed. We deliberately don't
            // delete the downloaded files on install failure: the bytes
            // are valid and re-trying install is cheaper than re-downloading.
            if (!string.IsNullOrEmpty(target.InstallSql))
            {
                await _reporter.OnInstallingAsync(
                    new ModelInstalling(model.Id), CancellationToken.None)
                    .ConfigureAwait(false);

                // Ambient version pin steers `ModelCatalog.ResolveFilePath`
                // at every `USING` resolution that runs inside the
                // installer's parsed CREATE MODEL statements. Cleared in
                // the finally so it never leaks past this install. Pinned
                // installs need the same pin so id-prefixed USING paths
                // resolve to the pinned version's folder, not active.
                string? previousPin = ModelInstallContext.CurrentVersionPin;
                IReadOnlyList<string> observed;
                ModelInstallContext.CurrentVersionPin = version;
                try
                {
                    observed = await _installer.InstallAsync(
                        model, target, pinnedMode, ct).ConfigureAwait(false);
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
                        // Partial install: drop everything the installer
                        // actually registered so the catalog matches its
                        // pre-install state. Active pointer was never
                        // flipped, so there's nothing to revert.
                        await _installer.DropModelsAsync(observed, CancellationToken.None)
                            .ConfigureAwait(false);
                        throw new InvalidOperationException(mismatch);
                    }
                }

                await _reporter.OnInstalledAsync(
                    new ModelInstalled(model.Id), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // Cross-check passed (or there was no installSql). No active
            // pointer to flip — the catalog row registered by installSql
            // (bare for !pinnedMode, suffixed otherwise) is itself the
            // active-version signal. ICatalogActiveVersionLookup walks
            // those rows to answer "what version is active for <id>?"
        }
        catch (OperationCanceledException)
        {
            await _reporter.OnFailedAsync(
                new ModelDownloadFailed(model.Id, "cancelled"), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Reaches here only for failures AFTER a source succeeded — i.e.
            // install-SQL errors. Pre-success per-source errors are caught
            // by the inner try and routed into failureLog above.
            _logger.LogError(ex, "Post-download step failed for {ModelId}", model.Id);
            await _reporter.OnFailedAsync(
                new ModelDownloadFailed(model.Id, ex.Message), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _active.TryRemove(activeKey, out _);
        }
    }

    // Cross-check declared identifiers from catalog.json against the
    // identifiers the installer actually registered. Returns an empty
    // string when the sets match; otherwise a one-line error message
    // identifying the missing / extra identifiers. Used by both the
    // active and pinned install paths — the only difference is the
    // expected identifier form (bare vs `<bare>@<digits>`).
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


    // Single-source download attempt. Throws on any failure — the outer
    // loop catches and advances to the next source.
    private async Task TryDownloadFromSourceAsync(
        CatalogModel model,
        string modelDir,
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

        // Total bytes may be unknown for github-release / https sources
        // (Size=0). The UI handles missing totals — leaves the percentage
        // empty and shows elapsed-only. HF entries always have real sizes.
        long totalBytes = files.Sum(f => f.Size);

        await _reporter.OnStartedAsync(
            new ModelDownloadStarted(model.Id, files.Count, totalBytes), ct)
            .ConfigureAwait(false);

        long bytesAcrossModel = 0;
        for (int i = 0; i < files.Count; i++)
        {
            SourceFile file = files[i];
            string destPath = Path.Combine(modelDir, file.Path);

            // Skip files already present at the expected size — the user
            // hit Install on a `Downloaded` model whose bytes are already
            // on disk; re-fetching from the source would burn bandwidth
            // and time for no gain. Mirrors the heuristic ProbeAsync uses
            // when classifying a model as `Downloaded`: a file is present
            // iff it exists and (when the inventory reports a size) the
            // on-disk length matches. Hash verification is intentionally
            // skipped on the skip-path — re-hashing multi-GB GGUF files
            // on every Install click would defeat the purpose; the
            // hash check below still runs on freshly-downloaded files.
            if (File.Exists(destPath))
            {
                bool sizeMatches = file.Size == 0 ||
                    new FileInfo(destPath).Length == file.Size;
                if (sizeMatches)
                {
                    long countedSize = file.Size > 0 ? file.Size : 0;
                    bytesAcrossModel += countedSize;
                    // Emit a synthetic per-file progress event so the UI's
                    // overall progress bar advances to reflect the skipped
                    // file as already-complete. Without this, an Install on
                    // a fully-downloaded model would jump from 0 → 100 only
                    // at the very end of the loop.
                    _ = _reporter.OnProgressAsync(new ModelDownloadProgress(
                        ModelId: model.Id,
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

            string actualSha = await client.DownloadFileAsync(
                source, file, destPath, progress, ct).ConfigureAwait(false);

            // Verify only when the source advertised an expected hash.
            // Null = source has no checksum API (github-release / https,
            // and HF non-LFS files like config.json) — trust HTTPS.
            if (file.Sha256 is { } expected &&
                !string.Equals(expected, actualSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"sha256 mismatch on {file.Path}: expected {expected}, got {actualSha}");
            }

            // Accumulate using the known size from the inventory if present,
            // otherwise approximate from observed bytes (the OnComplete will
            // settle this; the bytesAcrossModel tracker is best-effort).
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

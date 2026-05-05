// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member

using System.Diagnostics.CodeAnalysis;

using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Pooling;

using Microsoft.Extensions.Logging;

namespace DatumIngest.DatasetLibrary;

/// <summary>
/// Walks the dataset manifest at boot (and after every install /
/// uninstall) to compute the set of <c>.datum</c>-backed tables that
/// should be visible as <c>&lt;schema&gt;.&lt;variantId&gt;</c> in the engine,
/// and hands the snapshot to the shared
/// <see cref="DatasetSchemaCatalog"/> for atomic replacement.
/// </summary>
/// <remarks>
/// <para>
/// Naming rule per the multi-job answer locked in PR 5a: when a variant
/// has exactly one ingest job the table lands at
/// <c>&lt;schema&gt;.&lt;variantId&gt;</c>; with two or more it lands as
/// <c>&lt;schema&gt;.&lt;variantId&gt;_&lt;tableName&gt;</c> for each job. The single-job
/// shortcut keeps the common case ergonomic.
/// </para>
/// <para>
/// The recommended version (<c>variant.Versions[0]</c>) is what the
/// binder targets — the same version the install service writes. A
/// future "activate a non-recommended version" surface will set an
/// async-local pin and re-call <see cref="RebuildAsync"/>.
/// </para>
/// </remarks>
public sealed class DatasetSchemaBinder : IPreFlightDatasetSource
{
    private readonly IManifestStore _manifest;
    private readonly IDatasetPathResolver _paths;
    private readonly IDatasetDownloadService _downloads;
    private readonly Pool _pool;
    private readonly DatasetSchemaCatalog _catalog;
    private readonly ILogger<DatasetSchemaBinder> _logger;

    // (schema, table) → (variant, entry, version). Built once per
    // manifest load; covers both the single-job and multi-job naming
    // shapes so the pre-flight source can answer "which variant maps to
    // datasets.coco_test2017?" in one hashtable lookup. Volatile so the
    // RebuildAsync updates (install state portion) don't race the
    // lookup snapshot.
    private readonly Dictionary<(string Schema, string Table), CandidateRow> _candidatesByQualifiedName;

    // variantId → install state. Refreshed by RebuildAsync from
    // ProbeAllAsync. Read by IPreFlightDatasetSource.TryDescribe.
    private volatile IReadOnlyDictionary<string, DatasetInstallState> _installStates =
        new Dictionary<string, DatasetInstallState>();

    /// <summary>
    /// Fires after every successful <see cref="RebuildAsync"/> so
    /// observers (LanguageServer manifest, future UI surfaces) can
    /// invalidate caches that depend on the binding set. Synchronous —
    /// keep handlers cheap; a misbehaving handler's exception
    /// propagates to the caller of <see cref="RebuildAsync"/>.
    /// </summary>
    public event Action? BindingsChanged;

    private sealed record CandidateRow(
        string Schema,
        string Table,
        string VariantId,
        string EntryName,
        string DisplayName,
        string Version,
        IReadOnlyList<string> Modalities,
        IReadOnlyList<string> LicenseIds,
        long ApproxArchiveBytes,
        long ApproxIngestedBytes,
        string DatumPath);

    public DatasetSchemaBinder(
        IManifestStore manifest,
        IDatasetPathResolver paths,
        IDatasetDownloadService downloads,
        Pool pool,
        DatasetSchemaCatalog catalog,
        ILogger<DatasetSchemaBinder> logger)
    {
        _manifest = manifest;
        _paths = paths;
        _downloads = downloads;
        _pool = pool;
        _catalog = catalog;
        _logger = logger;
        _candidatesByQualifiedName = BuildCandidateLookup(manifest, paths);
    }

    private static Dictionary<(string, string), CandidateRow> BuildCandidateLookup(
        IManifestStore manifest,
        IDatasetPathResolver paths)
    {
        Dictionary<(string, string), CandidateRow> map = new(
            new SchemaTableComparer());
        foreach (DatasetEntry entry in manifest.Manifest.Datasets)
        {
            foreach (DatasetVariant variant in entry.Variants)
            {
                CatalogDatasetVersion version = variant.Versions[0];
                string versionFolder = paths.GetIngestedRoot(variant.Id, version.Version);
                bool single = version.Ingest.Count == 1;
                foreach (CatalogIngestJob job in version.Ingest)
                {
                    string suffix = single ? string.Empty : "_" + job.TableName;
                    string table = variant.Id + suffix;
                    string datumPath = Path.Combine(versionFolder, job.TableName + ".datum");
                    map[(entry.Schema, table)] = new CandidateRow(
                        Schema: entry.Schema,
                        Table: table,
                        VariantId: variant.Id,
                        EntryName: entry.Name,
                        DisplayName: variant.DisplayName,
                        Version: version.Version,
                        Modalities: entry.Modalities,
                        LicenseIds: entry.LicenseIds,
                        ApproxArchiveBytes: variant.ApproxArchiveBytes,
                        ApproxIngestedBytes: variant.ApproxIngestedBytes,
                        DatumPath: datumPath);
                }
            }
        }
        return map;
    }

    private sealed class SchemaTableComparer
        : IEqualityComparer<(string Schema, string Table)>
    {
        public bool Equals((string Schema, string Table) a, (string Schema, string Table) b)
            => StringComparer.OrdinalIgnoreCase.Equals(a.Schema, b.Schema)
            && StringComparer.OrdinalIgnoreCase.Equals(a.Table, b.Table);

        public int GetHashCode((string Schema, string Table) v)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(v.Schema),
                StringComparer.OrdinalIgnoreCase.GetHashCode(v.Table));
    }

    /// <summary>
    /// Distinct schemas declared across the manifest. The hosting
    /// <see cref="TableCatalog"/> uses this to mount the
    /// <see cref="DatasetSchemaCatalog"/> instance under each name at
    /// boot. Adding a new schema to the manifest requires a restart so
    /// the mount table picks it up.
    /// </summary>
    public IReadOnlyCollection<string> DeclaredSchemas
    {
        get
        {
            HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
            foreach (DatasetEntry e in _manifest.Manifest.Datasets)
            {
                set.Add(e.Schema);
            }
            return set;
        }
    }

    /// <summary>
    /// Walks every installed variant, builds a
    /// <see cref="DatumFileTableProviderV2"/> for each of its ingest-job
    /// tables, and atomically swaps the
    /// <see cref="DatasetSchemaCatalog"/>'s registered set.
    /// </summary>
    public async Task RebuildAsync(CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, DatasetInstallState> states =
            await _downloads.ProbeAllAsync(ct).ConfigureAwait(false);
        _installStates = states;

        List<ITableProvider> providers = new();
        foreach (CandidateRow row in _candidatesByQualifiedName.Values)
        {
            if (!states.TryGetValue(row.VariantId, out DatasetInstallState state)
                || state != DatasetInstallState.Installed)
            {
                continue;
            }
            if (!File.Exists(row.DatumPath))
            {
                // Install state said installed but a file is missing —
                // log and skip rather than tear down the entire mount;
                // the user can repair via uninstall + reinstall.
                _logger.LogWarning(
                    "Dataset variant {VariantId} reported Installed but {Path} is missing; skipping.",
                    row.VariantId, row.DatumPath);
                continue;
            }
            TableDescriptor descriptor = new(
                Name: $"{row.Schema}.{row.Table}",
                FilePath: row.DatumPath);
            DatumFileTableProviderV2 provider;
            try
            {
                provider = new DatumFileTableProviderV2(descriptor, _pool);
            }
            catch (Exception ex)
            {
                // Bad / corrupt / partial .datum — one variant's mount
                // failure shouldn't tear down the rest of the catalog.
                // The user sees the dataset go missing; `system.datasets`
                // will still surface the binding with status='missing'.
                _logger.LogWarning(
                    ex,
                    "Dataset variant {VariantId}: failed to open {Path}; skipping mount.",
                    row.VariantId, row.DatumPath);
                continue;
            }
            providers.Add(provider);
        }

        _catalog.SetTables(providers);
        _logger.LogInformation(
            "Dataset schema binder mounted {Count} tables across {Schemas} schemas.",
            providers.Count, DeclaredSchemas.Count);
        BindingsChanged?.Invoke();
    }

    /// <summary>
    /// Drops the mounted providers belonging to <paramref name="variantId"/>
    /// without going through the install-state probe. Used before
    /// <see cref="IDatasetDownloadService.UninstallAsync"/> tears the
    /// variant's ingested folder off disk — the
    /// <see cref="DatumFileTableProviderV2"/> instances would otherwise
    /// hold the <c>.datum</c> handles open and the recursive directory
    /// delete would throw with a sharing violation. The other variants'
    /// providers stay mounted (same instances, no disposal); only the
    /// uninstalled variant's providers fall out and dispose, releasing
    /// their handles.
    /// </summary>
    public void DropVariantBindings(string variantId)
    {
        HashSet<QualifiedName> toDrop = new();
        foreach (CandidateRow row in _candidatesByQualifiedName.Values)
        {
            if (string.Equals(row.VariantId, variantId, StringComparison.Ordinal))
            {
                toDrop.Add(new QualifiedName(row.Schema, row.Table));
            }
        }
        if (toDrop.Count == 0) return;

        List<ITableProvider> remaining = new();
        foreach (ITableProvider provider in _catalog.ListTables())
        {
            if (toDrop.Contains(provider.QualifiedName)) continue;
            remaining.Add(provider);
        }
        _catalog.SetTables(remaining);
        BindingsChanged?.Invoke();
    }

    // ─────────────────── system.datasets enumeration ───────────────────

    /// <summary>
    /// One dataset binding as surfaced by <c>system.datasets</c> +
    /// LanguageServer manifest. Mirrors the entry/variant/version triple
    /// the manifest describes plus the resolved on-disk path. The
    /// <see cref="IsInstalled"/> flag distinguishes bound-and-queryable
    /// from declared-but-not-installed — consumers that only care about
    /// installed rows filter on it.
    /// </summary>
    public sealed record DatasetBindingDescriptor(
        string Schema,
        string Table,
        string VariantId,
        string EntryName,
        string DisplayName,
        string Version,
        IReadOnlyList<string> Modalities,
        IReadOnlyList<string> LicenseIds,
        long ApproxArchiveBytes,
        long ApproxIngestedBytes,
        string DatumPath,
        bool IsInstalled);

    /// <summary>
    /// Enumerates every dataset table the manifest declares, with each
    /// row's current <see cref="DatasetBindingDescriptor.IsInstalled"/>
    /// flag. Consumed by <c>system.datasets</c> (installed-only filter)
    /// and the LanguageServer manifest (both installed and discovered
    /// rows surface in completion / hover).
    /// </summary>
    public IEnumerable<DatasetBindingDescriptor> EnumerateBindings()
    {
        IReadOnlyDictionary<string, DatasetInstallState> states = _installStates;
        foreach (CandidateRow row in _candidatesByQualifiedName.Values)
        {
            bool installed = states.TryGetValue(row.VariantId, out DatasetInstallState state)
                && state == DatasetInstallState.Installed;
            yield return new DatasetBindingDescriptor(
                Schema: row.Schema,
                Table: row.Table,
                VariantId: row.VariantId,
                EntryName: row.EntryName,
                DisplayName: row.DisplayName,
                Version: row.Version,
                Modalities: row.Modalities,
                LicenseIds: row.LicenseIds,
                ApproxArchiveBytes: row.ApproxArchiveBytes,
                ApproxIngestedBytes: row.ApproxIngestedBytes,
                DatumPath: row.DatumPath,
                IsInstalled: installed);
        }
    }

    // ──────────────────── IPreFlightDatasetSource ────────────────────

    /// <inheritdoc/>
    public bool IsDatasetSchema(string schema)
    {
        foreach (string declared in DeclaredSchemas)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(declared, schema)) return true;
        }
        return false;
    }

    /// <inheritdoc/>
    public bool TryDescribe(
        string schema,
        string name,
        [NotNullWhen(true)] out PreFlightDatasetCandidate? candidate)
    {
        if (!_candidatesByQualifiedName.TryGetValue((schema, name), out CandidateRow? row))
        {
            candidate = null;
            return false;
        }
        bool installed =
            _installStates.TryGetValue(row.VariantId, out DatasetInstallState state)
            && state == DatasetInstallState.Installed;
        candidate = new PreFlightDatasetCandidate(
            VariantId: row.VariantId,
            EntryName: row.EntryName,
            DisplayName: row.DisplayName,
            Version: row.Version,
            ApproxArchiveBytes: row.ApproxArchiveBytes,
            LicenseIds: row.LicenseIds,
            IsInstalled: installed);
        return true;
    }
}

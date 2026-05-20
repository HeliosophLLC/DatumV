using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.ModelLibrary;
using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Models.Calibration;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Catalog.Providers;

/// <summary>
/// Origin-of-definition discriminator for <see cref="ModelsTableProvider"/>'s
/// <c>kind</c> column. <c>builtin</c> = engine-baked entry in
/// <see cref="ModelCatalog"/>; <c>declared</c> = user-written
/// <c>CREATE MODEL</c> registered in <see cref="ModelRegistry"/>;
/// <c>discovered</c> = catalog-declared but not yet installed (the
/// vocabulary surface lets <c>models.foo(...)</c> autocomplete +
/// pre-flight resolve identifiers before their weights land on disk).
/// </summary>
internal static class ModelKind
{
    public const string Builtin = "builtin";
    public const string Declared = "declared";
    public const string Discovered = "discovered";
}

/// <summary>
/// Residency discriminator for <see cref="ModelsTableProvider"/>'s
/// <c>residency</c> column. <c>callable</c> = the engine has a live
/// registration (builtin or declared) and the identifier resolves at
/// query time; <c>discovered</c> = the catalog knows the identifier
/// (via a version's declared <c>models[]</c> array) but no install
/// has registered it yet, so calling it triggers pre-flight install.
/// </summary>
internal static class ModelResidency
{
    public const string Callable = "callable";
    public const string Discovered = "discovered";
}

/// <summary>
/// Virtual table that surfaces the contents of a <see cref="ModelCatalog"/> as
/// a SQL-queryable view. Users introspect the registered model zoo with
/// <c>SELECT * FROM system_models</c> — what's available, what's missing, what
/// each weight file is licensed under, where to re-download from if a file is
/// gone.
/// </summary>
/// <remarks>
/// <para>
/// Rows materialise on every <see cref="ScanAsync"/> call. The provider stat-s
/// each catalog entry's resolved file path at scan time and reports one of
/// three statuses:
/// <list type="bullet">
///   <item><term><c>available</c></term><description>files present, native backend (ONNX / LlamaSharp), ready to use.</description></item>
///   <item><term><c>missing</c></term><description>required file(s) not on disk; can't load.</description></item>
///   <item><term><c>bridge</c></term><description>backend is <c>python</c>, worker script + model files present, but runnability also depends on a Python venv + pip packages the catalog can't verify without spawning the subprocess.</description></item>
/// </list>
/// That liveness matters: a user runs the query, sees a missing model,
/// downloads it, runs again, and the status flips. Caching rows from a
/// single snapshot would defeat the diagnostic.
/// </para>
/// <para>
/// Schema (14 columns):
/// <list type="table">
///   <item><term>name</term><description>SQL identifier (the <c>X</c> in <c>models.X(...)</c>).</description></item>
///   <item><term>display_name</term><description>Human-readable model name.</description></item>
///   <item><term>category</term><description>Single-valued purpose: <c>llm</c>, <c>classifier</c>, <c>detector</c>, <c>embedder</c>, etc. Routing key for <c>tasks.X</c>.</description></item>
///   <item><term>modalities</term><description><c>Array&lt;String&gt;</c> — every medium the model touches (<c>["image", "text"]</c> for a captioner, <c>["text"]</c> for an LLM).</description></item>
///   <item><term>backend</term><description><c>onnx</c> / <c>llama</c> / <c>echo</c>.</description></item>
///   <item><term>parameters</term><description>Architectural param count (<c>"8B"</c>, <c>"3.5M"</c>).</description></item>
///   <item><term>file_name</term><description>Anchor file the catalog status-checks against; for multi-file models, the registration "entry point".</description></item>
///   <item><term>file_names</term><description><c>Array&lt;String&gt;</c> — every file the model needs to run (ONNX weights + tokenizer + configs). Lets users audit dependencies and rebuild missing installs.</description></item>
///   <item><term>file_size_bytes</term><description>Anchor file's on-disk size, or <see langword="null"/> when missing.</description></item>
///   <item><term>license</term><description>SPDX-style or model-specific license identifier.</description></item>
///   <item><term>license_holder</term><description>Entity granting the license (Meta, Microsoft, etc.).</description></item>
///   <item><term>source_url</term><description>Repo / model-zoo URL for re-downloading.</description></item>
///   <item><term>status</term><description><c>available</c> / <c>missing</c> / <c>bridge</c>. See class remarks for semantics.</description></item>
///   <item><term>kind</term><description><c>builtin</c> (engine-baked entry in <see cref="ModelCatalog"/>) or <c>declared</c> (user-written <c>CREATE MODEL</c> in <see cref="ModelRegistry"/>). The schema-stable origin discriminator.</description></item>
///   <item><term>batchable</term><description>Whether the engine can dispatch N rows in one cross-row batched call. For <c>declared</c> rows this is derived from a straight-line check on the body (DECLARE / SET / RETURN only). For <c>builtin</c> rows it's whatever the impl reports via <see cref="IModel.IsBatchable"/> (default <see langword="false"/>).</description></item>
///   <item><term>calibration_state</term><description><c>uncalibrated</c> / <c>calibrated</c> / <c>stale</c>. Reflects whether the calibration coordinator has measured this model's VRAM curve and whether the measurements still match observed dispatch behaviour.</description></item>
///   <item><term>max_calibrated_batch</term><description>Largest batch size present in the model's calibration curve, or NULL when uncalibrated.</description></item>
///   <item><term>weight_cost_bytes</term><description>VRAM cost of holding the model's weights (measured at load time as <c>vramAfter - vramBefore</c>), or NULL when no measurement has been recorded yet.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class ModelsTableProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional fully-qualified table name registered in the catalog.</summary>
    public const string TableName = "system.models";

    /// <summary>The canonical <see cref="QualifiedName"/> for this provider.</summary>
    public static readonly QualifiedName QualifiedTableName = new("system", "models");

    private static readonly Schema _schema = BuildSchema();

    private readonly ModelCatalog _modelCatalog;
    private readonly ModelRegistry? _declaredModels;
    private readonly ICatalogVocabulary? _vocabulary;

    /// <summary>
    /// Creates a provider that surfaces <paramref name="modelCatalog"/>
    /// (engine-baked built-ins), optionally <paramref name="declaredModels"/>
    /// (SQL-defined models registered via <c>CREATE MODEL</c>), and
    /// optionally <paramref name="vocabulary"/> as a single virtual table.
    /// When <paramref name="vocabulary"/> is provided, identifiers the
    /// catalog declares but no install has registered yet surface as
    /// <c>kind = "discovered"</c> / <c>residency = "discovered"</c> rows —
    /// the autocomplete / pre-flight substrate that lets users type
    /// <c>models.foo(...)</c> before its weights land on disk.
    /// All three sources are held by reference — registrations after
    /// construction are visible to subsequent scans.
    /// </summary>
    /// <param name="pool">Buffer pool for renting row batches.</param>
    /// <param name="modelCatalog">The built-in registry whose entries become <c>kind = "builtin"</c> rows.</param>
    /// <param name="declaredModels">Optional SQL-defined registry whose descriptors become <c>kind = "declared"</c> rows.</param>
    /// <param name="vocabulary">Optional catalog vocabulary surface whose unregistered identifiers become <c>kind = "discovered"</c> rows.</param>
    public ModelsTableProvider(Pool pool, ModelCatalog modelCatalog, ModelRegistry? declaredModels = null, ICatalogVocabulary? vocabulary = null)
        : base(pool, QualifiedTableName)
    {
        _modelCatalog = modelCatalog;
        _declaredModels = declaredModels;
        _vocabulary = vocabulary;
    }

    /// <inheritdoc/>
    public override long GetRowCount()
    {
        // Mirror ScanAsync's dedup: a builtin entry shadowed by a
        // declared entry contributes one row, not two; and a discovered
        // entry shadowed by either a builtin or a declared row drops out
        // (callable subsumes discovered).
        HashSet<string> callable = new(StringComparer.OrdinalIgnoreCase);
        if (_declaredModels is not null)
        {
            foreach (ModelDescriptor d in _declaredModels.Entries)
                callable.Add(d.Name);
        }
        long builtinShown = 0;
        foreach (ModelCatalogEntry e in _modelCatalog.Entries.Values)
        {
            if (callable.Add(e.Name)) builtinShown++;
        }
        long declaredCount = _declaredModels?.Entries.Count ?? 0;
        long discoveredShown = 0;
        if (_vocabulary is not null)
        {
            foreach (string identifier in _vocabulary.ByIdentifier.Keys)
            {
                if (!callable.Contains(identifier)) discoveredShown++;
            }
        }
        return builtinShown + declaredCount + discoveredShown;
    }

    /// <inheritdoc/>
    public override Schema GetSchema() => _schema;

    /// <inheritdoc/>
    public override async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Model.TypeIdTranslationTable? typeIdTranslations = null)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        // Snapshot both registries at scan start so concurrent registrations
        // during a long iteration don't produce inconsistent rows. The
        // snapshot is cheap: a list of references, no value materialisation.
        //
        // Every SQL-defined model registers in BOTH places: the
        // ModelRegistry (kind="declared") so REPL / DDL / introspection
        // see the user's CREATE MODEL definition, and the ModelCatalog
        // (kind="builtin") so the planner's MIO hoister can resolve
        // models.* call sites against a uniform IModel surface. Surfacing
        // both rows in system.models is confusing — every SQL-defined
        // model shows up twice. The declared row is the user-facing
        // source of truth; the builtin shadow is an internal artifact of
        // how dispatch wiring works. When a name appears in both,
        // suppress the builtin row so each model contributes exactly one
        // row. Engine-baked builtins (no declared counterpart) keep their
        // single kind="builtin" row.
        HashSet<string> declaredNames = new(StringComparer.OrdinalIgnoreCase);
        if (_declaredModels is not null)
        {
            foreach (ModelDescriptor d in _declaredModels.Entries)
                declaredNames.Add(d.Name);
        }

        var rows = new List<(string Name, RowSource Source, ModelCatalogEntry? Entry, ModelDescriptor? Descriptor, CatalogVocabularyEntry? Vocab)>(
            _modelCatalog.Entries.Count + (_declaredModels?.Entries.Count ?? 0) + (_vocabulary?.ByIdentifier.Count ?? 0));
        HashSet<string> seenNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModelCatalogEntry entry in _modelCatalog.Entries.Values)
        {
            // Skip the shadow builtin row when a declared row exists
            // for the same name — see comment above.
            if (declaredNames.Contains(entry.Name)) continue;
            rows.Add((entry.Name, RowSource.Builtin, entry, null, null));
            seenNames.Add(entry.Name);
        }
        if (_declaredModels is not null)
        {
            foreach (ModelDescriptor descriptor in _declaredModels.Entries)
            {
                rows.Add((descriptor.Name, RowSource.Declared, null, descriptor, null));
                seenNames.Add(descriptor.Name);
            }
        }
        if (_vocabulary is not null)
        {
            foreach ((string identifier, CatalogVocabularyEntry vocabEntry) in _vocabulary.ByIdentifier)
            {
                // Discovered = catalog-declared but not callable. When
                // the same identifier is already callable (builtin or
                // declared), the live registration wins — discovered
                // rows are strictly for "users can autocomplete this
                // but the engine has no live binding yet."
                if (seenNames.Contains(identifier)) continue;
                rows.Add((identifier, RowSource.Discovered, null, null, vocabEntry));
            }
        }
        rows.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        Heliosoph.DatumV.ModelLibrary.IModelPathResolver pathResolver = _modelCatalog.PathResolver;
        CalibrationRegistry calibrationRegistry = _modelCatalog.CalibrationRegistry;

        // requiredColumns / filterHint are advisory; we materialise the full row
        // and let the caller's project / filter operators trim.
        _ = requiredColumns;
        _ = filterHint;

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        foreach (var entry in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);

            DataValue[] values = Pool.RentDataValues(_schema.Columns.Count);
            switch (entry.Source)
            {
                case RowSource.Builtin:
                    FillRow(values, entry.Entry!, pathResolver, batch.Arena, _vocabulary);
                    break;
                case RowSource.Declared:
                    FillRowFromDescriptor(values, entry.Descriptor!, pathResolver, batch.Arena, _vocabulary);
                    break;
                case RowSource.Discovered:
                    FillDiscoveredRow(values, entry.Vocab!, pathResolver, batch.Arena);
                    break;
            }
            FillCalibrationCells(values, calibrationRegistry.Get(entry.Name), batch.Arena);
            batch.Add(values);

            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Internal row-source discriminator. Drives which Fill* method
    /// materialises a row and, transitively, the row's <c>kind</c> /
    /// <c>residency</c> column values.
    /// </summary>
    private enum RowSource
    {
        Builtin,
        Declared,
        Discovered,
    }

    /// <summary>
    /// Materialises a single row of <c>system_models</c> from a catalog entry.
    /// Stat-s the file at scan time so <c>status</c> + <c>file_size_bytes</c>
    /// reflect current disk state.
    /// </summary>
    private static void FillRow(
        DataValue[] cells, ModelCatalogEntry entry, Heliosoph.DatumV.ModelLibrary.IModelPathResolver pathResolver, Arena arena, ICatalogVocabulary? vocabulary)
    {
        // RelativePath is id-prefixed (e.g. "all-minilm-l6-v2/model.onnx");
        // route through the resolver so per-version installs surface as
        // "available" rather than "missing".
        string? resolvedPath = entry.RelativePath is null
            ? null
            : pathResolver.ResolveIdPrefixedPath(entry.RelativePath);

        bool fileExists;
        long? fileSize;
        if (resolvedPath is null)
        {
            // Synthetic backends (e.g. EchoModel) have no file. Report as
            // available since they're loadable; size is null.
            fileExists = true;
            fileSize = null;
        }
        else
        {
            FileInfo info = new(resolvedPath);
            fileExists = info.Exists;
            fileSize = fileExists ? info.Length : null;
        }

        // Three-state status:
        //   missing   -- anchor file absent; can't load.
        //   bridge    -- backend == "python" with files present; signals
        //                that a Python venv + pip packages are also
        //                required, which catalog can't verify here.
        //   available -- native backend (ONNX / LlamaSharp / synthetic) +
        //                files present; ready to use.
        string status;
        if (!fileExists)
        {
            status = "missing";
        }
        else if (string.Equals(entry.Backend, "python", StringComparison.OrdinalIgnoreCase))
        {
            status = "bridge";
        }
        else
        {
            status = "available";
        }

        cells[0]  = DataValue.FromString(entry.Name, arena);
        cells[1]  = WriteOptionalString(entry.DisplayName, arena);
        cells[2]  = WriteOptionalString(entry.Category, arena);
        cells[3]  = WriteOptionalStringArray(entry.Modalities, arena);
        cells[4]  = DataValue.FromString(entry.Backend, arena);
        cells[5]  = WriteOptionalString(entry.Parameters, arena);
        cells[6]  = WriteOptionalString(entry.RelativePath, arena);
        cells[7]  = WriteOptionalStringArray(entry.Files, arena);
        cells[8]  = fileSize.HasValue ? DataValue.FromInt64(fileSize.Value) : DataValue.Null(DataKind.Int64);
        cells[9]  = WriteOptionalString(entry.License, arena);
        cells[10] = WriteOptionalString(entry.LicenseHolder, arena);
        cells[11] = WriteOptionalString(entry.SourceUrl, arena);
        cells[12] = DataValue.FromString(status, arena);
        cells[13] = DataValue.FromString(ModelKind.Builtin, arena);
        cells[14] = WriteOptionalString(entry.ImplementsTaskName, arena);
        cells[15] = DataValue.FromBoolean(entry.Batchable);

        // Catalog substrate columns (catalog_id + residency + active_version).
        // A live builtin row is always callable. catalog_id surfaces when
        // the vocabulary can map this identifier back to a catalog entry;
        // engine-baked builtins without a catalog entry get NULL.
        // active_version is whatever the path resolver reports for the
        // catalog id (NULL when the resolver doesn't know).
        string? catalogId = vocabulary?.ByIdentifier.TryGetValue(entry.Name, out CatalogVocabularyEntry? vocab) == true
            ? vocab.CatalogEntryId
            : null;
        cells[19] = WriteOptionalString(catalogId, arena);
        cells[20] = DataValue.FromString(ModelResidency.Callable, arena);
        cells[21] = WriteOptionalString(
            catalogId is not null ? pathResolver.GetActiveVersion(catalogId) : null,
            arena);
    }

    /// <summary>
    /// Materialises one row from a SQL-defined <see cref="ModelDescriptor"/>.
    /// The descriptor doesn't carry upstream catalog metadata (license,
    /// modalities, source URL, etc.) so most columns surface as NULL; the
    /// shape that matters is the discriminator (<c>kind = "declared"</c>),
    /// the user-written <c>USING</c> path, and the file size + status
    /// derived from stat-ing the resolved path. Status is always
    /// <c>"available"</c> because the descriptor is only in the registry
    /// after <c>ApplyCreateModelAsync</c> succeeded — the file existed +
    /// the session loaded. If the file vanished post-load,
    /// <c>file_size_bytes</c> goes null but the bound session in memory
    /// is still callable.
    /// </summary>
    private static void FillRowFromDescriptor(
        DataValue[] cells, ModelDescriptor descriptor, Heliosoph.DatumV.ModelLibrary.IModelPathResolver pathResolver, Arena arena, ICatalogVocabulary? vocabulary)
    {
        long? fileSize = descriptor.UsingPath is { } usingPath
            ? TryStatUsingPath(usingPath, pathResolver)
            : null;

        cells[0]  = DataValue.FromString(descriptor.Name, arena);
        cells[1]  = DataValue.Null(DataKind.String);          // display_name
        cells[2]  = DataValue.Null(DataKind.String);          // category
        cells[3]  = DataValue.NullArrayOf(DataKind.String);   // modalities
        cells[4]  = DataValue.FromString("sql", arena);       // backend — discriminator inside the row; the `kind` column is the schema-stable signal
        cells[5]  = DataValue.Null(DataKind.String);          // parameters
        cells[6]  = descriptor.UsingPath is not null
            ? DataValue.FromString(descriptor.UsingPath, arena)
            : DataValue.Null(DataKind.String);                // file_name — null for delegating models with no USING
        // file_names: NULL for legacy single-session bundles; a string[]
        // of every aliased file's source path for multi-session bundles.
        // Each entry is the SQL-declared path (not the resolved absolute
        // form) so the column matches what the user wrote in CREATE MODEL.
        if (descriptor.UsingFiles is { Count: > 0 } usingFiles)
        {
            string[] paths = new string[usingFiles.Count];
            for (int i = 0; i < usingFiles.Count; i++) paths[i] = usingFiles[i].Path;
            cells[7] = DataValue.FromStringArray(paths, arena);
        }
        else
        {
            cells[7] = DataValue.NullArrayOf(DataKind.String);
        }
        cells[8]  = fileSize.HasValue ? DataValue.FromInt64(fileSize.Value) : DataValue.Null(DataKind.Int64);
        cells[9]  = DataValue.Null(DataKind.String);          // license
        cells[10] = DataValue.Null(DataKind.String);          // license_holder
        cells[11] = DataValue.Null(DataKind.String);          // source_url
        cells[12] = DataValue.FromString("available", arena); // session is loaded; see method remarks
        cells[13] = DataValue.FromString(ModelKind.Declared, arena);
        cells[14] = WriteOptionalString(descriptor.ImplementsTaskName, arena);
        cells[15] = DataValue.FromBoolean(ProceduralModelAdapter.IsStraightLineBody(descriptor.StatementBody));

        // Catalog substrate columns. A declared model rarely has a catalog
        // entry (it was registered by a user CREATE MODEL, not by
        // catalog installSql) — but the vocabulary check is still
        // worth doing because catalog-driven installs do register a
        // declared row mid-install. catalog_id and active_version
        // surface when present; residency is always callable.
        string? catalogId = vocabulary?.ByIdentifier.TryGetValue(descriptor.Name, out CatalogVocabularyEntry? vocab) == true
            ? vocab.CatalogEntryId
            : null;
        cells[19] = WriteOptionalString(catalogId, arena);
        cells[20] = DataValue.FromString(ModelResidency.Callable, arena);
        cells[21] = WriteOptionalString(
            catalogId is not null ? pathResolver.GetActiveVersion(catalogId) : null,
            arena);
    }

    /// <summary>
    /// Materialises a row for a catalog-declared identifier that has no
    /// live registration in <see cref="ModelCatalog"/> or
    /// <see cref="ModelRegistry"/>. Most runtime-derived columns
    /// (modalities, license metadata, file presence, calibration)
    /// surface as NULL because the catalog vocabulary alone doesn't
    /// know them — those come from the registrar that runs at install
    /// time. The minimal user-visible signal is the row exists at all
    /// (so autocomplete + pre-flight can resolve the name) plus
    /// <c>kind = "discovered"</c> + <c>residency = "discovered"</c>.
    /// </summary>
    private static void FillDiscoveredRow(
        DataValue[] cells, CatalogVocabularyEntry vocab, Heliosoph.DatumV.ModelLibrary.IModelPathResolver pathResolver, Arena arena)
    {
        CatalogModel owner = vocab.Owner;

        cells[0]  = DataValue.FromString(vocab.Identifier, arena);
        cells[1]  = WriteOptionalString(owner.DisplayName, arena);
        cells[2]  = DataValue.Null(DataKind.String);          // category — not on catalog entry today
        cells[3]  = DataValue.NullArrayOf(DataKind.String);   // modalities — derived at registration time
        cells[4]  = DataValue.FromString(owner.Kind, arena);  // catalog Kind ("onnx" / "python" / …) is the closest analogue to backend
        cells[5]  = DataValue.Null(DataKind.String);          // parameters
        cells[6]  = DataValue.Null(DataKind.String);          // file_name — version-dependent, deferred to install time
        cells[7]  = DataValue.NullArrayOf(DataKind.String);   // file_names
        cells[8]  = DataValue.Null(DataKind.Int64);           // file_size_bytes
        // Surface license from the catalog entry's first licenseId when
        // present so SQL queries against `license` work pre-install.
        cells[9]  = WriteOptionalString(owner.LicenseIds is { Count: > 0 } lic ? lic[0] : null, arena);
        cells[10] = DataValue.Null(DataKind.String);          // license_holder — derived from CatalogLicense table at registration
        cells[11] = DataValue.Null(DataKind.String);          // source_url
        cells[12] = DataValue.FromString("discovered", arena);
        cells[13] = DataValue.FromString(ModelKind.Discovered, arena);
        // task surfaces the entry's primary contract so users can find
        // discovered models via WHERE task = 'X'. Multi-task entries
        // expose their primary; the rest is in `system.tasks`.
        cells[14] = WriteOptionalString(owner.Tasks is { Count: > 0 } t ? t[0] : null, arena);
        cells[15] = DataValue.FromBoolean(false);              // batchable — unknowable until registered
        // calibration cells filled by FillCalibrationCells after this.
        cells[19] = DataValue.FromString(owner.Id, arena);
        cells[20] = DataValue.FromString(ModelResidency.Discovered, arena);
        // For a discovered row, active_version is NULL by definition —
        // the catalog hasn't been installed yet so `<id>/active` doesn't
        // exist. The drift-surfacing UI reads this column to badge
        // outdated installs; discovered rows are upstream of drift.
        cells[21] = DataValue.Null(DataKind.String);
    }

    /// <summary>
    /// Resolves a descriptor's <c>USING</c> path the same way
    /// <see cref="RoutineRegistrar"/> does at CREATE-MODEL time
    /// (<c>file://</c> stripped to absolute; otherwise relative to the
    /// model directory) and stat-s the result. Returns <see langword="null"/>
    /// when the file is gone or the path can't be resolved.
    /// </summary>
    private static long? TryStatUsingPath(string usingPath, Heliosoph.DatumV.ModelLibrary.IModelPathResolver pathResolver)
    {
        string resolved;
        if (usingPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            resolved = usingPath["file://".Length..];
        }
        else if (Path.IsPathRooted(usingPath))
        {
            resolved = Path.GetFullPath(usingPath);
        }
        else
        {
            // Same id-prefixed resolution as ModelCatalog.ResolveFilePath —
            // SQL-defined models register against the active-version
            // folder under the per-version layout.
            resolved = Path.GetFullPath(pathResolver.ResolveIdPrefixedPath(usingPath));
        }

        FileInfo info = new(resolved);
        return info.Exists ? info.Length : null;
    }

    private static DataValue WriteOptionalString(string? value, Arena arena) =>
        value is null ? DataValue.Null(DataKind.String) : DataValue.FromString(value, arena);

    /// <summary>
    /// Writes a string list as a typed <c>Array&lt;String&gt;</c> cell, or a
    /// typed null when the source is null. Empty lists round-trip as empty
    /// arrays (distinguishable from null in display: <c>[]</c> vs <c>NULL</c>).
    /// </summary>
    private static DataValue WriteOptionalStringArray(IReadOnlyList<string>? values, Arena arena)
    {
        if (values is null) return DataValue.NullArrayOf(DataKind.String);
        string[] copy = values is string[] array ? array : values.ToArray();
        return DataValue.FromStringArray(copy, arena);
    }

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("name",                 DataKind.String, nullable: false),
        new ColumnInfo("display_name",         DataKind.String, nullable: true),
        new ColumnInfo("category",             DataKind.String, nullable: true),
        new ColumnInfo("modalities",           DataKind.String, nullable: true) { IsArray = true },
        new ColumnInfo("backend",              DataKind.String, nullable: false),
        new ColumnInfo("parameters",           DataKind.String, nullable: true),
        new ColumnInfo("file_name",            DataKind.String, nullable: true),
        new ColumnInfo("file_names",           DataKind.String, nullable: true) { IsArray = true },
        new ColumnInfo("file_size_bytes",      DataKind.Int64,  nullable: true),
        new ColumnInfo("license",              DataKind.String, nullable: true),
        new ColumnInfo("license_holder",       DataKind.String, nullable: true),
        new ColumnInfo("source_url",           DataKind.String, nullable: true),
        new ColumnInfo("status",               DataKind.String, nullable: false),
        new ColumnInfo("kind",                 DataKind.String, nullable: false),
        new ColumnInfo("task",                 DataKind.String, nullable: true),
        new ColumnInfo("batchable",            DataKind.Boolean, nullable: false),
        // ─── Calibration columns (populated from ModelCatalog.CalibrationRegistry) ───
        // Always reflect current registry state; uncalibrated models surface
        // as "uncalibrated" with null max_batch and null/zero weight cost.
        new ColumnInfo("calibration_state",    DataKind.String, nullable: false),
        new ColumnInfo("max_calibrated_batch", DataKind.Int32,  nullable: true),
        new ColumnInfo("weight_cost_bytes",    DataKind.Int64,  nullable: true),
        // ─── Catalog substrate columns ───
        // catalog_id: parent catalog-entry id (kebab-case) when this row
        // resolves to a catalog entry; NULL for engine-baked builtins
        // and user CREATE MODEL rows with no catalog presence.
        new ColumnInfo("catalog_id",           DataKind.String, nullable: true),
        // residency: 'callable' when the engine has a live registration
        // for this name; 'discovered' when the catalog declares it but
        // no install has registered it yet.
        new ColumnInfo("residency",            DataKind.String, nullable: false),
        // active_version: the version string the path resolver is
        // currently routing to (catalog `<id>/active` file contents);
        // NULL when not installed or when the row has no catalog_id.
        new ColumnInfo("active_version",       DataKind.String, nullable: true),
    ]);

    /// <summary>
    /// String form of <see cref="ModelCalibration.State"/> for the
    /// <c>calibration_state</c> column. Three values mirror the enum
    /// surface; lowercased for SQL-ergonomic filtering
    /// (<c>WHERE calibration_state = 'uncalibrated'</c>).
    /// </summary>
    private static string CalibrationStateName(ModelCalibration.State state) => state switch
    {
        ModelCalibration.State.Uncalibrated => "uncalibrated",
        ModelCalibration.State.Calibrated => "calibrated",
        ModelCalibration.State.Stale => "stale",
        _ => "unknown",
    };

    /// <summary>
    /// Writes the three calibration cells onto a row at a fixed index
    /// offset. Used by both built-in and declared row fills so the column
    /// layout stays in lockstep with <see cref="BuildSchema"/>.
    /// </summary>
    private static void FillCalibrationCells(DataValue[] cells, ModelCalibration? calibration, Arena arena)
    {
        if (calibration is null)
        {
            cells[16] = DataValue.FromString("uncalibrated", arena);
            cells[17] = DataValue.Null(DataKind.Int32);
            cells[18] = DataValue.Null(DataKind.Int64);
            return;
        }

        cells[16] = DataValue.FromString(CalibrationStateName(calibration.Status), arena);

        IReadOnlyDictionary<int, CalibrationEntry> curve = calibration.Curve;
        cells[17] = curve.Count == 0
            ? DataValue.Null(DataKind.Int32)
            : DataValue.FromInt32(curve.Keys.Max());

        // weight_cost_bytes is meaningful only when a measurement has been
        // recorded (positive). Zero in the registry can mean "probe miss
        // at load time" rather than "free model"; surface as NULL so SQL
        // queries don't confuse the two.
        cells[18] = calibration.WeightCostBytes > 0
            ? DataValue.FromInt64(calibration.WeightCostBytes)
            : DataValue.Null(DataKind.Int64);
    }
}

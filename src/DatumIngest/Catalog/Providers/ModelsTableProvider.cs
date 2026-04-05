using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using DatumIngest.Catalog.Registries;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Calibration;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Origin-of-definition discriminator for <see cref="ModelsTableProvider"/>'s
/// <c>kind</c> column. <c>builtin</c> = engine-baked entry in
/// <see cref="ModelCatalog"/>; <c>declared</c> = user-written
/// <c>CREATE MODEL</c> registered in <see cref="ModelRegistry"/>. Mirrors
/// the codebase's <c>Models</c> vs <c>DeclaredModels</c> internal naming.
/// </summary>
internal static class ModelKind
{
    public const string Builtin = "builtin";
    public const string Declared = "declared";
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

    /// <summary>
    /// Creates a provider that surfaces <paramref name="modelCatalog"/>
    /// (engine-baked built-ins) and, optionally, <paramref name="declaredModels"/>
    /// (SQL-defined models registered via <c>CREATE MODEL</c>) as a single
    /// virtual table. Both registries are held by reference — entries
    /// registered after construction are visible to subsequent scans. Pass
    /// <see langword="null"/> for <paramref name="declaredModels"/> when
    /// the host has no SQL-DDL surface (rare; the standard wiring at
    /// <c>BuiltinModels.WireDefaults</c> always passes the catalog's
    /// <see cref="TableCatalog.DeclaredModels"/>).
    /// </summary>
    /// <param name="pool">Buffer pool for renting row batches.</param>
    /// <param name="modelCatalog">The built-in registry whose entries become <c>kind = "builtin"</c> rows.</param>
    /// <param name="declaredModels">Optional SQL-defined registry whose descriptors become <c>kind = "declared"</c> rows.</param>
    public ModelsTableProvider(Pool pool, ModelCatalog modelCatalog, ModelRegistry? declaredModels = null)
        : base(pool, QualifiedTableName)
    {
        _modelCatalog = modelCatalog;
        _declaredModels = declaredModels;
    }

    /// <inheritdoc/>
    public override long GetRowCount()
    {
        // Mirror ScanAsync's dedup: a builtin entry shadowed by a
        // declared entry contributes one row, not two.
        if (_declaredModels is null) return _modelCatalog.Entries.Count;
        HashSet<string> declaredNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModelDescriptor d in _declaredModels.Entries)
            declaredNames.Add(d.Name);
        long builtinShown = 0;
        foreach (ModelCatalogEntry e in _modelCatalog.Entries.Values)
        {
            if (!declaredNames.Contains(e.Name)) builtinShown++;
        }
        return builtinShown + _declaredModels.Entries.Count;
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

        var rows = new List<(string Name, bool IsBuiltin, ModelCatalogEntry? Entry, ModelDescriptor? Descriptor)>(
            _modelCatalog.Entries.Count + (_declaredModels?.Entries.Count ?? 0));
        foreach (ModelCatalogEntry entry in _modelCatalog.Entries.Values)
        {
            // Skip the shadow builtin row when a declared row exists
            // for the same name — see comment above.
            if (declaredNames.Contains(entry.Name)) continue;
            rows.Add((entry.Name, IsBuiltin: true, entry, null));
        }
        if (_declaredModels is not null)
        {
            foreach (ModelDescriptor descriptor in _declaredModels.Entries)
            {
                rows.Add((descriptor.Name, IsBuiltin: false, null, descriptor));
            }
        }
        rows.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        string modelDirectory = _modelCatalog.ModelDirectory;
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
            if (entry.IsBuiltin)
            {
                FillRow(values, entry.Entry!, modelDirectory, batch.Arena);
            }
            else
            {
                FillRowFromDescriptor(values, entry.Descriptor!, modelDirectory, batch.Arena);
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
    /// Materialises a single row of <c>system_models</c> from a catalog entry.
    /// Stat-s the file at scan time so <c>status</c> + <c>file_size_bytes</c>
    /// reflect current disk state.
    /// </summary>
    private static void FillRow(
        DataValue[] cells, ModelCatalogEntry entry, string modelDirectory, Arena arena)
    {
        string? resolvedPath = entry.RelativePath is null
            ? null
            : Path.Combine(modelDirectory, entry.RelativePath);

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
        DataValue[] cells, ModelDescriptor descriptor, string modelDirectory, Arena arena)
    {
        long? fileSize = TryStatUsingPath(descriptor.UsingPath, modelDirectory);

        cells[0]  = DataValue.FromString(descriptor.Name, arena);
        cells[1]  = DataValue.Null(DataKind.String);          // display_name
        cells[2]  = DataValue.Null(DataKind.String);          // category
        cells[3]  = DataValue.NullArrayOf(DataKind.String);   // modalities
        cells[4]  = DataValue.FromString("sql", arena);       // backend — discriminator inside the row; the `kind` column is the schema-stable signal
        cells[5]  = DataValue.Null(DataKind.String);          // parameters
        cells[6]  = DataValue.FromString(descriptor.UsingPath, arena);
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
    }

    /// <summary>
    /// Resolves a descriptor's <c>USING</c> path the same way
    /// <see cref="RoutineRegistrar"/> does at CREATE-MODEL time
    /// (<c>file://</c> stripped to absolute; otherwise relative to the
    /// model directory) and stat-s the result. Returns <see langword="null"/>
    /// when the file is gone or the path can't be resolved.
    /// </summary>
    private static long? TryStatUsingPath(string usingPath, string modelDirectory)
    {
        string resolved;
        if (usingPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            resolved = usingPath["file://".Length..];
        }
        else
        {
            resolved = Path.GetFullPath(Path.Combine(modelDirectory, usingPath));
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

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

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
/// each catalog entry's resolved file path at scan time and reports
/// <c>status = 'available'</c> when the file exists on disk, <c>'missing'</c>
/// otherwise. That liveness matters: a user runs the query, sees a missing
/// model, downloads it, runs again, and the status flips. Caching rows
/// from a single snapshot would defeat the diagnostic.
/// </para>
/// <para>
/// Schema (12 columns):
/// <list type="table">
///   <item><term>name</term><description>SQL identifier (the <c>X</c> in <c>models.X(...)</c>).</description></item>
///   <item><term>display_name</term><description>Human-readable model name.</description></item>
///   <item><term>category</term><description>Single-valued purpose: <c>llm</c>, <c>classifier</c>, <c>detector</c>, <c>embedder</c>, etc. Routing key for <c>tasks.X</c>.</description></item>
///   <item><term>modalities</term><description><c>Array&lt;String&gt;</c> — every medium the model touches (<c>["image", "text"]</c> for a captioner, <c>["text"]</c> for an LLM).</description></item>
///   <item><term>backend</term><description><c>onnx</c> / <c>llama</c> / <c>echo</c>.</description></item>
///   <item><term>parameters</term><description>Architectural param count (<c>"8B"</c>, <c>"3.5M"</c>).</description></item>
///   <item><term>file_name</term><description>Filename relative to the catalog's models directory.</description></item>
///   <item><term>file_size_bytes</term><description>Actual on-disk size, or <see langword="null"/> when missing.</description></item>
///   <item><term>license</term><description>SPDX-style or model-specific license identifier.</description></item>
///   <item><term>license_holder</term><description>Entity granting the license (Meta, Microsoft, etc.).</description></item>
///   <item><term>source_url</term><description>Repo / model-zoo URL for re-downloading.</description></item>
///   <item><term>status</term><description><c>available</c> / <c>missing</c>.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class ModelsTableProvider : ITableProvider
{
    private const int DefaultBatchSize = 64;

    /// <summary>The conventional table name registered in the catalog.</summary>
    public const string TableName = "system_models";

    private static readonly Schema _schema = BuildSchema();

    private readonly Pool _pool;
    private readonly ModelCatalog _modelCatalog;

    /// <summary>
    /// Creates a provider that surfaces <paramref name="modelCatalog"/> as a
    /// virtual table. The catalog is held by reference — entries registered
    /// after construction are visible to subsequent scans.
    /// </summary>
    /// <param name="pool">Buffer pool for renting row batches.</param>
    /// <param name="modelCatalog">The catalog whose entries become rows.</param>
    public ModelsTableProvider(Pool pool, ModelCatalog modelCatalog)
    {
        _pool = pool;
        _modelCatalog = modelCatalog;
        Name = TableName;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public bool Seekable => false;

    /// <summary>
    /// Gets whether <see cref="Dispose"/> has been called.
    /// </summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        Disposed = true;
    }

    /// <inheritdoc/>
    public long GetRowCount() => _modelCatalog.Entries.Count;

    /// <inheritdoc/>
    public Schema GetSchema() => _schema;

    /// <inheritdoc/>
    public Manifest.QueryResultsManifest? GetManifest() => null;

    /// <inheritdoc/>
    public Indexing.SourceIndex? GetSourceIndex() => null;

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        // Snapshot the catalog at scan start so concurrent registrations during a
        // long iteration don't produce inconsistent rows. The snapshot is cheap:
        // a list of references, no value materialisation.
        ModelCatalogEntry[] entries = _modelCatalog.Entries.Values
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string modelDirectory = _modelCatalog.ModelDirectory;

        // requiredColumns / filterHint are advisory; we materialise the full row
        // and let the caller's project / filter operators trim.
        _ = requiredColumns;
        _ = filterHint;

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        for (int i = 0; i < entries.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch ??= _pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);

            DataValue[] values = _pool.RentDataValues(_schema.Columns.Count);
            FillRow(values, entries[i], modelDirectory, batch.Arena);
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

    /// <inheritdoc/>
    public ISeekSession OpenSeekSession(IReadOnlySet<string>? requiredColumns, Arena? targetArena = null)
    {
        throw new NotSupportedException(
            $"{nameof(ModelsTableProvider)} does not support seek sessions; use ScanAsync.");
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

        cells[0]  = DataValue.FromString(entry.Name, arena);
        cells[1]  = WriteOptionalString(entry.DisplayName, arena);
        cells[2]  = WriteOptionalString(entry.Category, arena);
        cells[3]  = WriteOptionalStringArray(entry.Modalities, arena);
        cells[4]  = DataValue.FromString(entry.Backend, arena);
        cells[5]  = WriteOptionalString(entry.Parameters, arena);
        cells[6]  = WriteOptionalString(entry.RelativePath, arena);
        cells[7]  = fileSize.HasValue ? DataValue.FromInt64(fileSize.Value) : DataValue.Null(DataKind.Int64);
        cells[8]  = WriteOptionalString(entry.License, arena);
        cells[9]  = WriteOptionalString(entry.LicenseHolder, arena);
        cells[10] = WriteOptionalString(entry.SourceUrl, arena);
        cells[11] = DataValue.FromString(fileExists ? "available" : "missing", arena);
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
        new ColumnInfo("name",            DataKind.String, nullable: false),
        new ColumnInfo("display_name",    DataKind.String, nullable: true),
        new ColumnInfo("category",        DataKind.String, nullable: true),
        new ColumnInfo("modalities",      DataKind.String, nullable: true) { IsArray = true },
        new ColumnInfo("backend",         DataKind.String, nullable: false),
        new ColumnInfo("parameters",      DataKind.String, nullable: true),
        new ColumnInfo("file_name",       DataKind.String, nullable: true),
        new ColumnInfo("file_size_bytes", DataKind.Int64,  nullable: true),
        new ColumnInfo("license",         DataKind.String, nullable: true),
        new ColumnInfo("license_holder",  DataKind.String, nullable: true),
        new ColumnInfo("source_url",      DataKind.String, nullable: true),
        new ColumnInfo("status",          DataKind.String, nullable: false),
    ]);
}

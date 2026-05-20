using System.Runtime.CompilerServices;

using Heliosoph.DatumV.DatasetLibrary;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Catalog.Providers;

/// <summary>
/// Virtual table at <c>system.datasets</c> listing every installed
/// dataset table the engine can query. Rows reflect the
/// <see cref="DatasetSchemaBinder"/>'s install-state snapshot, so a
/// fresh install / uninstall before the next scan changes what
/// surfaces. Pre-install ("declared in the manifest but not yet
/// installed") variants are deliberately NOT surfaced — this table
/// answers "what's queryable today," not "what could the catalog
/// give me." Use the Datasets browser for the full catalog view.
/// </summary>
/// <remarks>
/// <para>
/// Schema:
/// <list type="table">
///   <item><term>schema</term><description>SQL schema the dataset binds into (default <c>"datasets"</c>; per-entry override allowed).</description></item>
///   <item><term>name</term><description>The bound table name — the <c>X</c> in <c>&lt;schema&gt;.X</c>. For single-job variants this is the variant id verbatim; for multi-job variants it's <c>&lt;variantId&gt;_&lt;tableName&gt;</c>.</description></item>
///   <item><term>variant_id</term><description>Install handle the downloader keys on. Same as <c>name</c> for single-job variants; the prefix for multi-job.</description></item>
///   <item><term>entry_name</term><description>Parent entry's user-facing name (e.g. <c>"COCO 2017"</c>).</description></item>
///   <item><term>display_name</term><description>Variant subtitle (e.g. <c>"test2017 (images)"</c>).</description></item>
///   <item><term>version</term><description>Catalog version installed (e.g. <c>"2017"</c>).</description></item>
///   <item><term>modalities</term><description><c>Array&lt;String&gt;</c> from the entry's modality vocabulary.</description></item>
///   <item><term>license_ids</term><description><c>Array&lt;String&gt;</c> from the entry's license set.</description></item>
///   <item><term>approx_archive_bytes</term><description>Manifest-declared raw archive size; the user's "this took ~6.6 GB to download" figure.</description></item>
///   <item><term>approx_ingested_bytes</term><description>Manifest-declared ingested size; close to the on-disk <c>.datum</c> footprint.</description></item>
///   <item><term>file_path</term><description>Absolute path to the bound <c>.datum</c> file. Useful for debugging and for the catalog explorer to link out.</description></item>
///   <item><term>file_size_bytes</term><description>Actual on-disk size of the <c>.datum</c>; <c>NULL</c> when the file is missing despite the install-state probe reporting installed (rare; install state caught a corrupt teardown).</description></item>
///   <item><term>status</term><description><c>"available"</c> when the file is on disk; <c>"missing"</c> when the install state lied. The binder logs a warning + skips the binding for missing files, so a missing row here means the catalog has the rebound snapshot pending a refresh.</description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class DatasetsTableProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional fully-qualified table name registered in the catalog.</summary>
    public const string TableName = "system.datasets";

    /// <summary>The canonical <see cref="QualifiedName"/> for this provider.</summary>
    public static readonly QualifiedName QualifiedTableName = new("system", "datasets");

    private static readonly Schema _schema = BuildSchema();

    private readonly DatasetSchemaBinder _binder;

    public DatasetsTableProvider(Pool pool, DatasetSchemaBinder binder)
        : base(pool, QualifiedTableName)
    {
        _binder = binder;
    }

    /// <inheritdoc/>
    public override long GetRowCount()
    {
        long count = 0;
        foreach (DatasetSchemaBinder.DatasetBindingDescriptor _ in _binder.EnumerateBindings().Where(d => d.IsInstalled))
        {
            count++;
        }
        return count;
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
        _ = requiredColumns;
        _ = filterHint;

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        foreach (DatasetSchemaBinder.DatasetBindingDescriptor desc in _binder.EnumerateBindings().Where(d => d.IsInstalled))
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);

            FileInfo info = new(desc.DatumPath);
            bool fileExists = info.Exists;
            long? fileSize = fileExists ? info.Length : null;

            DataValue[] cells = Pool.RentDataValues(_schema.Columns.Count);
            cells[0]  = DataValue.FromString(desc.Schema, batch.Arena);
            cells[1]  = DataValue.FromString(desc.Table, batch.Arena);
            cells[2]  = DataValue.FromString(desc.VariantId, batch.Arena);
            cells[3]  = DataValue.FromString(desc.EntryName, batch.Arena);
            cells[4]  = DataValue.FromString(desc.DisplayName, batch.Arena);
            cells[5]  = DataValue.FromString(desc.Version, batch.Arena);
            cells[6]  = DataValue.FromStringArray(desc.Modalities.ToArray(), batch.Arena);
            cells[7]  = DataValue.FromStringArray(desc.LicenseIds.ToArray(), batch.Arena);
            cells[8]  = DataValue.FromInt64(desc.ApproxArchiveBytes);
            cells[9]  = DataValue.FromInt64(desc.ApproxIngestedBytes);
            cells[10] = DataValue.FromString(desc.DatumPath, batch.Arena);
            cells[11] = fileSize.HasValue
                ? DataValue.FromInt64(fileSize.Value)
                : DataValue.Null(DataKind.Int64);
            cells[12] = DataValue.FromString(fileExists ? "available" : "missing", batch.Arena);
            batch.Add(cells);

            if (batch.IsFull) { yield return batch; batch = null; }
        }

        if (batch is not null) { yield return batch; }

        await Task.CompletedTask;
    }

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("schema",                DataKind.String, nullable: false),
        new ColumnInfo("name",                  DataKind.String, nullable: false),
        new ColumnInfo("variant_id",            DataKind.String, nullable: false),
        new ColumnInfo("entry_name",            DataKind.String, nullable: false),
        new ColumnInfo("display_name",          DataKind.String, nullable: false),
        new ColumnInfo("version",               DataKind.String, nullable: false),
        new ColumnInfo("modalities",            DataKind.String, nullable: false) { IsArray = true },
        new ColumnInfo("license_ids",           DataKind.String, nullable: false) { IsArray = true },
        new ColumnInfo("approx_archive_bytes",  DataKind.Int64,  nullable: false),
        new ColumnInfo("approx_ingested_bytes", DataKind.Int64,  nullable: false),
        new ColumnInfo("file_path",             DataKind.String, nullable: false),
        new ColumnInfo("file_size_bytes",       DataKind.Int64,  nullable: true),
        new ColumnInfo("status",                DataKind.String, nullable: false),
    ]);
}

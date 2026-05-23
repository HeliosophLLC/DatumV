using System.Runtime.CompilerServices;

using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Catalog.Providers;

/// <summary>
/// Virtual table that surfaces the on-disk contents of the catalog directory
/// as a SQL-queryable view. Each file under the catalog root becomes one row;
/// each row is classified by its location into a <c>kind</c> (data, udf,
/// procedure, model, view, query, manifest, gitignore, data_sidecar, other)
/// and joined against the in-memory registries so the <c>is_orphan</c> column
/// flags files on disk that have no matching registry entry.
/// </summary>
/// <remarks>
/// <para>
/// This is the "what's on disk?" complement to <c>system.udfs</c> /
/// <c>system.procedures</c> / <c>system.models</c> (which answer "what's
/// registered?"). The Project Explorer UI consumes it to render the catalog
/// tree; CLI debugging uses it to spot orphaned <c>.sql</c> files left behind
/// by a crash or hand-edit.
/// </para>
/// <para>
/// Schema:
/// <list type="table">
///   <item><term>path</term><description>Catalog-relative path, forward slashes.</description></item>
///   <item><term>kind</term><description>One of <c>data</c>, <c>data_sidecar</c>, <c>udf</c>, <c>procedure</c>, <c>model</c>, <c>view</c>, <c>query</c>, <c>manifest</c>, <c>gitignore</c>, <c>other</c>.</description></item>
///   <item><term>schema</term><description>Parsed from path when the kind has one (<c>public</c> for <c>data/public/foo.datum</c>, <c>models</c> for model files); null otherwise.</description></item>
///   <item><term>name</term><description>Filename stem for <c>data</c>/<c>data_sidecar</c>/<c>udf</c>/<c>procedure</c>/<c>model</c>; null otherwise.</description></item>
///   <item><term>size_bytes</term><description>File size from <see cref="FileInfo.Length"/>.</description></item>
///   <item><term>modified_at</term><description>UTC last-write time.</description></item>
///   <item><term>is_orphan</term><description>True when <c>kind</c> is one of the managed kinds (<c>udf</c>/<c>procedure</c>/<c>model</c>/<c>data</c>) and no manifest entry references this path. Always false for unmanaged kinds.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class SystemFilesProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional fully-qualified table name registered in the catalog.</summary>
    public const string TableName = "system.files";

    /// <summary>The canonical <see cref="QualifiedName"/> for this provider.</summary>
    public static readonly QualifiedName QualifiedTableName = new("system", "files");

    private static readonly Schema _schema = BuildSchema();

    private readonly string? _catalogDirectory;
    private readonly TableCatalog _catalog;

    /// <summary>
    /// Creates a provider rooted at <paramref name="catalogDirectory"/>. When
    /// the directory is <see langword="null"/> (in-memory catalog with no
    /// path), scans return zero rows.
    /// </summary>
    public SystemFilesProvider(Pool pool, string? catalogDirectory, TableCatalog catalog)
        : base(pool, QualifiedTableName)
    {
        _catalogDirectory = catalogDirectory;
        _catalog = catalog;
    }

    /// <inheritdoc/>
    public override long GetRowCount()
    {
        if (_catalogDirectory is null || !Directory.Exists(_catalogDirectory)) return 0;
        return Directory.EnumerateFiles(_catalogDirectory, "*", SearchOption.AllDirectories).LongCount();
    }

    /// <inheritdoc/>
    public override Schema GetSchema() => _schema;

    /// <inheritdoc/>
    public override async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        TypeIdTranslationTable? typeIdTranslations = null)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        _ = requiredColumns;
        _ = filterHint;

        if (_catalogDirectory is null || !Directory.Exists(_catalogDirectory))
        {
            yield break;
        }

        // Snapshot the set of paths each registry expects to find on disk.
        // The walker compares relative paths against this set to flag orphans.
        HashSet<string> referenced = BuildReferencedPaths();

        // Stable iteration order keeps tree-rendering in the UI deterministic.
        string[] files = Directory
            .EnumerateFiles(_catalogDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        foreach (string fullPath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relativePath = Path
                .GetRelativePath(_catalogDirectory, fullPath)
                .Replace('\\', '/');
            (string kind, string? schema, string? name) = ClassifyPath(relativePath);

            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
            }
            catch
            {
                // File may have been removed between enumeration and stat;
                // skip rather than abort the whole scan.
                continue;
            }

            bool isOrphan = IsManagedKind(kind) && !referenced.Contains(relativePath);

            batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);

            DataValue[] values = Pool.RentDataValues(_schema.Columns.Count);
            FillRow(values, relativePath, kind, schema, name, info, isOrphan, batch.Arena);
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
    /// Returns the set of catalog-relative paths the registries currently
    /// reference. Anything on disk in a <see cref="IsManagedKind"/> bucket
    /// but not in this set is an orphan. Exposed as an internal static
    /// so the REST endpoint can call it without going through the
    /// Arena/RowBatch scan path.
    /// </summary>
    internal static HashSet<string> BuildReferencedPaths(TableCatalog catalog)
    {
        HashSet<string> referenced = new(StringComparer.OrdinalIgnoreCase);

        foreach (UdfDescriptor e in catalog.Udfs.Entries)
        {
            referenced.Add(CatalogStore.UdfRelativePath(e.SchemaName, e.Name));
        }
        foreach (ProcedureDescriptor e in catalog.Procedures.Entries)
        {
            referenced.Add(CatalogStore.ProcedureRelativePath(e.SchemaName, e.Name));
        }
        foreach (ModelDescriptor e in catalog.DeclaredModels.Entries)
        {
            // Catalog-installed models have no on-disk .sql in the user's
            // catalog — only user-authored rows contribute.
            if (e.CatalogId is null)
            {
                referenced.Add(CatalogStore.ModelRelativePath(e.Name));
            }
        }
        foreach (ViewDescriptor e in catalog.Views.Entries)
        {
            referenced.Add(CatalogStore.ViewRelativePath(e.SchemaName, e.Name));
        }

        // Tables: ask the FlatFile backend for its on-disk paths. Each entry
        // already stores the path as catalog-relative; just normalise the
        // separator so the comparison matches the walker's output.
        FlatFileBackendState state = catalog.FlatFileCatalog.SnapshotBackendState();
        if (state.Tables is not null)
        {
            foreach (FlatFileTableEntry t in state.Tables)
            {
                if (!string.IsNullOrEmpty(t.FilePath))
                {
                    referenced.Add(t.FilePath.Replace('\\', '/'));
                }
            }
        }

        return referenced;
    }

    // Instance-side wrapper so the existing scan path stays unchanged.
    private HashSet<string> BuildReferencedPaths() => BuildReferencedPaths(_catalog);

    /// <summary>
    /// Classifies a catalog-relative path into a <c>kind</c> + parsed
    /// <c>(schema, name)</c>. Unknown shapes fall through to <c>other</c>
    /// so the row still surfaces in the explorer (with no schema/name).
    /// Exposed as internal so REST endpoints can reuse the classification
    /// without going through the Arena/RowBatch scan path.
    /// </summary>
    internal static (string Kind, string? Schema, string? Name) ClassifyPath(string relativePath)
    {
        string[] parts = relativePath.Split('/');

        if (parts.Length == 1)
        {
            string only = parts[0];
            if (only == CatalogStore.DefaultFileName) return ("manifest", null, null);
            if (only is ".gitignore" or ".gitattributes") return ("gitignore", null, null);
            // Top-level .sql is a user-authored saved query. Anything else
            // top-level (README, notes, data dumps) falls through to `other`.
            if (only.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                return ("query", null, Path.GetFileNameWithoutExtension(only));
            }
            return ("other", null, null);
        }

        string root = parts[0];

        // data/<schema>/<name>.datum   → ("data",         schema, stem)
        // data/<schema>/<name>.datum-* → ("data_sidecar", schema, stem-before-.datum)
        if (root == "data" && parts.Length == 3)
        {
            string schema = parts[1];
            string filename = parts[2];
            if (filename.EndsWith(".datum", StringComparison.OrdinalIgnoreCase))
            {
                return ("data", schema, Path.GetFileNameWithoutExtension(filename));
            }
            int idx = filename.IndexOf(".datum", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                return ("data_sidecar", schema, filename.Substring(0, idx));
            }
            return ("other", null, null);
        }

        if (root == "udfs" && parts.Length == 3
            && parts[2].EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            return ("udf", parts[1], Path.GetFileNameWithoutExtension(parts[2]));
        }

        if (root == "procedures" && parts.Length == 3
            && parts[2].EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            return ("procedure", parts[1], Path.GetFileNameWithoutExtension(parts[2]));
        }

        // Models always live in the `models` schema; the schema dir is elided.
        if (root == "models" && parts.Length == 2
            && parts[1].EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            return ("model", "models", Path.GetFileNameWithoutExtension(parts[1]));
        }

        if (root == "views" && parts.Length == 3
            && parts[2].EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            return ("view", parts[1], Path.GetFileNameWithoutExtension(parts[2]));
        }

        // Any other .sql under the catalog root that didn't match a managed
        // location is a user-authored saved query. Tab editor's "save" flow
        // drops files anywhere the user picks, including arbitrary sub-dirs.
        if (parts[parts.Length - 1].EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            return ("query", null, Path.GetFileNameWithoutExtension(parts[parts.Length - 1]));
        }

        return ("other", null, null);
    }

    /// <summary>
    /// <see langword="true"/> for kinds whose presence on disk is meant to be
    /// driven by a manifest entry — these are the kinds where orphan detection
    /// is meaningful. <c>data_sidecar</c> excluded because sidecars are
    /// per-table generated state, not directly registered.
    /// </summary>
    internal static bool IsManagedKind(string kind) =>
        kind is "udf" or "procedure" or "model" or "view" or "data";

    private static void FillRow(
        DataValue[] cells,
        string path,
        string kind,
        string? schema,
        string? name,
        FileInfo info,
        bool isOrphan,
        Arena arena)
    {
        cells[0] = DataValue.FromString(path, arena);
        cells[1] = DataValue.FromString(kind, arena);
        cells[2] = schema is null
            ? DataValue.Null(DataKind.String)
            : DataValue.FromString(schema, arena);
        cells[3] = name is null
            ? DataValue.Null(DataKind.String)
            : DataValue.FromString(name, arena);
        cells[4] = DataValue.FromInt64(info.Length);
        cells[5] = DataValue.FromTimestampTz(new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        cells[6] = DataValue.FromBoolean(isOrphan);
    }

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("path",        DataKind.String,      nullable: false),
        new ColumnInfo("kind",        DataKind.String,      nullable: false),
        new ColumnInfo("schema",      DataKind.String,      nullable: true),
        new ColumnInfo("name",        DataKind.String,      nullable: true),
        new ColumnInfo("size_bytes",  DataKind.Int64,       nullable: false),
        new ColumnInfo("modified_at", DataKind.TimestampTz, nullable: false),
        new ColumnInfo("is_orphan",   DataKind.Boolean,     nullable: false),
    ]);
}

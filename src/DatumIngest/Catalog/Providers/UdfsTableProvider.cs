using System.Runtime.CompilerServices;
using System.Text;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Virtual table that surfaces the contents of a <see cref="UdfRegistry"/> as
/// a SQL-queryable view. Users introspect the registered UDFs with
/// <c>SELECT * FROM system_udfs</c> — what's defined, what each one's
/// parameter list and body look like, and which ones are callable in the
/// current session.
/// </summary>
/// <remarks>
/// <para>
/// Rows materialise on every <see cref="ScanAsync"/> call. The provider
/// snapshots the registry at scan start so a long iteration sees a stable
/// view even if a concurrent <c>CREATE FUNCTION</c> runs.
/// </para>
/// <para>
/// Schema (5 columns):
/// <list type="table">
///   <item><term>name</term><description>Unqualified UDF name. Call sites use the <c>udf.</c> prefix (<c>udf.name</c>).</description></item>
///   <item><term>parameter_count</term><description>Number of declared parameters. <c>0</c> for nullary UDFs.</description></item>
///   <item><term>parameters</term><description>Comma-separated rendition of the parameter list, <c>"name TYPE, name TYPE"</c>. Empty string for nullary UDFs.</description></item>
///   <item><term>return_type</term><description>Declared <c>RETURNS</c> annotation, or NULL when the UDF was registered without one.</description></item>
///   <item><term>body</term><description>The body expression formatted via <see cref="QueryExplainer.FormatExpression"/>. Reflects the AST as parsed; whitespace and parenthesisation may differ from the user's input.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class UdfsTableProvider : ITableProvider
{
    private const int DefaultBatchSize = 64;

    /// <summary>The conventional table name registered in the catalog.</summary>
    public const string TableName = "system_udfs";

    private static readonly Schema _schema = BuildSchema();

    private readonly Pool _pool;
    private readonly UdfRegistry _registry;

    /// <summary>
    /// Creates a provider that surfaces <paramref name="registry"/> as a
    /// virtual table. The registry is held by reference — entries
    /// registered after construction are visible to subsequent scans.
    /// </summary>
    /// <param name="pool">Buffer pool for renting row batches.</param>
    /// <param name="registry">The registry whose entries become rows.</param>
    public UdfsTableProvider(Pool pool, UdfRegistry registry)
    {
        _pool = pool;
        _registry = registry;
        Name = TableName;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public bool Seekable => false;

    /// <summary>Whether <see cref="Dispose"/> has been called.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc/>
    public void Dispose() => Disposed = true;

    /// <inheritdoc/>
    public long GetRowCount() => _registry.Entries.Count;

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

        // Snapshot the registry at scan start so concurrent registrations
        // during a long iteration don't produce inconsistent rows.
        UdfDescriptor[] entries = _registry.Entries.Values
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // requiredColumns / filterHint are advisory; we materialise the full
        // row and let the caller's project / filter operators trim.
        _ = requiredColumns;
        _ = filterHint;

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        for (int i = 0; i < entries.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch ??= _pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);

            DataValue[] values = _pool.RentDataValues(_schema.Columns.Count);
            FillRow(values, entries[i], batch.Arena);
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
            $"{nameof(UdfsTableProvider)} does not support seek sessions; use ScanAsync.");
    }

    private static void FillRow(DataValue[] cells, UdfDescriptor descriptor, Arena arena)
    {
        cells[0] = DataValue.FromString(descriptor.Name, arena);
        cells[1] = DataValue.FromInt32(descriptor.Parameters.Count);
        cells[2] = DataValue.FromString(FormatParameters(descriptor.Parameters), arena);
        cells[3] = descriptor.ReturnTypeName is null
            ? DataValue.Null(DataKind.String)
            : DataValue.FromString(descriptor.ReturnTypeName, arena);
        cells[4] = DataValue.FromString(QueryExplainer.FormatExpression(descriptor.Body), arena);
    }

    /// <summary>
    /// Renders a parameter list as <c>"name1 TYPE1, name2 TYPE2"</c>.
    /// Empty input produces an empty string rather than NULL — the row's
    /// <c>parameter_count</c> column already disambiguates nullary UDFs,
    /// and an empty string keeps the column non-nullable in the schema.
    /// </summary>
    private static string FormatParameters(IReadOnlyList<UdfParameter> parameters)
    {
        if (parameters.Count == 0) return string.Empty;

        StringBuilder sb = new();
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(parameters[i].Name);
            sb.Append(' ');
            sb.Append(parameters[i].TypeName);
        }
        return sb.ToString();
    }

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("name",            DataKind.String, nullable: false),
        new ColumnInfo("parameter_count", DataKind.Int32,  nullable: false),
        new ColumnInfo("parameters",      DataKind.String, nullable: false),
        new ColumnInfo("return_type",     DataKind.String, nullable: true),
        new ColumnInfo("body",            DataKind.String, nullable: false),
    ]);
}

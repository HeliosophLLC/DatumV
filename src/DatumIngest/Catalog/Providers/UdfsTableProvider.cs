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
/// Schema (7 columns):
/// <list type="table">
///   <item><term>name</term><description>Unqualified UDF name. Call sites use the <c>udf.</c> prefix (<c>udf.name</c>).</description></item>
///   <item><term>parameter_count</term><description>Number of declared parameters. <c>0</c> for nullary UDFs.</description></item>
///   <item><term>parameters</term><description>Comma-separated rendition of the parameter list, <c>"name TYPE, name TYPE"</c>. Empty string for nullary UDFs.</description></item>
///   <item><term>return_type</term><description>Declared <c>RETURNS</c> annotation, or NULL when the UDF was registered without one.</description></item>
///   <item><term>body_kind</term><description><c>"macro"</c> for inline-expression UDFs, <c>"procedural"</c> for <c>BEGIN…END</c> bodies.</description></item>
///   <item><term>is_pure</term><description><c>TRUE</c> for UDFs declared <c>CREATE PURE FUNCTION</c>; carries the referential-transparency assertion the planner's CSE pass honours.</description></item>
///   <item><term>body</term><description>For macros, the body expression formatted via <see cref="QueryExplainer.FormatExpression"/>. For procedural UDFs, the verbatim <c>CREATE FUNCTION</c> source text. Either way, the column is non-null.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class UdfsTableProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional fully-qualified table name registered in the catalog.</summary>
    public const string TableName = "system.udfs";

    /// <summary>The canonical <see cref="QualifiedName"/> for this provider.</summary>
    public static readonly QualifiedName QualifiedTableName = new("system", "udfs");

    private static readonly Schema _schema = BuildSchema();

    private readonly UdfRegistry _registry;

    /// <summary>
    /// Creates a provider that surfaces <paramref name="registry"/> as a
    /// virtual table. The registry is held by reference — entries
    /// registered after construction are visible to subsequent scans.
    /// </summary>
    /// <param name="pool">Buffer pool for renting row batches.</param>
    /// <param name="registry">The registry whose entries become rows.</param>
    public UdfsTableProvider(Pool pool, UdfRegistry registry) : base(pool, QualifiedTableName)
    {
        _registry = registry;
    }

    /// <inheritdoc/>
    public override long GetRowCount() => _registry.Entries.Count;

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

        // Snapshot the registry at scan start so concurrent registrations
        // during a long iteration don't produce inconsistent rows.
        UdfDescriptor[] entries = _registry.Entries
            .OrderBy(e => e.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
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

            batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);

            DataValue[] values = Pool.RentDataValues(_schema.Columns.Count);
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

    private static void FillRow(DataValue[] cells, UdfDescriptor descriptor, Arena arena)
    {
        cells[0] = DataValue.FromString(descriptor.SchemaName, arena);
        cells[1] = DataValue.FromString(descriptor.Name, arena);
        cells[2] = DataValue.FromInt32(descriptor.Parameters.Count);
        cells[3] = DataValue.FromString(FormatParameters(descriptor.Parameters), arena);
        cells[4] = descriptor.ReturnTypeName is null
            ? DataValue.Null(DataKind.String)
            : DataValue.FromString(
                descriptor.ReturnIsNotNull
                    ? descriptor.ReturnTypeName + " IS NOT NULL"
                    : descriptor.ReturnTypeName,
                arena);
        cells[5] = DataValue.FromString(
            descriptor.IsProcedural ? "procedural" : "macro",
            arena);
        cells[6] = DataValue.FromBoolean(descriptor.IsPure);
        cells[7] = DataValue.FromString(FormatBody(descriptor), arena);
    }

    /// <summary>
    /// Renders a UDF's body for the <c>body</c> column. Macros use the
    /// expression formatter so the AST round-trips as a SQL fragment;
    /// procedural UDFs surface their stored source text (the full
    /// <c>CREATE FUNCTION</c> statement, mirroring how <c>system_procedures</c>
    /// renders procedure bodies). Falls back to a placeholder when neither
    /// representation is available — only reachable for descriptors built
    /// programmatically without source text.
    /// </summary>
    private static string FormatBody(UdfDescriptor descriptor)
    {
        if (descriptor.IsProcedural)
        {
            return descriptor.SourceText ?? $"CREATE FUNCTION {descriptor.Name} -- procedural body unavailable";
        }
        return descriptor.ExpressionBody is null
            ? $"-- {descriptor.Name}: macro body missing"
            : QueryExplainer.FormatExpression(descriptor.ExpressionBody);
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
            UdfParameter p = parameters[i];
            sb.Append('@');
            sb.Append(p.Name);
            sb.Append(' ');
            sb.Append(p.TypeName);
            if (p.IsNotNull)
            {
                sb.Append(" IS NOT NULL");
            }
            if (p.Default is not null)
            {
                sb.Append(" = ");
                sb.Append(QueryExplainer.FormatExpression(p.Default));
            }
        }
        return sb.ToString();
    }

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("schema",          DataKind.String,  nullable: false),
        new ColumnInfo("name",            DataKind.String,  nullable: false),
        new ColumnInfo("parameter_count", DataKind.Int32,   nullable: false),
        new ColumnInfo("parameters",      DataKind.String,  nullable: false),
        new ColumnInfo("return_type",     DataKind.String,  nullable: true),
        new ColumnInfo("body_kind",       DataKind.String,  nullable: false),
        new ColumnInfo("is_pure",         DataKind.Boolean, nullable: false),
        new ColumnInfo("body",            DataKind.String,  nullable: false),
    ]);
}

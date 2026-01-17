using System.Runtime.CompilerServices;
using System.Text;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Virtual table that surfaces the contents of a <see cref="ProcedureRegistry"/>
/// as a SQL-queryable view. Users introspect the registered procedures with
/// <c>SELECT * FROM system_procedures</c> — what's defined, what each one's
/// parameter list and source look like, and which ones are callable in the
/// current session.
/// </summary>
/// <remarks>
/// <para>
/// Rows materialise on every <see cref="ScanAsync"/> call. The provider
/// snapshots the registry at scan start so a long iteration sees a stable
/// view even if a concurrent <c>CREATE PROCEDURE</c> runs.
/// </para>
/// <para>
/// Schema (4 columns):
/// <list type="table">
///   <item><term>name</term><description>Unqualified procedure name. Call sites use the <c>proc.</c> prefix.</description></item>
///   <item><term>parameter_count</term><description>Number of declared parameters. <c>0</c> for nullary procedures.</description></item>
///   <item><term>parameters</term><description>Comma-separated rendition of the parameter list, <c>"@name TYPE [IS NOT NULL], @name TYPE"</c>. Empty string for nullary procedures.</description></item>
///   <item><term>source_text</term><description>The original CREATE PROCEDURE source as registered. Whitespace and comments are preserved.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class ProceduresTableProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional table name registered in the catalog.</summary>
    public const string TableName = "system_procedures";

    private static readonly Schema _schema = BuildSchema();

    private readonly ProcedureRegistry _registry;

    /// <summary>
    /// Creates a provider that surfaces <paramref name="registry"/> as a
    /// virtual table. The registry is held by reference — entries
    /// registered after construction are visible to subsequent scans.
    /// </summary>
    /// <param name="pool">Buffer pool for renting row batches.</param>
    /// <param name="registry">The registry whose entries become rows.</param>
    public ProceduresTableProvider(Pool pool, ProcedureRegistry registry) : base(pool, TableName)
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
        ProcedureDescriptor[] entries = _registry.Entries.Values
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

    private static void FillRow(DataValue[] cells, ProcedureDescriptor descriptor, Arena arena)
    {
        cells[0] = DataValue.FromString(descriptor.Name, arena);
        cells[1] = DataValue.FromInt32(descriptor.Parameters.Count);
        cells[2] = DataValue.FromString(FormatParameters(descriptor.Parameters), arena);
        cells[3] = DataValue.FromString(descriptor.SourceText, arena);
    }

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
                sb.Append(DatumIngest.Execution.QueryExplainer.FormatExpression(p.Default));
            }
        }
        return sb.ToString();
    }

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("name",            DataKind.String, nullable: false),
        new ColumnInfo("parameter_count", DataKind.Int32,  nullable: false),
        new ColumnInfo("parameters",      DataKind.String, nullable: false),
        new ColumnInfo("source_text",     DataKind.String, nullable: false),
    ]);
}

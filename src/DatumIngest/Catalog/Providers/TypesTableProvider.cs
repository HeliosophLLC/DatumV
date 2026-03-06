using System.Runtime.CompilerServices;

using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Virtual table surfacing the engine-defined named-type vocabulary
/// (<see cref="NamedTypeRegistry.Entries"/>) as a SQL-queryable view at
/// <c>system.types</c>. Users introspect the registered composite struct
/// shapes that <c>CREATE MODEL</c> bodies and <c>IMPLEMENTS</c> contracts
/// reference by name.
/// </summary>
/// <remarks>
/// Schema (3 columns):
/// <list type="table">
///   <item><term>name</term><description>Canonical identifier (<c>"ScoredClass"</c>, <c>"BoundingBox"</c>, …).</description></item>
///   <item><term>kind</term><description>Always <c>"named-struct"</c> in v1; future user <c>CREATE TYPE</c> may add other discriminators.</description></item>
///   <item><term>definition</term><description>Human-readable struct shape, e.g. <c>"Struct&lt;class: Int32, score: Float32&gt;"</c>.</description></item>
/// </list>
/// </remarks>
public sealed class TypesTableProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional fully-qualified table name.</summary>
    public const string TableName = "system.types";

    /// <summary>The canonical <see cref="QualifiedName"/> for this provider.</summary>
    public static readonly QualifiedName QualifiedTableName = new("system", "types");

    private static readonly Schema _schema = BuildSchema();

    /// <summary>
    /// Creates a provider over the static named-type vocabulary. No
    /// per-instance state is captured — every scan walks
    /// <see cref="NamedTypeRegistry.Entries"/> directly.
    /// </summary>
    public TypesTableProvider(Pool pool)
        : base(pool, QualifiedTableName)
    {
    }

    /// <inheritdoc/>
    public override long GetRowCount() => NamedTypeRegistry.Entries.Count;

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

        // requiredColumns / filterHint are advisory; the caller's project /
        // filter operators handle trimming.
        _ = requiredColumns;
        _ = filterHint;

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        foreach (NamedTypeRegistry.NamedTypeDefinition def in NamedTypeRegistry.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);

            DataValue[] cells = Pool.RentDataValues(_schema.Columns.Count);
            cells[0] = DataValue.FromString(def.Name, batch.Arena);
            cells[1] = DataValue.FromString("named-struct", batch.Arena);
            cells[2] = DataValue.FromString(def.Description, batch.Arena);
            batch.Add(cells);

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

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("name",       DataKind.String, nullable: false),
        new ColumnInfo("kind",       DataKind.String, nullable: false),
        new ColumnInfo("definition", DataKind.String, nullable: false),
    ]);
}

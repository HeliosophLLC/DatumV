using System.Runtime.CompilerServices;

using DatumIngest.Catalog.Registries;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Virtual table that surfaces the contents of a <see cref="ViewRegistry"/>
/// as a SQL-queryable view. Users introspect the registered views with
/// <c>SELECT * FROM system.views</c> — schema, name, and the verbatim
/// <c>CREATE VIEW</c> source text.
/// </summary>
public sealed class ViewsTableProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional fully-qualified table name registered in the catalog.</summary>
    public const string TableName = "system.views";

    /// <summary>The canonical <see cref="QualifiedName"/> for this provider.</summary>
    public static readonly QualifiedName QualifiedTableName = new("system", "views");

    private static readonly Schema _schema = new(
    [
        new ColumnInfo("schema",      DataKind.String, nullable: false),
        new ColumnInfo("name",        DataKind.String, nullable: false),
        new ColumnInfo("source_text", DataKind.String, nullable: false),
    ]);

    private static readonly string[] ColumnNames = ["schema", "name", "source_text"];

    private readonly ViewRegistry _registry;

    /// <param name="pool">Buffer pool for renting row batches.</param>
    /// <param name="registry">The view registry whose entries become rows.</param>
    public ViewsTableProvider(Pool pool, ViewRegistry registry) : base(pool, QualifiedTableName)
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

        ViewDescriptor[] entries = _registry.Entries
            .OrderBy(e => e.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _ = requiredColumns;
        _ = filterHint;

        ColumnLookup lookup = new(ColumnNames);
        RowBatch? batch = null;

        for (int i = 0; i < entries.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
            DataValue[] values = Pool.RentDataValues(3);
            values[0] = DataValue.FromString(entries[i].SchemaName, batch.Arena);
            values[1] = DataValue.FromString(entries[i].Name, batch.Arena);
            values[2] = DataValue.FromString(entries[i].SourceText, batch.Arena);
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
}

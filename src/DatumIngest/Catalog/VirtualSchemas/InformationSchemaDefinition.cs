using DatumIngest.Model;

namespace DatumIngest.Catalog.VirtualSchemas;

/// <summary>
/// The <c>information_schema</c> virtual schema, providing PostgreSQL-compatible metadata views
/// for tables, columns, and schemata visible in the current catalog.
/// </summary>
internal sealed class InformationSchemaDefinition : IVirtualSchema
{
    private static readonly Dictionary<string, IVirtualTableSource> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tables"] = new TablesSource(),
        ["columns"] = new ColumnsSource(),
        ["schemata"] = new SchemataSource(),
    };

    /// <inheritdoc />
    public string Name => "information_schema";

    /// <inheritdoc />
    public IReadOnlyList<string> TableNames { get; } = ["tables", "columns", "schemata"];

    /// <inheritdoc />
    public IVirtualTableSource? TryResolve(string tableName)
    {
        Sources.TryGetValue(tableName, out IVirtualTableSource? source);
        return source;
    }

    // ─────────────────── information_schema.tables ───────────────────

    /// <summary>
    /// Lists all tables visible in the current catalog, classifying each as
    /// <c>BASE TABLE</c> or <c>TEMPORARY TABLE</c> and assigning it to the
    /// <c>public</c> or <c>temp</c> schema accordingly.
    /// </summary>
    private sealed class TablesSource : IVirtualTableSource
    {
        private static readonly Schema OutputSchema = new(
        [
            new ColumnInfo("table_catalog", DataKind.String, false),
            new ColumnInfo("table_schema", DataKind.String, false),
            new ColumnInfo("table_name", DataKind.String, false),
            new ColumnInfo("table_type", DataKind.String, false),
        ]);

        private static readonly string[] ColumnNames = ["table_catalog", "table_schema", "table_name", "table_type"];

        public Schema GetSchema() => OutputSchema;

        public async IAsyncEnumerable<RowBatch> ScanAsync(
            VirtualTableContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RowBatch batch = RowBatch.Rent(64);

            foreach (string tableName in context.Catalog.TableNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool isTemporary = false;
                if (context.Catalog.TryResolve(tableName, out TableDescriptor? descriptor) && descriptor is not null)
                {
                    isTemporary = descriptor.Mutability == TableMutability.SessionOwned;
                }

                DataValue[] values =
                [
                    DataValue.FromString("datum"),
                    DataValue.FromString(isTemporary ? "temp" : "public"),
                    DataValue.FromString(tableName),
                    DataValue.FromString(isTemporary ? "TEMPORARY TABLE" : "BASE TABLE"),
                ];

                batch.Add(new Row(ColumnNames, values));

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = RowBatch.Rent(64);
                }
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }
            else
            {
                batch.Return();
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    // ─────────────────── information_schema.columns ───────────────────

    /// <summary>
    /// Lists all columns of all tables visible in the current catalog, including
    /// ordinal position, data type (as DatumIngest <see cref="DataKind"/> name),
    /// and nullability.
    /// </summary>
    private sealed class ColumnsSource : IVirtualTableSource
    {
        private static readonly Schema OutputSchema = new(
        [
            new ColumnInfo("table_catalog", DataKind.String, false),
            new ColumnInfo("table_schema", DataKind.String, false),
            new ColumnInfo("table_name", DataKind.String, false),
            new ColumnInfo("column_name", DataKind.String, false),
            new ColumnInfo("ordinal_position", DataKind.Int32, false),
            new ColumnInfo("data_type", DataKind.String, false),
            new ColumnInfo("is_nullable", DataKind.String, false),
        ]);

        private static readonly string[] ColumnNames =
            ["table_catalog", "table_schema", "table_name", "column_name", "ordinal_position", "data_type", "is_nullable"];

        public Schema GetSchema() => OutputSchema;

        public async IAsyncEnumerable<RowBatch> ScanAsync(
            VirtualTableContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RowBatch batch = RowBatch.Rent(64);

            foreach (string tableName in context.Catalog.TableNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool isTemporary = false;
                if (context.Catalog.TryResolve(tableName, out TableDescriptor? descriptor) && descriptor is not null)
                {
                    isTemporary = descriptor.Mutability == TableMutability.SessionOwned;
                }

                Schema tableSchema;
                try
                {
                    tableSchema = await context.Catalog.GetSchemaAsync(tableName, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Skip tables whose schema cannot be resolved (e.g. file missing).
                    continue;
                }

                string schemaName = isTemporary ? "temp" : "public";

                for (int ordinal = 0; ordinal < tableSchema.Columns.Count; ordinal++)
                {
                    ColumnInfo column = tableSchema.Columns[ordinal];

                    DataValue[] values =
                    [
                        DataValue.FromString("datum"),
                        DataValue.FromString(schemaName),
                        DataValue.FromString(tableName),
                        DataValue.FromString(column.Name),
                        DataValue.FromInt32(ordinal + 1),
                        DataValue.FromString(column.Kind.ToString()),
                        DataValue.FromString(column.Nullable ? "YES" : "NO"),
                    ];

                    batch.Add(new Row(ColumnNames, values));

                    if (batch.IsFull)
                    {
                        yield return batch;
                        batch = RowBatch.Rent(64);
                    }
                }
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }
            else
            {
                batch.Return();
            }
        }
    }

    // ─────────────────── information_schema.schemata ───────────────────

    /// <summary>
    /// Lists the known schema namespaces: <c>public</c>, <c>temp</c>,
    /// <c>information_schema</c>, and <c>datum_catalog</c>.
    /// </summary>
    private sealed class SchemataSource : IVirtualTableSource
    {
        private static readonly Schema OutputSchema = new(
        [
            new ColumnInfo("catalog_name", DataKind.String, false),
            new ColumnInfo("schema_name", DataKind.String, false),
        ]);

        private static readonly string[] ColumnNames = ["catalog_name", "schema_name"];

        private static readonly string[] SchemaNames = ["public", "temp", "information_schema", "datum_catalog"];

        public Schema GetSchema() => OutputSchema;

        public async IAsyncEnumerable<RowBatch> ScanAsync(
            VirtualTableContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RowBatch batch = RowBatch.Rent(4);

            foreach (string schemaName in SchemaNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DataValue[] values =
                [
                    DataValue.FromString("datum"),
                    DataValue.FromString(schemaName),
                ];

                batch.Add(new Row(ColumnNames, values));
            }

            yield return batch;

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}

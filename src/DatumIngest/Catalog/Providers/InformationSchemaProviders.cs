using System.Runtime.CompilerServices;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// <c>information_schema.tables</c> — lists every table registered in the catalog,
/// classifying each by type and schema. Query as <c>SELECT * FROM information_schema.tables</c>.
/// </summary>
/// <remarks>
/// Schema: <c>table_catalog</c>, <c>table_schema</c>, <c>table_name</c>, <c>table_type</c>.
/// <c>table_catalog</c> is always <c>"datum"</c>. Schema assignment follows a name-prefix
/// convention: <c>information_schema.*</c> and <c>datum_catalog.*</c> providers report their
/// owning schema; all other providers are reported under <c>"public"</c>.
/// </remarks>
internal sealed class InformationSchemaTablesProvider : NonSeekableTableProviderBase
{
    /// <summary>The SQL-queryable name of this virtual table.</summary>
    public const string TableName = "information_schema.tables";

    private static readonly Schema _schema = new(
    [
        new ColumnInfo("table_catalog", DataKind.String, nullable: false),
        new ColumnInfo("table_schema",  DataKind.String, nullable: false),
        new ColumnInfo("table_name",    DataKind.String, nullable: false),
        new ColumnInfo("table_type",    DataKind.String, nullable: false),
    ]);

    private static readonly string[] ColumnNames =
        ["table_catalog", "table_schema", "table_name", "table_type"];

    private readonly TableCatalog _catalog;

    /// <param name="pool">Buffer pool for renting row batches.</param>
    /// <param name="catalog">Catalog whose providers become rows. Held by reference.</param>
    public InformationSchemaTablesProvider(Pool pool, TableCatalog catalog)
        : base(pool, QualifiedName.Parse(TableName))
    {
        _catalog = catalog;
    }

    /// <inheritdoc/>
    public override long GetRowCount() => 0;

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

        ColumnLookup lookup = new(ColumnNames);
        RowBatch? batch = null;

        foreach (ITableProvider provider in _catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (string schema, string type) = ClassifyProvider(provider.Name);

            batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
            DataValue[] values = Pool.RentDataValues(4);
            values[0] = DataValue.FromString("datum", batch.Arena);
            values[1] = DataValue.FromString(schema, batch.Arena);
            values[2] = DataValue.FromString(provider.Name.Name, batch.Arena);
            values[3] = DataValue.FromString(type, batch.Arena);
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

    internal static (string Schema, string Type) ClassifyProvider(QualifiedName name)
    {
        if (string.Equals(name.Schema, "information_schema", StringComparison.OrdinalIgnoreCase))
            return ("information_schema", "VIEW");
        if (string.Equals(name.Schema, "datum_catalog", StringComparison.OrdinalIgnoreCase))
            return ("datum_catalog", "VIEW");
        if (string.Equals(name.Schema, "system", StringComparison.OrdinalIgnoreCase))
            return ("system", "VIEW");
        return (name.Schema, "BASE TABLE");
    }
}

/// <summary>
/// <c>information_schema.columns</c> — lists every column of every table in the catalog.
/// Query as <c>SELECT * FROM information_schema.columns</c>.
/// </summary>
/// <remarks>
/// Schema: <c>table_catalog</c>, <c>table_schema</c>, <c>table_name</c>,
/// <c>column_name</c>, <c>ordinal_position</c>, <c>data_type</c>, <c>is_nullable</c>.
/// </remarks>
internal sealed class InformationSchemaColumnsProvider : NonSeekableTableProviderBase
{
    /// <summary>The SQL-queryable name of this virtual table.</summary>
    public const string TableName = "information_schema.columns";

    private static readonly Schema _schema = new(
    [
        new ColumnInfo("table_catalog",    DataKind.String, nullable: false),
        new ColumnInfo("table_schema",     DataKind.String, nullable: false),
        new ColumnInfo("table_name",       DataKind.String, nullable: false),
        new ColumnInfo("column_name",      DataKind.String, nullable: false),
        new ColumnInfo("ordinal_position", DataKind.Int32,  nullable: false),
        new ColumnInfo("data_type",        DataKind.String, nullable: false),
        new ColumnInfo("is_nullable",      DataKind.String, nullable: false),
    ]);

    private static readonly string[] ColumnNames =
        ["table_catalog", "table_schema", "table_name", "column_name", "ordinal_position", "data_type", "is_nullable"];

    private readonly TableCatalog _catalog;

    /// <param name="pool">Buffer pool for renting row batches.</param>
    /// <param name="catalog">Catalog whose providers become rows. Held by reference.</param>
    public InformationSchemaColumnsProvider(Pool pool, TableCatalog catalog)
        : base(pool, QualifiedName.Parse(TableName))
    {
        _catalog = catalog;
    }

    /// <inheritdoc/>
    public override long GetRowCount() => 0;

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

        ColumnLookup lookup = new(ColumnNames);
        RowBatch? batch = null;

        foreach (ITableProvider provider in _catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Schema tableSchema;
            try
            {
                tableSchema = provider.GetSchema();
            }
            catch
            {
                continue;
            }

            (string schemaName, _) = InformationSchemaTablesProvider.ClassifyProvider(provider.Name);

            for (int ordinal = 0; ordinal < tableSchema.Columns.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ColumnInfo column = tableSchema.Columns[ordinal];

                batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
                DataValue[] values = Pool.RentDataValues(7);
                values[0] = DataValue.FromString("datum", batch.Arena);
                values[1] = DataValue.FromString(schemaName, batch.Arena);
                values[2] = DataValue.FromString(provider.Name.Name, batch.Arena);
                values[3] = DataValue.FromString(column.Name, batch.Arena);
                values[4] = DataValue.FromInt32(ordinal + 1);
                values[5] = DataValue.FromString(column.Kind.ToString(), batch.Arena);
                values[6] = DataValue.FromString(column.Nullable ? "YES" : "NO", batch.Arena);
                batch.Add(values);

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }
}

/// <summary>
/// <c>information_schema.table_constraints</c> — lists every named constraint on every
/// table in the catalog. Currently surfaces <c>PRIMARY KEY</c> constraints (derived from
/// the table's footer-stamped PK) and <c>UNIQUE</c> constraints (from
/// <c>CREATE UNIQUE INDEX</c>). Query as
/// <c>SELECT * FROM information_schema.table_constraints</c>.
/// </summary>
/// <remarks>
/// Schema: <c>constraint_catalog</c>, <c>constraint_schema</c>, <c>constraint_name</c>,
/// <c>table_catalog</c>, <c>table_schema</c>, <c>table_name</c>, <c>constraint_type</c>.
/// <para>
/// PK constraint names are derived as <c>&lt;table&gt;_pkey</c> to match the PostgreSQL
/// auto-naming convention. UNIQUE constraint names are the user-supplied index names from
/// <c>CREATE UNIQUE INDEX name ON …</c>. Non-unique indexes are not constraints and are
/// not listed here.
/// </para>
/// </remarks>
internal sealed class InformationSchemaTableConstraintsProvider : NonSeekableTableProviderBase
{
    /// <summary>The SQL-queryable name of this virtual table.</summary>
    public const string TableName = "information_schema.table_constraints";

    private static readonly Schema _schema = new(
    [
        new ColumnInfo("constraint_catalog", DataKind.String, nullable: false),
        new ColumnInfo("constraint_schema",  DataKind.String, nullable: false),
        new ColumnInfo("constraint_name",    DataKind.String, nullable: false),
        new ColumnInfo("table_catalog",      DataKind.String, nullable: false),
        new ColumnInfo("table_schema",       DataKind.String, nullable: false),
        new ColumnInfo("table_name",         DataKind.String, nullable: false),
        new ColumnInfo("constraint_type",    DataKind.String, nullable: false),
    ]);

    private static readonly string[] ColumnNames =
    [
        "constraint_catalog", "constraint_schema", "constraint_name",
        "table_catalog", "table_schema", "table_name", "constraint_type",
    ];

    private readonly TableCatalog _catalog;

    /// <param name="pool">Buffer pool for renting row batches.</param>
    /// <param name="catalog">Catalog whose tables are scanned for constraints. Held by reference.</param>
    public InformationSchemaTableConstraintsProvider(Pool pool, TableCatalog catalog)
        : base(pool, QualifiedName.Parse(TableName))
    {
        _catalog = catalog;
    }

    /// <inheritdoc/>
    public override long GetRowCount() => 0;

    /// <inheritdoc/>
    public override Schema GetSchema() => _schema;

    /// <summary>
    /// Returns the PostgreSQL-style auto-name for a table's PRIMARY KEY
    /// constraint: <c>&lt;table&gt;_pkey</c>. Stable and derivable so we
    /// don't need to persist it.
    /// </summary>
    internal static string PrimaryKeyConstraintName(string tableName)
        => tableName + "_pkey";

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

        ColumnLookup lookup = new(ColumnNames);
        RowBatch? batch = null;

        foreach (ITableProvider provider in _catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Schema tableSchema;
            try
            {
                tableSchema = provider.GetSchema();
            }
            catch
            {
                continue;
            }

            (string schemaName, _) = InformationSchemaTablesProvider.ClassifyProvider(provider.Name);

            // PRIMARY KEY — use the user-supplied name when one is registered
            // in the catalog, otherwise derive the PG default `<table>_pkey`.
            if (tableSchema.PrimaryKeyColumnIndices.Count > 0)
            {
                string pkName = _catalog.GetPrimaryKeyConstraintName(provider.Name.ToString());

                batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
                DataValue[] values = Pool.RentDataValues(7);
                values[0] = DataValue.FromString("datum", batch.Arena);
                values[1] = DataValue.FromString(schemaName, batch.Arena);
                values[2] = DataValue.FromString(pkName, batch.Arena);
                values[3] = DataValue.FromString("datum", batch.Arena);
                values[4] = DataValue.FromString(schemaName, batch.Arena);
                values[5] = DataValue.FromString(provider.Name.Name, batch.Arena);
                values[6] = DataValue.FromString("PRIMARY KEY", batch.Arena);
                batch.Add(values);

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }

            // UNIQUE — only persistent tables track named indexes today;
            // GetTableIndexes returns null for temp / virtual tables, which
            // naturally drops them out of the iteration.
            IReadOnlyList<IndexDescriptor>? indexes = _catalog.GetTableIndexes(provider.Name.ToString());
            if (indexes is null) continue;

            foreach (IndexDescriptor index in indexes)
            {
                if (!index.IsUnique) continue;

                batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
                DataValue[] values = Pool.RentDataValues(7);
                values[0] = DataValue.FromString("datum", batch.Arena);
                values[1] = DataValue.FromString(schemaName, batch.Arena);
                values[2] = DataValue.FromString(index.Name, batch.Arena);
                values[3] = DataValue.FromString("datum", batch.Arena);
                values[4] = DataValue.FromString(schemaName, batch.Arena);
                values[5] = DataValue.FromString(provider.Name.Name, batch.Arena);
                values[6] = DataValue.FromString("UNIQUE", batch.Arena);
                batch.Add(values);

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }
}

/// <summary>
/// <c>information_schema.key_column_usage</c> — for each named constraint, one row per
/// constraint-bearing column with the column's ordinal position in that constraint.
/// Joins to <c>information_schema.table_constraints</c> on <c>constraint_name</c>.
/// Query as <c>SELECT * FROM information_schema.key_column_usage</c>.
/// </summary>
/// <remarks>
/// Schema: <c>constraint_catalog</c>, <c>constraint_schema</c>, <c>constraint_name</c>,
/// <c>table_catalog</c>, <c>table_schema</c>, <c>table_name</c>,
/// <c>column_name</c>, <c>ordinal_position</c>.
/// <para>
/// Ordinals follow the constraint's declaration order, not the table's column order — so
/// <c>PRIMARY KEY (b, a)</c> reports <c>b</c> at ordinal 1 and <c>a</c> at ordinal 2.
/// </para>
/// </remarks>
internal sealed class InformationSchemaKeyColumnUsageProvider : NonSeekableTableProviderBase
{
    /// <summary>The SQL-queryable name of this virtual table.</summary>
    public const string TableName = "information_schema.key_column_usage";

    private static readonly Schema _schema = new(
    [
        new ColumnInfo("constraint_catalog", DataKind.String, nullable: false),
        new ColumnInfo("constraint_schema",  DataKind.String, nullable: false),
        new ColumnInfo("constraint_name",    DataKind.String, nullable: false),
        new ColumnInfo("table_catalog",      DataKind.String, nullable: false),
        new ColumnInfo("table_schema",       DataKind.String, nullable: false),
        new ColumnInfo("table_name",         DataKind.String, nullable: false),
        new ColumnInfo("column_name",        DataKind.String, nullable: false),
        new ColumnInfo("ordinal_position",   DataKind.Int32,  nullable: false),
    ]);

    private static readonly string[] ColumnNames =
    [
        "constraint_catalog", "constraint_schema", "constraint_name",
        "table_catalog", "table_schema", "table_name",
        "column_name", "ordinal_position",
    ];

    private readonly TableCatalog _catalog;

    /// <param name="pool">Buffer pool for renting row batches.</param>
    /// <param name="catalog">Catalog whose tables are scanned for constraint column membership. Held by reference.</param>
    public InformationSchemaKeyColumnUsageProvider(Pool pool, TableCatalog catalog)
        : base(pool, QualifiedName.Parse(TableName))
    {
        _catalog = catalog;
    }

    /// <inheritdoc/>
    public override long GetRowCount() => 0;

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

        ColumnLookup lookup = new(ColumnNames);
        RowBatch? batch = null;

        foreach (ITableProvider provider in _catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Schema tableSchema;
            try
            {
                tableSchema = provider.GetSchema();
            }
            catch
            {
                continue;
            }

            (string schemaName, _) = InformationSchemaTablesProvider.ClassifyProvider(provider.Name);

            // PRIMARY KEY columns. Ordinals follow PK declaration order
            // (PrimaryKeyColumnIndices is already in that order, e.g. [1, 0]
            // for `PRIMARY KEY (b, a)` on `(a, b)`).
            if (tableSchema.PrimaryKeyColumnIndices.Count > 0)
            {
                string pkName = _catalog.GetPrimaryKeyConstraintName(provider.Name.ToString());
                for (int p = 0; p < tableSchema.PrimaryKeyColumnIndices.Count; p++)
                {
                    int colIndex = tableSchema.PrimaryKeyColumnIndices[p];
                    if (colIndex < 0 || colIndex >= tableSchema.Columns.Count) continue;
                    string columnName = tableSchema.Columns[colIndex].Name;

                    batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
                    DataValue[] values = Pool.RentDataValues(8);
                    values[0] = DataValue.FromString("datum", batch.Arena);
                    values[1] = DataValue.FromString(schemaName, batch.Arena);
                    values[2] = DataValue.FromString(pkName, batch.Arena);
                    values[3] = DataValue.FromString("datum", batch.Arena);
                    values[4] = DataValue.FromString(schemaName, batch.Arena);
                    values[5] = DataValue.FromString(provider.Name.Name, batch.Arena);
                    values[6] = DataValue.FromString(columnName, batch.Arena);
                    values[7] = DataValue.FromInt32(p + 1);
                    batch.Add(values);

                    if (batch.IsFull)
                    {
                        yield return batch;
                        batch = null;
                    }
                }
            }

            // UNIQUE INDEX columns. Same ordinal convention as PK — the
            // index's Columns list is already in the order the user
            // supplied to `CREATE UNIQUE INDEX … (col1, col2, …)`.
            IReadOnlyList<IndexDescriptor>? indexes = _catalog.GetTableIndexes(provider.Name.ToString());
            if (indexes is null) continue;

            foreach (IndexDescriptor index in indexes)
            {
                if (!index.IsUnique) continue;

                for (int p = 0; p < index.Columns.Count; p++)
                {
                    batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
                    DataValue[] values = Pool.RentDataValues(8);
                    values[0] = DataValue.FromString("datum", batch.Arena);
                    values[1] = DataValue.FromString(schemaName, batch.Arena);
                    values[2] = DataValue.FromString(index.Name, batch.Arena);
                    values[3] = DataValue.FromString("datum", batch.Arena);
                    values[4] = DataValue.FromString(schemaName, batch.Arena);
                    values[5] = DataValue.FromString(provider.Name.Name, batch.Arena);
                    values[6] = DataValue.FromString(index.Columns[p], batch.Arena);
                    values[7] = DataValue.FromInt32(p + 1);
                    batch.Add(values);

                    if (batch.IsFull)
                    {
                        yield return batch;
                        batch = null;
                    }
                }
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }
}

/// <summary>
/// <c>information_schema.schemata</c> — lists the known SQL schema namespaces.
/// Query as <c>SELECT * FROM information_schema.schemata</c>.
/// </summary>
/// <remarks>Schema: <c>catalog_name</c>, <c>schema_name</c>.</remarks>
internal sealed class InformationSchemaSchemataProvider : NonSeekableTableProviderBase
{
    /// <summary>The SQL-queryable name of this virtual table.</summary>
    public const string TableName = "information_schema.schemata";

    private static readonly Schema _schema = new(
    [
        new ColumnInfo("catalog_name", DataKind.String, nullable: false),
        new ColumnInfo("schema_name",  DataKind.String, nullable: false),
    ]);

    private static readonly string[] ColumnNames = ["catalog_name", "schema_name"];

    private static readonly string[] SchemaNames =
        ["public", "information_schema", "datum_catalog"];

    /// <param name="pool">Buffer pool for renting row batches.</param>
    public InformationSchemaSchemataProvider(Pool pool) : base(pool, QualifiedName.Parse(TableName)) { }

    /// <inheritdoc/>
    public override long GetRowCount() => SchemaNames.Length;

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

        ColumnLookup lookup = new(ColumnNames);
        RowBatch batch = Pool.RentRowBatch(lookup, SchemaNames.Length, targetArena);

        foreach (string schemaName in SchemaNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DataValue[] values = Pool.RentDataValues(2);
            values[0] = DataValue.FromString("datum", batch.Arena);
            values[1] = DataValue.FromString(schemaName, batch.Arena);
            batch.Add(values);
        }

        yield return batch;

        await Task.CompletedTask;
    }
}

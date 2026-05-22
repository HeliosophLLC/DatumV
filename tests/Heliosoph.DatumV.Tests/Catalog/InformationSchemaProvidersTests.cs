using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// Tests for the three <c>information_schema</c> virtual table providers:
/// <see cref="InformationSchemaTablesProvider"/>,
/// <see cref="InformationSchemaColumnsProvider"/>, and
/// <see cref="InformationSchemaSchemataProvider"/>.
/// </summary>
public sealed class InformationSchemaProvidersTests : ServiceTestBase
{
    // ─────────────────────── helpers ───────────────────────

    private sealed record TablesRow(
        string TableCatalog, string TableSchema, string TableName, string TableType);

    private sealed record ColumnsRow(
        string TableCatalog, string TableSchema, string TableName,
        string ColumnName, int OrdinalPosition, string DataType, string IsNullable,
        string DataKind, int? CharacterMaximumLength, bool IsBlankPadded);

    private sealed record SchemataRow(string CatalogName, string SchemaName);

    private static async Task<List<TablesRow>> ScanTablesAsync(
        InformationSchemaTablesProvider provider)
    {
        List<TablesRow> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null,
            CancellationToken.None))
        {
            Arena arena = batch.Arena;
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                rows.Add(new TablesRow(
                    row[0].AsString(arena),
                    row[1].AsString(arena),
                    row[2].AsString(arena),
                    row[3].AsString(arena)));
            }
        }
        return rows;
    }

    private static async Task<List<ColumnsRow>> ScanColumnsAsync(
        InformationSchemaColumnsProvider provider)
    {
        List<ColumnsRow> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null,
            CancellationToken.None))
        {
            Arena arena = batch.Arena;
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                rows.Add(new ColumnsRow(
                    row[0].AsString(arena),
                    row[1].AsString(arena),
                    row[2].AsString(arena),
                    row[3].AsString(arena),
                    row[4].AsInt32(),
                    row[5].AsString(arena),
                    row[6].AsString(arena),
                    row[7].AsString(arena),
                    row[8].IsNull ? null : row[8].AsInt32(),
                    row[9].AsBoolean()));
            }
        }
        return rows;
    }

    private static async Task<List<SchemataRow>> ScanSchemataAsync(
        InformationSchemaSchemataProvider provider)
    {
        List<SchemataRow> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null,
            CancellationToken.None))
        {
            Arena arena = batch.Arena;
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                rows.Add(new SchemataRow(row[0].AsString(arena), row[1].AsString(arena)));
            }
        }
        return rows;
    }

    // ───────────── information_schema.tables ─────────────

    [Fact]
    public void Tables_AutoRegistered_InEmptyCatalog()
    {
        TableCatalog catalog = CreateCatalog();
        Assert.True(catalog.TryGetTable(InformationSchemaTablesProvider.TableName, out _));
    }

    [Fact]
    public void Tables_Schema_HasFourColumnsInOrder()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[InformationSchemaTablesProvider.TableName];
        Schema schema = provider.GetSchema();

        Assert.Equal(4, schema.Columns.Count);
        Assert.Equal("table_catalog",    schema.Columns[0].Name);
        Assert.Equal("table_schema",     schema.Columns[1].Name);
        Assert.Equal("table_name",       schema.Columns[2].Name);
        Assert.Equal("table_type",       schema.Columns[3].Name);

        Assert.All(schema.Columns, c => Assert.Equal(DataKind.String, c.Kind));
        Assert.All(schema.Columns, c => Assert.False(c.Nullable));
    }

    [Fact]
    public async Task Tables_Scan_CatalogAlwaysIs_datum()
    {
        TableCatalog catalog = CreateCatalog();
        InformationSchemaTablesProvider provider =
            (InformationSchemaTablesProvider)catalog[InformationSchemaTablesProvider.TableName];
        List<TablesRow> rows = await ScanTablesAsync(provider);

        Assert.All(rows, r => Assert.Equal("datum", r.TableCatalog));
    }

    [Fact]
    public async Task Tables_Scan_InformationSchemaProviders_ReportedAsViews()
    {
        TableCatalog catalog = CreateCatalog();
        InformationSchemaTablesProvider provider =
            (InformationSchemaTablesProvider)catalog[InformationSchemaTablesProvider.TableName];
        List<TablesRow> rows = await ScanTablesAsync(provider);

        // table_name carries the unqualified name; table_schema carries the schema.
        TablesRow? tablesRow = rows.FirstOrDefault(r =>
            r.TableName == "tables" && r.TableSchema == "information_schema");
        Assert.NotNull(tablesRow);
        Assert.Equal("information_schema", tablesRow.TableSchema);
        Assert.Equal("VIEW",               tablesRow.TableType);
    }

    [Fact]
    public async Task Tables_Scan_UserTable_ReportedAsBaseTable()
    {
        Pool pool = GetService<Pool>();
        TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(pool, "orders",
            columns: ["id", "amount"],
            rows: []));

        InformationSchemaTablesProvider provider =
            (InformationSchemaTablesProvider)catalog[InformationSchemaTablesProvider.TableName];
        List<TablesRow> rows = await ScanTablesAsync(provider);

        TablesRow? ordersRow = rows.FirstOrDefault(r => r.TableName == "orders");
        Assert.NotNull(ordersRow);
        Assert.Equal("public",     ordersRow.TableSchema);
        Assert.Equal("BASE TABLE", ordersRow.TableType);
    }

    [Fact]
    public void Tables_IsNotSeekable()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[InformationSchemaTablesProvider.TableName];
        Assert.False(provider.Seekable);
    }

    // ───────────── information_schema.columns ─────────────

    [Fact]
    public void Columns_AutoRegistered_InEmptyCatalog()
    {
        TableCatalog catalog = CreateCatalog();
        Assert.True(catalog.TryGetTable(InformationSchemaColumnsProvider.TableName, out _));
    }

    [Fact]
    public void Columns_Schema_HasTenColumnsInOrder()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[InformationSchemaColumnsProvider.TableName];
        Schema schema = provider.GetSchema();

        Assert.Equal(10, schema.Columns.Count);
        Assert.Equal("table_catalog",             schema.Columns[0].Name);
        Assert.Equal("table_schema",              schema.Columns[1].Name);
        Assert.Equal("table_name",                schema.Columns[2].Name);
        Assert.Equal("column_name",               schema.Columns[3].Name);
        Assert.Equal("ordinal_position",          schema.Columns[4].Name);
        Assert.Equal("data_type",                 schema.Columns[5].Name);
        Assert.Equal("is_nullable",               schema.Columns[6].Name);
        Assert.Equal("data_kind",                 schema.Columns[7].Name);
        Assert.Equal("character_maximum_length",  schema.Columns[8].Name);
        Assert.Equal("is_blank_padded",           schema.Columns[9].Name);
    }

    [Fact]
    public async Task Columns_Varchar_SurfacesPgDataTypeAndMaxLength()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (name VARCHAR(64))");

        InformationSchemaColumnsProvider provider =
            (InformationSchemaColumnsProvider)catalog[InformationSchemaColumnsProvider.TableName];
        List<ColumnsRow> rows = await ScanColumnsAsync(provider);
        ColumnsRow nameCol = rows.Single(r => r.TableName == "t" && r.ColumnName == "name");

        Assert.Equal("character varying", nameCol.DataType);
        Assert.Equal("String", nameCol.DataKind);
        Assert.Equal(64, nameCol.CharacterMaximumLength);
        Assert.False(nameCol.IsBlankPadded);
    }

    [Fact]
    public async Task Columns_Char_SurfacesPgDataTypeAndPaddingFlag()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (code CHAR(5))");

        InformationSchemaColumnsProvider provider =
            (InformationSchemaColumnsProvider)catalog[InformationSchemaColumnsProvider.TableName];
        List<ColumnsRow> rows = await ScanColumnsAsync(provider);
        ColumnsRow codeCol = rows.Single(r => r.TableName == "t" && r.ColumnName == "code");

        Assert.Equal("character", codeCol.DataType);
        Assert.Equal("String", codeCol.DataKind);
        Assert.Equal(5, codeCol.CharacterMaximumLength);
        Assert.True(codeCol.IsBlankPadded);
    }

    [Fact]
    public async Task Columns_BareTextOrString_SurfacesText()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (a TEXT, b String)");

        InformationSchemaColumnsProvider provider =
            (InformationSchemaColumnsProvider)catalog[InformationSchemaColumnsProvider.TableName];
        List<ColumnsRow> rows = await ScanColumnsAsync(provider);
        List<ColumnsRow> tRows = rows.Where(r => r.TableName == "t").OrderBy(r => r.OrdinalPosition).ToList();

        Assert.Equal("text",   tRows[0].DataType);
        Assert.Equal("String", tRows[0].DataKind);
        Assert.Null(tRows[0].CharacterMaximumLength);
        Assert.False(tRows[0].IsBlankPadded);

        Assert.Equal("text",   tRows[1].DataType);
        Assert.Equal("String", tRows[1].DataKind);
    }

    [Theory]
    [InlineData("Int16", "smallint")]
    [InlineData("Int32", "integer")]
    [InlineData("Int64", "bigint")]
    [InlineData("Float32", "real")]
    [InlineData("Float64", "double precision")]
    [InlineData("Boolean", "boolean")]
    [InlineData("Date", "date")]
    [InlineData("Time", "time without time zone")]
    [InlineData("TimestampTz", "timestamp with time zone")]
    [InlineData("Timestamp", "timestamp without time zone")]
    [InlineData("Uuid", "uuid")]
    [InlineData("Decimal", "numeric")]
    [InlineData("Json", "jsonb")]
    public async Task Columns_NumericAndTemporal_MapToPgDataType(string nativeKind, string expectedPg)
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan($"CREATE TEMP TABLE t (c {nativeKind})");

        InformationSchemaColumnsProvider provider =
            (InformationSchemaColumnsProvider)catalog[InformationSchemaColumnsProvider.TableName];
        List<ColumnsRow> rows = await ScanColumnsAsync(provider);
        ColumnsRow c = rows.Single(r => r.TableName == "t" && r.ColumnName == "c");

        Assert.Equal(expectedPg, c.DataType);
        Assert.Equal(nativeKind, c.DataKind);
    }

    [Fact]
    public async Task Columns_KindWithoutPgEquivalent_SurfacesUserDefined()
    {
        // UInt32, Float16, Image etc. have no clean PG analog — PG's
        // convention is to use 'USER-DEFINED' and let the consumer fall
        // back to the engine's native kind name (data_kind).
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (c UInt32)");

        InformationSchemaColumnsProvider provider =
            (InformationSchemaColumnsProvider)catalog[InformationSchemaColumnsProvider.TableName];
        List<ColumnsRow> rows = await ScanColumnsAsync(provider);
        ColumnsRow c = rows.Single(r => r.TableName == "t" && r.ColumnName == "c");

        Assert.Equal("USER-DEFINED", c.DataType);
        Assert.Equal("UInt32", c.DataKind);
    }

    [Fact]
    public async Task Columns_TypedArray_SurfacesArrayDataType()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (xs Float32[384])");

        InformationSchemaColumnsProvider provider =
            (InformationSchemaColumnsProvider)catalog[InformationSchemaColumnsProvider.TableName];
        List<ColumnsRow> rows = await ScanColumnsAsync(provider);
        ColumnsRow c = rows.Single(r => r.TableName == "t" && r.ColumnName == "xs");

        Assert.Equal("ARRAY", c.DataType);
        Assert.Equal("Float32", c.DataKind);
        // No max-length / padding for arrays.
        Assert.Null(c.CharacterMaximumLength);
        Assert.False(c.IsBlankPadded);
    }

    [Fact]
    public async Task Columns_Scan_UserTableColumns_IncludedWithCorrectOrdinals()
    {
        Pool pool = GetService<Pool>();
        TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(pool, "products",
            columns: ["sku", "price", "in_stock"],
            rows: []));

        InformationSchemaColumnsProvider provider =
            (InformationSchemaColumnsProvider)catalog[InformationSchemaColumnsProvider.TableName];
        List<ColumnsRow> rows = await ScanColumnsAsync(provider);

        List<ColumnsRow> productCols = rows
            .Where(r => r.TableName == "products")
            .OrderBy(r => r.OrdinalPosition)
            .ToList();

        Assert.Equal(3, productCols.Count);
        Assert.Equal("sku",      productCols[0].ColumnName); Assert.Equal(1, productCols[0].OrdinalPosition);
        Assert.Equal("price",    productCols[1].ColumnName); Assert.Equal(2, productCols[1].OrdinalPosition);
        Assert.Equal("in_stock", productCols[2].ColumnName); Assert.Equal(3, productCols[2].OrdinalPosition);
        Assert.All(productCols, r => Assert.Equal("datum",   r.TableCatalog));
        Assert.All(productCols, r => Assert.Equal("public",  r.TableSchema));
    }

    [Fact]
    public async Task Columns_Scan_CatalogAlwaysIs_datum()
    {
        TableCatalog catalog = CreateCatalog();
        InformationSchemaColumnsProvider provider =
            (InformationSchemaColumnsProvider)catalog[InformationSchemaColumnsProvider.TableName];
        List<ColumnsRow> rows = await ScanColumnsAsync(provider);

        Assert.All(rows, r => Assert.Equal("datum", r.TableCatalog));
    }

    [Fact]
    public void Columns_IsNotSeekable()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[InformationSchemaColumnsProvider.TableName];
        Assert.False(provider.Seekable);
    }

    // ───────────── information_schema.schemata ────────────

    [Fact]
    public void Schemata_AutoRegistered_InEmptyCatalog()
    {
        TableCatalog catalog = CreateCatalog();
        Assert.True(catalog.TryGetTable(InformationSchemaSchemataProvider.TableName, out _));
    }

    [Fact]
    public void Schemata_Schema_HasTwoColumnsInOrder()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[InformationSchemaSchemataProvider.TableName];
        Schema schema = provider.GetSchema();

        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("catalog_name", schema.Columns[0].Name);
        Assert.Equal("schema_name",  schema.Columns[1].Name);
        Assert.All(schema.Columns, c => Assert.Equal(DataKind.String, c.Kind));
        Assert.All(schema.Columns, c => Assert.False(c.Nullable));
    }

    [Fact]
    public async Task Schemata_Scan_ReturnsExpectedSchemas()
    {
        TableCatalog catalog = CreateCatalog();
        InformationSchemaSchemataProvider provider =
            (InformationSchemaSchemataProvider)catalog[InformationSchemaSchemataProvider.TableName];
        List<SchemataRow> rows = await ScanSchemataAsync(provider);

        Assert.All(rows, r => Assert.Equal("datum", r.CatalogName));
        HashSet<string> names = rows.Select(r => r.SchemaName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("public",             names);
        Assert.Contains("information_schema", names);
        Assert.Contains("system",      names);
        // S9: `models` is a real built-in schema visible to discovery.
        Assert.Contains("models",             names);
    }

    [Fact]
    public void Schemata_GetRowCount_MatchesScanOutput()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[InformationSchemaSchemataProvider.TableName];
        Assert.Equal(4, provider.GetRowCount());
    }

    [Fact]
    public void Schemata_IsNotSeekable()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[InformationSchemaSchemataProvider.TableName];
        Assert.False(provider.Seekable);
    }

    // ───────────── information_schema.table_constraints ─────────────

    private sealed record TableConstraintsRow(
        string ConstraintCatalog, string ConstraintSchema, string ConstraintName,
        string TableCatalog, string TableSchema, string TableName, string ConstraintType);

    private static async Task<List<TableConstraintsRow>> ScanTableConstraintsAsync(
        InformationSchemaTableConstraintsProvider provider)
    {
        List<TableConstraintsRow> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null,
            CancellationToken.None))
        {
            Arena arena = batch.Arena;
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                rows.Add(new TableConstraintsRow(
                    row[0].AsString(arena), row[1].AsString(arena), row[2].AsString(arena),
                    row[3].AsString(arena), row[4].AsString(arena), row[5].AsString(arena),
                    row[6].AsString(arena)));
            }
        }
        return rows;
    }

    [Fact]
    public void TableConstraints_AutoRegistered_InEmptyCatalog()
    {
        TableCatalog catalog = CreateCatalog();
        Assert.True(catalog.TryGetTable(InformationSchemaTableConstraintsProvider.TableName, out _));
    }

    [Fact]
    public void TableConstraints_Schema_HasSevenColumnsInOrder()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[InformationSchemaTableConstraintsProvider.TableName];
        Schema schema = provider.GetSchema();

        Assert.Equal(7, schema.Columns.Count);
        Assert.Equal("constraint_catalog", schema.Columns[0].Name);
        Assert.Equal("constraint_schema",  schema.Columns[1].Name);
        Assert.Equal("constraint_name",    schema.Columns[2].Name);
        Assert.Equal("table_catalog",      schema.Columns[3].Name);
        Assert.Equal("table_schema",       schema.Columns[4].Name);
        Assert.Equal("table_name",         schema.Columns[5].Name);
        Assert.Equal("constraint_type",    schema.Columns[6].Name);

        Assert.All(schema.Columns, c => Assert.Equal(DataKind.String, c.Kind));
        Assert.All(schema.Columns, c => Assert.False(c.Nullable));
    }

    [Fact]
    public async Task TableConstraints_Scan_PrimaryKey_NamedTablePkey()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE users (id Int32 PRIMARY KEY, name String)");

        InformationSchemaTableConstraintsProvider provider =
            (InformationSchemaTableConstraintsProvider)catalog[InformationSchemaTableConstraintsProvider.TableName];
        List<TableConstraintsRow> rows = await ScanTableConstraintsAsync(provider);

        TableConstraintsRow? pk = rows.FirstOrDefault(r =>
            r.TableName == "users" && r.ConstraintType == "PRIMARY KEY");
        Assert.NotNull(pk);
        Assert.Equal("users_pkey", pk.ConstraintName);
        Assert.Equal("datum",      pk.ConstraintCatalog);
        Assert.Equal("public",     pk.ConstraintSchema);
        Assert.Equal("datum",      pk.TableCatalog);
        Assert.Equal("public",     pk.TableSchema);
    }

    [Fact]
    public async Task TableConstraints_Scan_TableWithoutPk_NotListed()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE notes (text String)");

        InformationSchemaTableConstraintsProvider provider =
            (InformationSchemaTableConstraintsProvider)catalog[InformationSchemaTableConstraintsProvider.TableName];
        List<TableConstraintsRow> rows = await ScanTableConstraintsAsync(provider);

        Assert.DoesNotContain(rows, r => r.TableName == "notes");
    }

    [Fact]
    public async Task TableConstraints_Scan_CompositePrimaryKey_OneRow()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE orders (a Int32, b Int32, c String, PRIMARY KEY (a, b))");

        InformationSchemaTableConstraintsProvider provider =
            (InformationSchemaTableConstraintsProvider)catalog[InformationSchemaTableConstraintsProvider.TableName];
        List<TableConstraintsRow> rows = await ScanTableConstraintsAsync(provider);

        // Composite PK produces exactly one constraint row (the column-level
        // decomposition lives in information_schema.key_column_usage).
        List<TableConstraintsRow> orderPks = rows
            .Where(r => r.TableName == "orders" && r.ConstraintType == "PRIMARY KEY")
            .ToList();
        Assert.Single(orderPks);
        Assert.Equal("orders_pkey", orderPks[0].ConstraintName);
    }

    [Fact]
    public async Task TableConstraints_Scan_UniqueIndex_UsesUserSuppliedName()
    {
        using TableCatalog catalog = CreateCatalog(Path.Combine(
            Path.GetTempPath(), $"datum_isuq_{Guid.NewGuid():N}", ".datum-catalog.json"));
        catalog.Plan("CREATE TABLE products (id Int32 PRIMARY KEY, sku String)");
        catalog.Plan("CREATE UNIQUE INDEX my_sku_idx ON products (sku)");

        InformationSchemaTableConstraintsProvider provider =
            (InformationSchemaTableConstraintsProvider)catalog[InformationSchemaTableConstraintsProvider.TableName];
        List<TableConstraintsRow> rows = await ScanTableConstraintsAsync(provider);

        TableConstraintsRow? uq = rows.FirstOrDefault(r =>
            r.TableName == "products" && r.ConstraintType == "UNIQUE");
        Assert.NotNull(uq);
        Assert.Equal("my_sku_idx", uq.ConstraintName);
    }

    [Fact]
    public async Task TableConstraints_Scan_NonUniqueIndex_NotListed()
    {
        using TableCatalog catalog = CreateCatalog(Path.Combine(
            Path.GetTempPath(), $"datum_isnu_{Guid.NewGuid():N}", ".datum-catalog.json"));
        catalog.Plan("CREATE TABLE products (id Int32 PRIMARY KEY, sku String)");
        catalog.Plan("CREATE INDEX sku_lookup ON products (sku)");

        InformationSchemaTableConstraintsProvider provider =
            (InformationSchemaTableConstraintsProvider)catalog[InformationSchemaTableConstraintsProvider.TableName];
        List<TableConstraintsRow> rows = await ScanTableConstraintsAsync(provider);

        // Non-unique indexes are not constraints — they're plain B+Trees.
        Assert.DoesNotContain(rows, r => r.ConstraintName == "sku_lookup");
    }

    [Fact]
    public void TableConstraints_IsNotSeekable()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[InformationSchemaTableConstraintsProvider.TableName];
        Assert.False(provider.Seekable);
    }

    // ───────────── information_schema.key_column_usage ─────────────

    private sealed record KeyColumnUsageRow(
        string ConstraintCatalog, string ConstraintSchema, string ConstraintName,
        string TableCatalog, string TableSchema, string TableName,
        string ColumnName, int OrdinalPosition);

    private static async Task<List<KeyColumnUsageRow>> ScanKeyColumnUsageAsync(
        InformationSchemaKeyColumnUsageProvider provider)
    {
        List<KeyColumnUsageRow> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null,
            CancellationToken.None))
        {
            Arena arena = batch.Arena;
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                rows.Add(new KeyColumnUsageRow(
                    row[0].AsString(arena), row[1].AsString(arena), row[2].AsString(arena),
                    row[3].AsString(arena), row[4].AsString(arena), row[5].AsString(arena),
                    row[6].AsString(arena), row[7].AsInt32()));
            }
        }
        return rows;
    }

    [Fact]
    public void KeyColumnUsage_AutoRegistered_InEmptyCatalog()
    {
        TableCatalog catalog = CreateCatalog();
        Assert.True(catalog.TryGetTable(InformationSchemaKeyColumnUsageProvider.TableName, out _));
    }

    [Fact]
    public void KeyColumnUsage_Schema_HasEightColumnsInOrder()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[InformationSchemaKeyColumnUsageProvider.TableName];
        Schema schema = provider.GetSchema();

        Assert.Equal(8, schema.Columns.Count);
        Assert.Equal("constraint_catalog", schema.Columns[0].Name);
        Assert.Equal("constraint_schema",  schema.Columns[1].Name);
        Assert.Equal("constraint_name",    schema.Columns[2].Name);
        Assert.Equal("table_catalog",      schema.Columns[3].Name);
        Assert.Equal("table_schema",       schema.Columns[4].Name);
        Assert.Equal("table_name",         schema.Columns[5].Name);
        Assert.Equal("column_name",        schema.Columns[6].Name);
        Assert.Equal("ordinal_position",   schema.Columns[7].Name);
    }

    [Fact]
    public async Task KeyColumnUsage_Scan_SingleColumnPk_OneRowOrdinalOne()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE users (id Int32 PRIMARY KEY, name String)");

        InformationSchemaKeyColumnUsageProvider provider =
            (InformationSchemaKeyColumnUsageProvider)catalog[InformationSchemaKeyColumnUsageProvider.TableName];
        List<KeyColumnUsageRow> rows = await ScanKeyColumnUsageAsync(provider);

        KeyColumnUsageRow? pkCol = rows.FirstOrDefault(r =>
            r.ConstraintName == "users_pkey");
        Assert.NotNull(pkCol);
        Assert.Equal("id", pkCol.ColumnName);
        Assert.Equal(1,    pkCol.OrdinalPosition);
    }

    [Fact]
    public async Task KeyColumnUsage_Scan_CompositePk_OrdinalsMatchDeclarationOrder()
    {
        TableCatalog catalog = CreateCatalog();
        // PK is (b, a) — note the reversed order from the column list.
        catalog.Plan("CREATE TEMP TABLE orders (a Int32, b Int32, c String, PRIMARY KEY (b, a))");

        InformationSchemaKeyColumnUsageProvider provider =
            (InformationSchemaKeyColumnUsageProvider)catalog[InformationSchemaKeyColumnUsageProvider.TableName];
        List<KeyColumnUsageRow> rows = await ScanKeyColumnUsageAsync(provider);

        List<KeyColumnUsageRow> pkCols = rows
            .Where(r => r.ConstraintName == "orders_pkey")
            .OrderBy(r => r.OrdinalPosition)
            .ToList();
        Assert.Equal(2, pkCols.Count);
        // Ordinals follow PK declaration order, not the column list's order.
        Assert.Equal("b", pkCols[0].ColumnName); Assert.Equal(1, pkCols[0].OrdinalPosition);
        Assert.Equal("a", pkCols[1].ColumnName); Assert.Equal(2, pkCols[1].OrdinalPosition);
    }

    [Fact]
    public async Task KeyColumnUsage_Scan_UniqueIndex_ColumnsListed()
    {
        using TableCatalog catalog = CreateCatalog(Path.Combine(
            Path.GetTempPath(), $"datum_iskcu_{Guid.NewGuid():N}", ".datum-catalog.json"));
        catalog.Plan("CREATE TABLE products (id Int32 PRIMARY KEY, sku String, region String)");
        catalog.Plan("CREATE UNIQUE INDEX sku_region_idx ON products (sku, region)");

        InformationSchemaKeyColumnUsageProvider provider =
            (InformationSchemaKeyColumnUsageProvider)catalog[InformationSchemaKeyColumnUsageProvider.TableName];
        List<KeyColumnUsageRow> rows = await ScanKeyColumnUsageAsync(provider);

        List<KeyColumnUsageRow> uqCols = rows
            .Where(r => r.ConstraintName == "sku_region_idx")
            .OrderBy(r => r.OrdinalPosition)
            .ToList();
        Assert.Equal(2, uqCols.Count);
        Assert.Equal("sku",    uqCols[0].ColumnName); Assert.Equal(1, uqCols[0].OrdinalPosition);
        Assert.Equal("region", uqCols[1].ColumnName); Assert.Equal(2, uqCols[1].OrdinalPosition);
    }

    [Fact]
    public void KeyColumnUsage_IsNotSeekable()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[InformationSchemaKeyColumnUsageProvider.TableName];
        Assert.False(provider.Seekable);
    }
}

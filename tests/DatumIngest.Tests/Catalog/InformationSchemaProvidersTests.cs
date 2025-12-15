using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

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
        string ColumnName, int OrdinalPosition, string DataType, string IsNullable);

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
                    row[6].AsString(arena)));
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

        TablesRow? tablesRow = rows.FirstOrDefault(r =>
            r.TableName == InformationSchemaTablesProvider.TableName);
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
    public void Columns_Schema_HasSevenColumnsInOrder()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[InformationSchemaColumnsProvider.TableName];
        Schema schema = provider.GetSchema();

        Assert.Equal(7, schema.Columns.Count);
        Assert.Equal("table_catalog",    schema.Columns[0].Name);
        Assert.Equal("table_schema",     schema.Columns[1].Name);
        Assert.Equal("table_name",       schema.Columns[2].Name);
        Assert.Equal("column_name",      schema.Columns[3].Name);
        Assert.Equal("ordinal_position", schema.Columns[4].Name);
        Assert.Equal("data_type",        schema.Columns[5].Name);
        Assert.Equal("is_nullable",      schema.Columns[6].Name);
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
        Assert.Contains("datum_catalog",      names);
    }

    [Fact]
    public void Schemata_GetRowCount_MatchesScanOutput()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[InformationSchemaSchemataProvider.TableName];
        Assert.Equal(3, provider.GetRowCount());
    }

    [Fact]
    public void Schemata_IsNotSeekable()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[InformationSchemaSchemataProvider.TableName];
        Assert.False(provider.Seekable);
    }
}

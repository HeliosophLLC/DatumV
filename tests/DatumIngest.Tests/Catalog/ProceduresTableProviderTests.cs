using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <see cref="ProceduresTableProvider"/> — schema, scan-time row
/// materialisation, and integration with <see cref="TableCatalog"/>'s
/// auto-registration of <c>system_procedures</c>.
/// </summary>
public class ProceduresTableProviderTests : ServiceTestBase
{
    /// <summary>Plain-CLR snapshot of a system.procedures row.</summary>
    private sealed record SystemProcedureRow(
        string Schema,
        string Name,
        int ParameterCount,
        string Parameters,
        string SourceText);

    private static async Task<List<SystemProcedureRow>> ScanProviderAsync(ProceduresTableProvider provider)
    {
        List<SystemProcedureRow> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null,
            CancellationToken.None))
        {
            Arena arena = batch.Arena;
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                rows.Add(new SystemProcedureRow(
                    Schema: row[0].AsString(arena),
                    Name: row[1].AsString(arena),
                    ParameterCount: row[2].AsInt32(),
                    Parameters: row[3].AsString(arena),
                    SourceText: row[4].AsString(arena)));
            }
        }
        return rows;
    }

    [Fact]
    public void Schema_HasFiveColumnsInDeclaredOrder()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[ProceduresTableProvider.TableName];
        Schema schema = provider.GetSchema();

        Assert.Equal(5, schema.Columns.Count);
        Assert.Equal("schema", schema.Columns[0].Name);
        Assert.Equal("name", schema.Columns[1].Name);
        Assert.Equal("parameter_count", schema.Columns[2].Name);
        Assert.Equal("parameters", schema.Columns[3].Name);
        Assert.Equal("source_text", schema.Columns[4].Name);
    }

    [Fact]
    public void TableCatalog_AutoRegistersSystemProcedures()
    {
        TableCatalog catalog = CreateCatalog();

        Assert.True(catalog.HasTable(ProceduresTableProvider.TableName));
        Assert.True(catalog.HasTable("system.procedures"));
    }

    [Fact]
    public async Task EmptyRegistry_ScanReturnsNoRows()
    {
        TableCatalog catalog = CreateCatalog();
        ProceduresTableProvider provider = (ProceduresTableProvider)catalog[ProceduresTableProvider.TableName];

        List<SystemProcedureRow> rows = await ScanProviderAsync(provider);

        Assert.Empty(rows);
        Assert.Equal(0L, provider.GetRowCount());
    }

    [Fact]
    public async Task RegisterProcedure_AppearsInScan()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE PROCEDURE noop() AS BEGIN SELECT 1 END");

        ProceduresTableProvider provider = (ProceduresTableProvider)catalog[ProceduresTableProvider.TableName];
        List<SystemProcedureRow> rows = await ScanProviderAsync(provider);

        Assert.Single(rows);
        Assert.Equal("noop", rows[0].Name);
        Assert.Equal(0, rows[0].ParameterCount);
    }

    [Fact]
    public async Task ParametersFormat_RendersAtPrefixAndIsNotNull()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE foo(@a INT32, @b STRING IS NOT NULL) AS BEGIN SELECT @a, @b END");

        ProceduresTableProvider provider = (ProceduresTableProvider)catalog[ProceduresTableProvider.TableName];
        List<SystemProcedureRow> rows = await ScanProviderAsync(provider);

        Assert.Equal(2, rows[0].ParameterCount);
        Assert.Equal("@a INT32, @b STRING IS NOT NULL", rows[0].Parameters, ignoreCase: true);
    }

    [Fact]
    public async Task SourceText_PreservedVerbatim()
    {
        // The user's exact input — formatting and all — survives to the
        // introspection table.
        const string sql = "CREATE PROCEDURE multi_line(@x INT32) AS BEGIN\n  SET @x = @x + 1\nEND";
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(sql);

        ProceduresTableProvider provider = (ProceduresTableProvider)catalog[ProceduresTableProvider.TableName];
        List<SystemProcedureRow> rows = await ScanProviderAsync(provider);

        Assert.Equal(sql, rows[0].SourceText);
    }

    [Fact]
    public async Task RegisterMultipleProcedures_OrderedByName()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE PROCEDURE zebra() AS BEGIN SELECT 1 END");
        catalog.Plan("CREATE PROCEDURE alpha() AS BEGIN SELECT 1 END");
        catalog.Plan("CREATE PROCEDURE bravo() AS BEGIN SELECT 1 END");

        ProceduresTableProvider provider = (ProceduresTableProvider)catalog[ProceduresTableProvider.TableName];
        List<SystemProcedureRow> rows = await ScanProviderAsync(provider);

        Assert.Equal(3, rows.Count);
        Assert.Equal("alpha", rows[0].Name);
        Assert.Equal("bravo", rows[1].Name);
        Assert.Equal("zebra", rows[2].Name);
    }

    [Fact]
    public async Task DropProcedure_RemovesFromScan()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE PROCEDURE keep() AS BEGIN SELECT 1 END");
        catalog.Plan("CREATE PROCEDURE remove() AS BEGIN SELECT 1 END");
        catalog.Plan("DROP PROCEDURE remove");

        ProceduresTableProvider provider = (ProceduresTableProvider)catalog[ProceduresTableProvider.TableName];
        List<SystemProcedureRow> rows = await ScanProviderAsync(provider);

        Assert.Single(rows);
        Assert.Equal("keep", rows[0].Name);
    }
}

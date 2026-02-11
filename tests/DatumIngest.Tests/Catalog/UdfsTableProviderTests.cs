using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <see cref="UdfsTableProvider"/> — schema, scan-time row
/// materialisation, and integration with <see cref="TableCatalog"/>'s
/// auto-registration of <c>system_udfs</c>.
/// </summary>
/// <remarks>
/// Row snapshots resolve string payloads against <c>batch.Arena</c> while
/// the batch is still live. Direct DataValue copies would dangle once the
/// scan iterator advances and the batch is pool-returned.
/// </remarks>
public class UdfsTableProviderTests : ServiceTestBase
{
    /// <summary>Plain-CLR snapshot of a system.udfs row.</summary>
    private sealed record SystemUdfRow(
        string Schema,
        string Name,
        int ParameterCount,
        string Parameters,
        string? ReturnType,
        string BodyKind,
        bool IsPure,
        string Body);

    private static async Task<List<SystemUdfRow>> ScanProviderAsync(UdfsTableProvider provider)
    {
        List<SystemUdfRow> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null,
            CancellationToken.None))
        {
            Arena arena = batch.Arena;
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                rows.Add(new SystemUdfRow(
                    Schema: row[0].AsString(arena),
                    Name: row[1].AsString(arena),
                    ParameterCount: row[2].AsInt32(),
                    Parameters: row[3].AsString(arena),
                    ReturnType: row[4].IsNull ? null : row[4].AsString(arena),
                    BodyKind: row[5].AsString(arena),
                    IsPure: row[6].AsBoolean(),
                    Body: row[7].AsString(arena)));
            }
        }
        return rows;
    }

    [Fact]
    public void Schema_HasEightColumnsInDeclaredOrder()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[UdfsTableProvider.TableName];
        Schema schema = provider.GetSchema();

        Assert.Equal(8, schema.Columns.Count);
        Assert.Equal("schema", schema.Columns[0].Name);
        Assert.Equal("name", schema.Columns[1].Name);
        Assert.Equal("parameter_count", schema.Columns[2].Name);
        Assert.Equal("parameters", schema.Columns[3].Name);
        Assert.Equal("return_type", schema.Columns[4].Name);
        Assert.Equal("body_kind", schema.Columns[5].Name);
        Assert.Equal("is_pure", schema.Columns[6].Name);
        Assert.Equal("body", schema.Columns[7].Name);
    }

    [Fact]
    public void Schema_TypesAndNullability()
    {
        TableCatalog catalog = CreateCatalog();
        ITableProvider provider = catalog[UdfsTableProvider.TableName];
        Schema schema = provider.GetSchema();

        Assert.Equal(DataKind.String, schema.Columns[0].Kind);
        Assert.False(schema.Columns[0].Nullable);

        Assert.Equal(DataKind.String, schema.Columns[1].Kind);
        Assert.False(schema.Columns[1].Nullable);

        Assert.Equal(DataKind.Int32, schema.Columns[2].Kind);
        Assert.False(schema.Columns[2].Nullable);

        Assert.Equal(DataKind.String, schema.Columns[3].Kind);
        Assert.False(schema.Columns[3].Nullable);

        Assert.Equal(DataKind.String, schema.Columns[4].Kind);
        Assert.True(schema.Columns[4].Nullable);

        Assert.Equal(DataKind.String, schema.Columns[5].Kind);
        Assert.False(schema.Columns[5].Nullable);

        Assert.Equal(DataKind.Boolean, schema.Columns[6].Kind);
        Assert.False(schema.Columns[6].Nullable);

        Assert.Equal(DataKind.String, schema.Columns[7].Kind);
        Assert.False(schema.Columns[7].Nullable);
    }

    [Fact]
    public void TableCatalog_AutoRegistersSystemUdfs()
    {
        TableCatalog catalog = CreateCatalog();

        Assert.True(catalog.HasTable(UdfsTableProvider.TableName));
        Assert.True(catalog.HasTable("system.udfs"));
    }

    [Fact]
    public async Task EmptyRegistry_ScanProducesZeroRows()
    {
        TableCatalog catalog = CreateCatalog();
        UdfsTableProvider provider = (UdfsTableProvider)catalog[UdfsTableProvider.TableName];

        List<SystemUdfRow> rows = await ScanProviderAsync(provider);

        Assert.Empty(rows);
        Assert.Equal(0L, provider.GetRowCount());
    }

    [Fact]
    public async Task RegisterUdf_AppearsInScan()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE FUNCTION shout(@name STRING) AS upper(@name)");

        UdfsTableProvider provider = (UdfsTableProvider)catalog[UdfsTableProvider.TableName];
        List<SystemUdfRow> rows = await ScanProviderAsync(provider);

        Assert.Single(rows);
        Assert.Equal("shout", rows[0].Name);
        Assert.Equal(1, rows[0].ParameterCount);
    }

    [Fact]
    public async Task RegisterMultipleUdfs_OrderedByName()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE FUNCTION zebra(@x INT32) AS @x");
        catalog.Plan("CREATE FUNCTION alpha(@x INT32) AS @x + 1");
        catalog.Plan("CREATE FUNCTION bravo(@x INT32) AS @x * 2");

        UdfsTableProvider provider = (UdfsTableProvider)catalog[UdfsTableProvider.TableName];
        List<SystemUdfRow> rows = await ScanProviderAsync(provider);

        Assert.Equal(3, rows.Count);
        Assert.Equal("alpha", rows[0].Name);
        Assert.Equal("bravo", rows[1].Name);
        Assert.Equal("zebra", rows[2].Name);
    }

    [Fact]
    public async Task DropUdf_RemovedFromScan()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE FUNCTION shout(@name STRING) AS upper(@name)");
        catalog.Plan("CREATE FUNCTION whisper(@name STRING) AS lower(@name)");
        catalog.Plan("DROP FUNCTION shout");

        UdfsTableProvider provider = (UdfsTableProvider)catalog[UdfsTableProvider.TableName];
        List<SystemUdfRow> rows = await ScanProviderAsync(provider);

        Assert.Single(rows);
        Assert.Equal("whisper", rows[0].Name);
    }

    [Fact]
    public async Task ParameterCountAndList_RenderedCorrectly()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE FUNCTION add3(@a INT32, @b INT32, @c INT32) AS @a + @b + @c");

        UdfsTableProvider provider = (UdfsTableProvider)catalog[UdfsTableProvider.TableName];
        List<SystemUdfRow> rows = await ScanProviderAsync(provider);

        Assert.Equal(3, rows[0].ParameterCount);
        Assert.Equal("@a INT32, @b INT32, @c INT32", rows[0].Parameters, ignoreCase: true);
    }

    [Fact]
    public async Task NullaryUdf_ParameterCountZero_ParametersEmpty()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE FUNCTION pi() AS 3.14");

        UdfsTableProvider provider = (UdfsTableProvider)catalog[UdfsTableProvider.TableName];
        List<SystemUdfRow> rows = await ScanProviderAsync(provider);

        Assert.Equal(0, rows[0].ParameterCount);
        Assert.Equal(string.Empty, rows[0].Parameters);
    }

    [Fact]
    public async Task ReturnType_NullWhenAbsent_PopulatedWhenDeclared()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE FUNCTION no_return(@x INT32) AS @x");
        catalog.Plan("CREATE FUNCTION typed(@x INT32) RETURNS INT32 AS @x * 2");

        UdfsTableProvider provider = (UdfsTableProvider)catalog[UdfsTableProvider.TableName];
        List<SystemUdfRow> rows = await ScanProviderAsync(provider);

        SystemUdfRow noReturn = rows.Single(r => string.Equals(r.Name, "no_return", StringComparison.OrdinalIgnoreCase));
        SystemUdfRow typed = rows.Single(r => string.Equals(r.Name, "typed", StringComparison.OrdinalIgnoreCase));

        Assert.Null(noReturn.ReturnType);
        Assert.Equal("INT32", typed.ReturnType, ignoreCase: true);
    }

    [Fact]
    public async Task Body_FormattedFromAst()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE FUNCTION shout(@name STRING) AS upper(@name)");

        UdfsTableProvider provider = (UdfsTableProvider)catalog[UdfsTableProvider.TableName];
        List<SystemUdfRow> rows = await ScanProviderAsync(provider);

        // Body is rendered via QueryExplainer.FormatExpression — the exact
        // string isn't load-bearing, but it must reference the body's salient
        // tokens (the inner function name and the parameter).
        Assert.Contains("upper", rows[0].Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name", rows[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MacroUdf_BodyKindIsMacro_IsPureFalse()
    {
        // Default for AS-expression bodies. PURE has no meaning on macros and
        // is rejected at parse time, so is_pure is always false here.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE FUNCTION shout(@name STRING) AS upper(@name)");

        UdfsTableProvider provider = (UdfsTableProvider)catalog[UdfsTableProvider.TableName];
        List<SystemUdfRow> rows = await ScanProviderAsync(provider);

        Assert.Equal("macro", rows[0].BodyKind);
        Assert.False(rows[0].IsPure);
    }

    [Fact]
    public async Task ProceduralUdf_BodyKindAndPureFlag_SurfaceCorrectly()
    {
        // Procedural body with the PURE modifier. Both flags should round-trip
        // through registration into the system_udfs row.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PURE FUNCTION sq(@x INT32) RETURNS INT32 BEGIN RETURN @x * @x END");

        UdfsTableProvider provider = (UdfsTableProvider)catalog[UdfsTableProvider.TableName];
        List<SystemUdfRow> rows = await ScanProviderAsync(provider);

        Assert.Equal("procedural", rows[0].BodyKind);
        Assert.True(rows[0].IsPure);
    }

    [Fact]
    public async Task ProceduralUdf_BodyShowsVerbatimSourceText()
    {
        // For procedural UDFs, the body column carries the original CREATE
        // FUNCTION text — there's no Statement formatter, so users see the
        // SQL they wrote (mirroring how system_procedures works).
        const string sql =
            "CREATE FUNCTION pipeline(@x INT32) RETURNS INT32 BEGIN " +
                "DECLARE @y INT32 = @x + 1; " +
                "RETURN @y * 2 " +
            "END";
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(sql);

        UdfsTableProvider provider = (UdfsTableProvider)catalog[UdfsTableProvider.TableName];
        List<SystemUdfRow> rows = await ScanProviderAsync(provider);

        Assert.Equal(sql, rows[0].Body);
    }

    [Fact]
    public void Provider_IsNotSeekable()
    {
        TableCatalog catalog = CreateCatalog();
        UdfsTableProvider provider = (UdfsTableProvider)catalog[UdfsTableProvider.TableName];

        Assert.False(provider.Seekable);
        Assert.Throws<NotSupportedException>(
            () => provider.OpenSeekSession(requiredColumns: null));
    }

    [Fact]
    public async Task Disposed_ScanThrowsObjectDisposed()
    {
        TableCatalog catalog = CreateCatalog();
        UdfsTableProvider provider = (UdfsTableProvider)catalog[UdfsTableProvider.TableName];
        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await foreach (RowBatch _ in provider.ScanAsync(
                requiredColumns: null, filterHint: null, targetArena: null,
                CancellationToken.None))
            {
                // Should never reach here.
            }
        });
    }
}

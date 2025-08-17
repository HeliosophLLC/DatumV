using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <see cref="ParquetTableProvider"/> using Parquet fixture files
/// created programmatically via Parquet.Net.
/// </summary>
public sealed class ParquetTableProviderTests : IDisposable
{
    private readonly string _fixtureDirectory;

    public ParquetTableProviderTests()
    {
        _fixtureDirectory = Path.Combine(Path.GetTempPath(), "datum_parquet_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_fixtureDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_fixtureDirectory))
        {
            Directory.Delete(_fixtureDirectory, recursive: true);
        }
    }

    private string FixturePath(string fileName) => Path.Combine(_fixtureDirectory, fileName);

    private static TableDescriptor Descriptor(string filePath, Dictionary<string, string>? options = null)
    {
        return new TableDescriptor("parquet", "test", filePath, options ?? new Dictionary<string, string>());
    }

    private static async Task<List<Row>> ReadAllAsync(IAsyncEnumerable<Row> source)
    {
        List<Row> rows = new();
        await foreach (Row row in source)
        {
            rows.Add(row);
        }
        return rows;
    }

    // ───────────────────── Fixture creation helpers ─────────────────────

    /// <summary>
    /// Creates a Parquet file with three columns: id (int), name (string), score (double).
    /// </summary>
    private async Task<string> CreateSimpleFixtureAsync()
    {
        string path = FixturePath("simple.parquet");

        ParquetSchema schema = new(
            new DataField<int>("id"),
            new DataField<string>("name"),
            new DataField<double>("score"));

        using FileStream stream = File.Create(path);
        using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream);
        writer.CompressionMethod = CompressionMethod.None;

        using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[0], new int[] { 1, 2, 3 }));
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[1], new string[] { "Alice", "Bob", "Charlie" }));
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[2], new double[] { 95.5, 87.3, 91.0 }));

        return path;
    }

    /// <summary>
    /// Creates a Parquet file with float columns.
    /// </summary>
    private async Task<string> CreateFloatFixtureAsync()
    {
        string path = FixturePath("floats.parquet");

        ParquetSchema schema = new(
            new DataField<float>("value"),
            new DataField<float>("weight"));

        using FileStream stream = File.Create(path);
        using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream);
        writer.CompressionMethod = CompressionMethod.None;

        using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[0], new float[] { 1.5f, 2.5f, 3.5f }));
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[1], new float[] { 0.1f, 0.9f, 0.5f }));

        return path;
    }

    /// <summary>
    /// Creates a Parquet file with nullable integer column.
    /// </summary>
    private async Task<string> CreateNullableFixtureAsync()
    {
        string path = FixturePath("nullable.parquet");

        ParquetSchema schema = new(
            new DataField<int?>("value"));

        using FileStream stream = File.Create(path);
        using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream);
        writer.CompressionMethod = CompressionMethod.None;

        using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[0], new int?[] { 10, null, 30 }));

        return path;
    }

    /// <summary>
    /// Creates a Parquet file with byte[] (binary) column.
    /// </summary>
    private async Task<string> CreateBinaryFixtureAsync()
    {
        string path = FixturePath("binary.parquet");

        ParquetSchema schema = new(
            new DataField<string>("label"),
            new DataField<byte[]>("data"));

        using FileStream stream = File.Create(path);
        using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream);
        writer.CompressionMethod = CompressionMethod.None;

        using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[0], new string[] { "a", "b" }));
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[1], new byte[][] { [1, 2, 3], [4, 5] }));

        return path;
    }

    /// <summary>
    /// Creates a Parquet file with boolean column.
    /// </summary>
    private async Task<string> CreateBooleanFixtureAsync()
    {
        string path = FixturePath("boolean.parquet");

        ParquetSchema schema = new(
            new DataField<string>("name"),
            new DataField<bool>("active"));

        using FileStream stream = File.Create(path);
        using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream);
        writer.CompressionMethod = CompressionMethod.None;

        using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[0], new string[] { "x", "y", "z" }));
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[1], new bool[] { true, false, true }));

        return path;
    }

    /// <summary>
    /// Creates a Parquet file with long (int64) column.
    /// </summary>
    private async Task<string> CreateLongFixtureAsync()
    {
        string path = FixturePath("long.parquet");

        ParquetSchema schema = new(
            new DataField<long>("big_number"));

        using FileStream stream = File.Create(path);
        using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream);
        writer.CompressionMethod = CompressionMethod.None;

        using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[0], new long[] { 100000L, 200000L, 300000L }));

        return path;
    }

    /// <summary>
    /// Creates a Parquet file with multiple row groups.
    /// </summary>
    private async Task<string> CreateMultiRowGroupFixtureAsync()
    {
        string path = FixturePath("multi_rg.parquet");

        ParquetSchema schema = new(
            new DataField<int>("id"),
            new DataField<string>("value"));

        using FileStream stream = File.Create(path);
        using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream);
        writer.CompressionMethod = CompressionMethod.None;

        // Row group 1
        using (ParquetRowGroupWriter rowGroup1 = writer.CreateRowGroup())
        {
            await rowGroup1.WriteColumnAsync(new DataColumn(schema.DataFields[0], new int[] { 1, 2 }));
            await rowGroup1.WriteColumnAsync(new DataColumn(schema.DataFields[1], new string[] { "a", "b" }));
        }

        // Row group 2
        using (ParquetRowGroupWriter rowGroup2 = writer.CreateRowGroup())
        {
            await rowGroup2.WriteColumnAsync(new DataColumn(schema.DataFields[0], new int[] { 3, 4, 5 }));
            await rowGroup2.WriteColumnAsync(new DataColumn(schema.DataFields[1], new string[] { "c", "d", "e" }));
        }

        return path;
    }

    // ───────────────────── Schema tests ─────────────────────

    [Fact]
    public async Task GetSchema_InfersColumnsFromParquetSchema()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.Equal("id", schema.Columns[0].Name);
        Assert.Equal("name", schema.Columns[1].Name);
        Assert.Equal("score", schema.Columns[2].Name);
    }

    [Fact]
    public async Task GetSchema_MapsIntToScalar()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Equal(DataKind.Int32, schema.Columns[0].Kind);
    }

    [Fact]
    public async Task GetSchema_MapsDoubleToScalar()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Equal(DataKind.Float64, schema.Columns[2].Kind);
    }

    [Fact]
    public async Task GetSchema_MapsStringToString()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Equal(DataKind.String, schema.Columns[1].Kind);
    }

    [Fact]
    public async Task GetSchema_MapsFloatToScalar()
    {
        string path = await CreateFloatFixtureAsync();
        ParquetTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Equal(DataKind.Float32, schema.Columns[0].Kind);
    }

    [Fact]
    public async Task GetSchema_MapsByteArrayToUInt8Array()
    {
        string path = await CreateBinaryFixtureAsync();
        ParquetTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        ColumnInfo dataColumn = schema.Columns.Single(c => c.Name == "data");
        Assert.Equal(DataKind.UInt8Array, dataColumn.Kind);
    }

    [Fact]
    public async Task GetSchema_MapsLongToScalar()
    {
        string path = await CreateLongFixtureAsync();
        ParquetTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Equal(DataKind.Int64, schema.Columns[0].Kind);
    }

    [Fact]
    public async Task GetSchema_MapsBoolToBoolean()
    {
        string path = await CreateBooleanFixtureAsync();
        ParquetTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        ColumnInfo activeColumn = schema.Columns.Single(c => c.Name == "active");
        Assert.Equal(DataKind.Boolean, activeColumn.Kind);
    }

    [Fact]
    public async Task GetSchema_NullableColumnMarkedNullable()
    {
        string path = await CreateNullableFixtureAsync();
        ParquetTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.True(schema.Columns[0].Nullable);
    }

    // ───────────────────── Row reading tests ─────────────────────

    [Fact]
    public async Task Open_ReadsAllRows()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task Open_ReadsIntValuesAsScalar()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(1, rows[0]["id"].AsInt32());
        Assert.Equal(2, rows[1]["id"].AsInt32());
        Assert.Equal(3, rows[2]["id"].AsInt32());
    }

    [Fact]
    public async Task Open_ReadsDoubleValuesAsScalar()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(95.5, rows[0]["score"].AsFloat64());
        Assert.Equal(87.3, rows[1]["score"].AsFloat64(), 0.05);
    }

    [Fact]
    public async Task Open_ReadsStringValues()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal("Alice", rows[0]["name"].AsString());
        Assert.Equal("Bob", rows[1]["name"].AsString());
        Assert.Equal("Charlie", rows[2]["name"].AsString());
    }

    [Fact]
    public async Task Open_ReadsFloatValues()
    {
        string path = await CreateFloatFixtureAsync();
        ParquetTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(1.5f, rows[0]["value"].AsFloat32());
        Assert.Equal(0.9f, rows[1]["weight"].AsFloat32());
    }

    [Fact]
    public async Task Open_ReadsNullableValues()
    {
        string path = await CreateNullableFixtureAsync();
        ParquetTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(10, rows[0]["value"].AsInt32());
        Assert.True(rows[1]["value"].IsNull);
        Assert.Equal(30, rows[2]["value"].AsInt32());
    }

    [Fact]
    public async Task Open_ReadsBinaryValues()
    {
        string path = await CreateBinaryFixtureAsync();
        ParquetTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(2, rows.Count);
        byte[] firstData = rows[0]["data"].AsUInt8Array();
        Assert.Equal(new byte[] { 1, 2, 3 }, firstData);

        byte[] secondData = rows[1]["data"].AsUInt8Array();
        Assert.Equal(new byte[] { 4, 5 }, secondData);
    }

    [Fact]
    public async Task Open_ReadsBooleanAsBoolean()
    {
        string path = await CreateBooleanFixtureAsync();
        ParquetTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.True(rows[0]["active"].AsBoolean());
        Assert.False(rows[1]["active"].AsBoolean());
    }

    [Fact]
    public async Task Open_ReadsLongAsScalar()
    {
        string path = await CreateLongFixtureAsync();
        ParquetTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(100000L, rows[0]["big_number"].AsInt64());
    }

    [Fact]
    public async Task Open_ReadsMultipleRowGroups()
    {
        string path = await CreateMultiRowGroupFixtureAsync();
        ParquetTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(5, rows.Count);
        Assert.Equal(1, rows[0]["id"].AsInt32());
        Assert.Equal("e", rows[4]["value"].AsString());
    }

    [Fact]
    public async Task Open_ProjectionPushdown_OnlyReturnsRequestedColumns()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();
        HashSet<string> requested = new(["name"]);
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), requested, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal("Alice", rows[0]["name"].AsString());
        Assert.Throws<KeyNotFoundException>(() => rows[0]["id"]);
    }

    // ───────────────────── Capabilities ─────────────────────

    [Fact]
    public async Task GetCapabilities_ReturnsRowCount()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();
        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            Descriptor(path), CancellationToken.None);

        Assert.Equal(3L, capabilities.EstimatedRowCount);
    }

    [Fact]
    public async Task GetCapabilities_MultiRowGroupSumsRows()
    {
        string path = await CreateMultiRowGroupFixtureAsync();
        ParquetTableProvider provider = new();
        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            Descriptor(path), CancellationToken.None);

        Assert.Equal(5L, capabilities.EstimatedRowCount);
    }

    // ───────────────────── Statistics-based row group pruning ─────────────────────

    /// <summary>
    /// Creates a Parquet file with two row groups having non-overlapping id ranges:
    /// group 1 has ids [1, 2, 3, 4, 5], group 2 has ids [6, 7, 8, 9, 10].
    /// </summary>
    private async Task<string> CreatePruningFixtureAsync()
    {
        string path = FixturePath("pruning.parquet");

        ParquetSchema schema = new(
            new DataField<int>("id"),
            new DataField<string>("name"));

        using FileStream stream = File.Create(path);
        using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream);
        writer.CompressionMethod = CompressionMethod.None;

        // Row group 1: ids 1-5
        using (ParquetRowGroupWriter rowGroup1 = writer.CreateRowGroup())
        {
            await rowGroup1.WriteColumnAsync(new DataColumn(schema.DataFields[0], new int[] { 1, 2, 3, 4, 5 }));
            await rowGroup1.WriteColumnAsync(new DataColumn(schema.DataFields[1], new string[] { "a", "b", "c", "d", "e" }));
        }

        // Row group 2: ids 6-10
        using (ParquetRowGroupWriter rowGroup2 = writer.CreateRowGroup())
        {
            await rowGroup2.WriteColumnAsync(new DataColumn(schema.DataFields[0], new int[] { 6, 7, 8, 9, 10 }));
            await rowGroup2.WriteColumnAsync(new DataColumn(schema.DataFields[1], new string[] { "f", "g", "h", "i", "j" }));
        }

        return path;
    }

    [Fact]
    public async Task OpenWithFilter_GreaterThan7_SkipsFirstRowGroup()
    {
        string path = await CreatePruningFixtureAsync();
        ParquetTableProvider provider = new();
        IFilterableTableProvider filterable = provider;

        // id > 7 → group 1 [1-5] has max 5 ≤ 7, should be pruned.
        // Filter is advisory: all rows from non-pruned group 2 are returned.
        Expression filter = new BinaryExpression(
            new ColumnReference("id"), BinaryOperator.GreaterThan, new LiteralExpression(7.0));

        List<Row> rows = await ReadAllAsync(
            filterable.OpenAsync(Descriptor(path), null, filter, CancellationToken.None));

        Assert.Equal(5, rows.Count); // All rows from group 2
        Assert.Equal(6, rows[0]["id"].AsInt32());
        Assert.Equal(10, rows[4]["id"].AsInt32());
        Assert.Equal(2, provider.TotalRowGroups);
        Assert.Equal(1, provider.PrunedRowGroups);
    }

    [Fact]
    public async Task OpenWithFilter_Equal3_SkipsSecondRowGroup()
    {
        string path = await CreatePruningFixtureAsync();
        ParquetTableProvider provider = new();
        IFilterableTableProvider filterable = provider;

        // id = 3 → group 2 [6-10] has min 6 > 3, should be pruned
        Expression filter = new BinaryExpression(
            new ColumnReference("id"), BinaryOperator.Equal, new LiteralExpression(3.0));

        List<Row> rows = await ReadAllAsync(
            filterable.OpenAsync(Descriptor(path), null, filter, CancellationToken.None));

        // Group 1 is read (contains rows 1-5), but filter is advisory so all 5 rows returned
        Assert.Equal(5, rows.Count);
        Assert.Equal(2, provider.TotalRowGroups);
        Assert.Equal(1, provider.PrunedRowGroups);
    }

    [Fact]
    public async Task OpenWithFilter_LessThan3_SkipsSecondRowGroup()
    {
        string path = await CreatePruningFixtureAsync();
        ParquetTableProvider provider = new();
        IFilterableTableProvider filterable = provider;

        // id < 3 → group 2 [6-10] has min 6 ≥ 3, should be pruned
        Expression filter = new BinaryExpression(
            new ColumnReference("id"), BinaryOperator.LessThan, new LiteralExpression(3.0));

        List<Row> rows = await ReadAllAsync(
            filterable.OpenAsync(Descriptor(path), null, filter, CancellationToken.None));

        // Only group 1 read (advisory filter, all 5 rows returned)
        Assert.Equal(5, rows.Count);
        Assert.Equal(2, provider.TotalRowGroups);
        Assert.Equal(1, provider.PrunedRowGroups);
    }

    [Fact]
    public async Task OpenWithFilter_MatchesBothGroups_NoPruning()
    {
        string path = await CreatePruningFixtureAsync();
        ParquetTableProvider provider = new();
        IFilterableTableProvider filterable = provider;

        // id > 3 → group 1 max=5 > 3 (cannot skip), group 2 max=10 > 3 (cannot skip)
        Expression filter = new BinaryExpression(
            new ColumnReference("id"), BinaryOperator.GreaterThan, new LiteralExpression(3.0));

        List<Row> rows = await ReadAllAsync(
            filterable.OpenAsync(Descriptor(path), null, filter, CancellationToken.None));

        Assert.Equal(10, rows.Count);
        Assert.Equal(2, provider.TotalRowGroups);
        Assert.Equal(0, provider.PrunedRowGroups);
    }

    [Fact]
    public async Task OpenWithFilter_UnsupportedPredicate_NoPruning()
    {
        string path = await CreatePruningFixtureAsync();
        ParquetTableProvider provider = new();
        IFilterableTableProvider filterable = provider;

        // LIKE is unsupported → no pruning
        Expression filter = new BinaryExpression(
            new ColumnReference("name"), BinaryOperator.Like, new LiteralExpression("%test%"));

        List<Row> rows = await ReadAllAsync(
            filterable.OpenAsync(Descriptor(path), null, filter, CancellationToken.None));

        Assert.Equal(10, rows.Count);
        Assert.Equal(2, provider.TotalRowGroups);
        Assert.Equal(0, provider.PrunedRowGroups);
    }

    [Fact]
    public async Task OpenWithFilter_WithProjectionPushdown_PrunesCorrectly()
    {
        string path = await CreatePruningFixtureAsync();
        ParquetTableProvider provider = new();
        IFilterableTableProvider filterable = provider;

        // id > 7 with projection to name only.
        // Filter is advisory: all rows from non-pruned group 2 are returned.
        Expression filter = new BinaryExpression(
            new ColumnReference("id"), BinaryOperator.GreaterThan, new LiteralExpression(7.0));

        HashSet<string> requiredColumns = new(["name"]);
        List<Row> rows = await ReadAllAsync(
            filterable.OpenAsync(Descriptor(path), requiredColumns, filter, CancellationToken.None));

        Assert.Equal(5, rows.Count); // All rows from group 2
        Assert.Equal("f", rows[0]["name"].AsString());
        Assert.Equal(1, provider.PrunedRowGroups);
    }

    [Fact]
    public async Task OpenWithoutFilter_ReadsAllRowGroups()
    {
        string path = await CreatePruningFixtureAsync();
        ParquetTableProvider provider = new();

        // Using the non-filter OpenAsync — should read all groups
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(10, rows.Count);
        // TotalRowGroups/PrunedRowGroups are not set by the non-filter path
        Assert.Equal(0, provider.TotalRowGroups);
        Assert.Equal(0, provider.PrunedRowGroups);
    }

    // ───────────────────── Seekable row range access ─────────────────────

    [Fact]
    public async Task ReadRowRange_ReadsExactRange()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();

        // 3 rows total: read rows 1..2 (Bob, Charlie).
        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 1, count: 2, CancellationToken.None));

        Assert.Equal(2, rows.Count);
        Assert.Equal("Bob", rows[0]["name"].AsString());
        Assert.Equal("Charlie", rows[1]["name"].AsString());
    }

    [Fact]
    public async Task ReadRowRange_ClampsToAvailableRows()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();

        // Only 3 rows; asking for 10 starting at row 1 should return 2.
        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 1, count: 10, CancellationToken.None));

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task ReadRowRange_StartBeyondEnd_ReturnsEmpty()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 100, count: 5, CancellationToken.None));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ReadRowRange_SingleRow()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 0, count: 1, CancellationToken.None));

        Assert.Single(rows);
        Assert.Equal(1, rows[0]["id"].AsInt32());
        Assert.Equal("Alice", rows[0]["name"].AsString());
    }

    [Fact]
    public async Task ReadRowRange_AcrossMultipleRowGroups()
    {
        // Multi-row-group fixture: group 1 has [1,2], group 2 has [3,4,5].
        // Read rows 1..3 (crosses the row group boundary).
        string path = await CreateMultiRowGroupFixtureAsync();
        ParquetTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 1, count: 3, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows[0]["id"].AsInt32());   // Last row of group 1
        Assert.Equal("b", rows[0]["value"].AsString());
        Assert.Equal(3, rows[1]["id"].AsInt32());   // First row of group 2
        Assert.Equal("c", rows[1]["value"].AsString());
        Assert.Equal(4, rows[2]["id"].AsInt32());   // Second row of group 2
        Assert.Equal("d", rows[2]["value"].AsString());
    }

    [Fact]
    public async Task ReadRowRange_SkipsLeadingRowGroup()
    {
        // Multi-row-group fixture: group 1 has [1,2] (2 rows), group 2 has [3,4,5] (3 rows).
        // Read rows 3..4 — entirely in group 2, group 1 should be skipped.
        string path = await CreateMultiRowGroupFixtureAsync();
        ParquetTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 3, count: 2, CancellationToken.None));

        Assert.Equal(2, rows.Count);
        Assert.Equal(4, rows[0]["id"].AsInt32());
        Assert.Equal(5, rows[1]["id"].AsInt32());
    }

    [Fact]
    public async Task ReadRowRange_WithProjection()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();
        HashSet<string> requiredColumns = new(["name"]);

        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), requiredColumns, startRow: 0, count: 2, CancellationToken.None));

        Assert.Equal(2, rows.Count);
        Assert.Equal("Alice", rows[0]["name"].AsString());
        Assert.Throws<KeyNotFoundException>(() => rows[0]["id"]);
    }

    [Fact]
    public async Task GetCapabilities_ReportsSupportsSeek()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();
        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            Descriptor(path), CancellationToken.None);

        Assert.True(capabilities.SupportsSeek);
    }
}

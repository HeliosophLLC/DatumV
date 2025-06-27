using Axon.QueryEngine.Catalog;
using Axon.QueryEngine.Catalog.Providers;
using Axon.QueryEngine.Model;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace Axon.QueryEngine.Tests.Catalog;

/// <summary>
/// Tests for <see cref="ParquetTableProvider"/> using Parquet fixture files
/// created programmatically via Parquet.Net.
/// </summary>
public sealed class ParquetTableProviderTests : IDisposable
{
    private readonly string _fixtureDirectory;

    public ParquetTableProviderTests()
    {
        _fixtureDirectory = Path.Combine(Path.GetTempPath(), "axon_parquet_tests_" + Guid.NewGuid().ToString("N")[..8]);
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

        Assert.Equal(DataKind.Scalar, schema.Columns[0].Kind);
    }

    [Fact]
    public async Task GetSchema_MapsDoubleToScalar()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Equal(DataKind.Scalar, schema.Columns[2].Kind);
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

        Assert.Equal(DataKind.Scalar, schema.Columns[0].Kind);
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

        Assert.Equal(DataKind.Scalar, schema.Columns[0].Kind);
    }

    [Fact]
    public async Task GetSchema_MapsBoolToString()
    {
        string path = await CreateBooleanFixtureAsync();
        ParquetTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        ColumnInfo activeColumn = schema.Columns.Single(c => c.Name == "active");
        // Booleans mapped to String ("True"/"False") since DataKind has no Boolean type
        Assert.Equal(DataKind.String, activeColumn.Kind);
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

        Assert.Equal(1.0f, rows[0]["id"].AsScalar());
        Assert.Equal(2.0f, rows[1]["id"].AsScalar());
        Assert.Equal(3.0f, rows[2]["id"].AsScalar());
    }

    [Fact]
    public async Task Open_ReadsDoubleValuesAsScalar()
    {
        string path = await CreateSimpleFixtureAsync();
        ParquetTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(95.5f, rows[0]["score"].AsScalar());
        Assert.Equal(87.3f, rows[1]["score"].AsScalar(), 0.05f);
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
        Assert.Equal(1.5f, rows[0]["value"].AsScalar());
        Assert.Equal(0.9f, rows[1]["weight"].AsScalar());
    }

    [Fact]
    public async Task Open_ReadsNullableValues()
    {
        string path = await CreateNullableFixtureAsync();
        ParquetTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(10.0f, rows[0]["value"].AsScalar());
        Assert.True(rows[1]["value"].IsNull);
        Assert.Equal(30.0f, rows[2]["value"].AsScalar());
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
    public async Task Open_ReadsBooleanAsString()
    {
        string path = await CreateBooleanFixtureAsync();
        ParquetTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal("True", rows[0]["active"].AsString());
        Assert.Equal("False", rows[1]["active"].AsString());
    }

    [Fact]
    public async Task Open_ReadsLongAsScalar()
    {
        string path = await CreateLongFixtureAsync();
        ParquetTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(100000f, rows[0]["big_number"].AsScalar());
    }

    [Fact]
    public async Task Open_ReadsMultipleRowGroups()
    {
        string path = await CreateMultiRowGroupFixtureAsync();
        ParquetTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(5, rows.Count);
        Assert.Equal(1.0f, rows[0]["id"].AsScalar());
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
}

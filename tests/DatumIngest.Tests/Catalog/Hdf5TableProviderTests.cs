using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;
using PureHDF;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <see cref="Hdf5TableProvider"/> using HDF5 fixture files
/// created programmatically via PureHDF.
/// </summary>
public sealed class Hdf5TableProviderTests : IDisposable
{
    private readonly string _fixtureDirectory;

    public Hdf5TableProviderTests()
    {
        _fixtureDirectory = Path.Combine(Path.GetTempPath(), "datum_hdf5_tests_" + Guid.NewGuid().ToString("N")[..8]);
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
        return new TableDescriptor("hdf5", "test", filePath, options ?? new Dictionary<string, string>());
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

    /// <summary>
    /// Creates an HDF5 file with 10 rows for seek testing:
    /// "id" (int32 0..9), "value" (float64), "label" (string).
    /// </summary>
    private string CreateSeekFixture()
    {
        string path = FixturePath("seek.h5");
        int[] identifiers = new int[10];
        double[] values = new double[10];
        string[] labels = new string[10];
        for (int index = 0; index < 10; index++)
        {
            identifiers[index] = index;
            values[index] = index * 1.5;
            labels[index] = $"row{index}";
        }

        H5File file = new()
        {
            ["id"] = identifiers,
            ["value"] = values,
            ["label"] = labels,
        };
        file.Write(path);
        return path;
    }

    // ───────────────────── Fixture creation helpers ─────────────────────

    /// <summary>
    /// Creates an HDF5 file with three 1-D datasets:
    /// "id" (int32), "score" (float64), "name" (string), each with 3 elements.
    /// </summary>
    private string CreateSimpleFixture()
    {
        string path = FixturePath("simple.h5");
        H5File file = new()
        {
            ["id"] = new int[] { 1, 2, 3 },
            ["score"] = new double[] { 95.5, 87.3, 91.0 },
            ["name"] = new string[] { "Alice", "Bob", "Charlie" },
        };
        file.Write(path);
        return path;
    }

    /// <summary>
    /// Creates an HDF5 file with a single float32 dataset.
    /// </summary>
    private string CreateFloat32Fixture()
    {
        string path = FixturePath("float32.h5");
        H5File file = new()
        {
            ["values"] = new float[] { 1.5f, 2.5f, 3.5f, 4.5f },
        };
        file.Write(path);
        return path;
    }

    /// <summary>
    /// Creates an HDF5 file with datasets nested inside a group.
    /// </summary>
    private string CreateGroupedFixture()
    {
        string path = FixturePath("grouped.h5");
        H5File file = new()
        {
            ["sensors"] = new H5Group()
            {
                ["temperature"] = new float[] { 20.1f, 21.5f, 19.8f },
                ["humidity"] = new float[] { 45.0f, 50.2f, 55.1f },
            },
        };
        file.Write(path);
        return path;
    }

    /// <summary>
    /// Creates an HDF5 file with a 2-D float32 dataset (matrix).
    /// Shape: [3, 4].
    /// </summary>
    private string Create2DFixture()
    {
        string path = FixturePath("matrix.h5");
        float[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        H5File file = new()
        {
            ["matrix"] = new H5Dataset(data, fileDims: [3, 4]),
        };
        file.Write(path);
        return path;
    }

    /// <summary>
    /// Creates an HDF5 file with no datasets (empty).
    /// </summary>
    private string CreateEmptyFixture()
    {
        string path = FixturePath("empty.h5");
        H5File file = new();
        file.Write(path);
        return path;
    }

    /// <summary>
    /// Creates an HDF5 file with a single byte/uint8 dataset.
    /// </summary>
    private string CreateUInt8Fixture()
    {
        string path = FixturePath("uint8.h5");
        H5File file = new()
        {
            ["bytes"] = new byte[] { 10, 20, 30, 40, 50 },
        };
        file.Write(path);
        return path;
    }

    // ───────────────────── Schema tests ─────────────────────

    [Fact]
    public async Task GetSchema_InfersColumnsFromDatasets()
    {
        string path = CreateSimpleFixture();
        Hdf5TableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);

        ColumnInfo idColumn = schema.Columns.Single(c => c.Name == "id");
        ColumnInfo scoreColumn = schema.Columns.Single(c => c.Name == "score");
        ColumnInfo nameColumn = schema.Columns.Single(c => c.Name == "name");

        Assert.Equal(DataKind.Scalar, idColumn.Kind);
        Assert.Equal(DataKind.Scalar, scoreColumn.Kind);
        Assert.Equal(DataKind.String, nameColumn.Kind);
    }

    [Fact]
    public async Task GetSchema_Float32DetectedAsScalar()
    {
        string path = CreateFloat32Fixture();
        Hdf5TableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Single(schema.Columns);
        Assert.Equal("values", schema.Columns[0].Name);
        Assert.Equal(DataKind.Scalar, schema.Columns[0].Kind);
    }

    [Fact]
    public async Task GetSchema_UInt8DetectedAsUInt8()
    {
        string path = CreateUInt8Fixture();
        Hdf5TableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Single(schema.Columns);
        Assert.Equal("bytes", schema.Columns[0].Name);
        Assert.Equal(DataKind.UInt8, schema.Columns[0].Kind);
    }

    [Fact]
    public async Task GetSchema_GroupedDatasetsUseFlattenedNames()
    {
        string path = CreateGroupedFixture();
        Hdf5TableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Contains(schema.Columns, c => c.Name == "sensors/temperature");
        Assert.Contains(schema.Columns, c => c.Name == "sensors/humidity");
    }

    [Fact]
    public async Task GetSchema_EmptyFileThrows()
    {
        string path = CreateEmptyFixture();
        Hdf5TableProvider provider = new();

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GetSchemaAsync(Descriptor(path), CancellationToken.None));
    }

    [Fact]
    public async Task GetSchema_2DDatasetDetectedAsVector()
    {
        string path = Create2DFixture();
        Hdf5TableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Single(schema.Columns);
        Assert.Equal("matrix", schema.Columns[0].Name);
        Assert.Equal(DataKind.Vector, schema.Columns[0].Kind);
    }

    // ───────────────────── Row reading tests ─────────────────────

    [Fact]
    public async Task Open_ReadsAllRows()
    {
        string path = CreateSimpleFixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task Open_ReadsIntegerValuesAsScalar()
    {
        string path = CreateSimpleFixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(1.0f, rows[0]["id"].AsScalar());
        Assert.Equal(2.0f, rows[1]["id"].AsScalar());
        Assert.Equal(3.0f, rows[2]["id"].AsScalar());
    }

    [Fact]
    public async Task Open_ReadsFloat64ValuesAsScalar()
    {
        string path = CreateSimpleFixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(95.5f, rows[0]["score"].AsScalar());
        Assert.Equal(87.3f, rows[1]["score"].AsScalar(), 0.05f);
        Assert.Equal(91.0f, rows[2]["score"].AsScalar());
    }

    [Fact]
    public async Task Open_ReadsStringValues()
    {
        string path = CreateSimpleFixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal("Alice", rows[0]["name"].AsString());
        Assert.Equal("Bob", rows[1]["name"].AsString());
        Assert.Equal("Charlie", rows[2]["name"].AsString());
    }

    [Fact]
    public async Task Open_ReadsFloat32Values()
    {
        string path = CreateFloat32Fixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(4, rows.Count);
        Assert.Equal(1.5f, rows[0]["values"].AsScalar());
        Assert.Equal(4.5f, rows[3]["values"].AsScalar());
    }

    [Fact]
    public async Task Open_ReadsUInt8Values()
    {
        string path = CreateUInt8Fixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(5, rows.Count);
        Assert.Equal((byte)10, rows[0]["bytes"].AsUInt8());
        Assert.Equal((byte)50, rows[4]["bytes"].AsUInt8());
    }

    [Fact]
    public async Task Open_Reads2DDatasetAsVectorPerRow()
    {
        string path = Create2DFixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);

        float[] firstRow = rows[0]["matrix"].AsVector();
        Assert.Equal(4, firstRow.Length);
        Assert.Equal(1.0f, firstRow[0]);
        Assert.Equal(4.0f, firstRow[3]);

        float[] lastRow = rows[2]["matrix"].AsVector();
        Assert.Equal(9.0f, lastRow[0]);
        Assert.Equal(12.0f, lastRow[3]);
    }

    [Fact]
    public async Task Open_ProjectionPushdown_OnlyReturnsRequestedColumns()
    {
        string path = CreateSimpleFixture();
        Hdf5TableProvider provider = new();
        HashSet<string> requested = new(["name"]);
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), requested, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal("Alice", rows[0]["name"].AsString());

        Assert.Throws<KeyNotFoundException>(() => rows[0]["id"]);
    }

    [Fact]
    public async Task Open_EmptyFileYieldsNoRows()
    {
        string path = CreateEmptyFixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Open_GroupedDatasetsReadCorrectly()
    {
        string path = CreateGroupedFixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(20.1f, rows[0]["sensors/temperature"].AsScalar(), 0.05f);
        Assert.Equal(45.0f, rows[0]["sensors/humidity"].AsScalar());
    }

    // ───────────────────── Capabilities ─────────────────────

    [Fact]
    public async Task GetCapabilities_ReturnsRowCount()
    {
        string path = CreateSimpleFixture();
        Hdf5TableProvider provider = new();
        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            Descriptor(path), CancellationToken.None);

        Assert.Equal(3L, capabilities.EstimatedRowCount);
    }

    // ───────────────────── Seekable read tests ─────────────────────

    [Fact]
    public async Task GetCapabilities_ReportsSeekSupport()
    {
        string path = CreateSimpleFixture();
        Hdf5TableProvider provider = new();
        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            Descriptor(path), CancellationToken.None);

        Assert.True(capabilities.SupportsSeek);
    }

    [Fact]
    public async Task ReadRowRange_ReadsMiddleSlice()
    {
        string path = CreateSeekFixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 3, count: 4, CancellationToken.None));

        Assert.Equal(4, rows.Count);
        Assert.Equal(3.0f, rows[0]["id"].AsScalar());
        Assert.Equal(4.0f, rows[1]["id"].AsScalar());
        Assert.Equal(5.0f, rows[2]["id"].AsScalar());
        Assert.Equal(6.0f, rows[3]["id"].AsScalar());
    }

    [Fact]
    public async Task ReadRowRange_ReadsFirstRows()
    {
        string path = CreateSeekFixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 0, count: 2, CancellationToken.None));

        Assert.Equal(2, rows.Count);
        Assert.Equal(0.0f, rows[0]["id"].AsScalar());
        Assert.Equal(1.0f, rows[1]["id"].AsScalar());
    }

    [Fact]
    public async Task ReadRowRange_ReadsLastRows()
    {
        string path = CreateSeekFixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 8, count: 5, CancellationToken.None));

        // Only 2 rows remain (indices 8 and 9), so clamped.
        Assert.Equal(2, rows.Count);
        Assert.Equal(8.0f, rows[0]["id"].AsScalar());
        Assert.Equal(9.0f, rows[1]["id"].AsScalar());
    }

    [Fact]
    public async Task ReadRowRange_StartBeyondEnd_ReturnsEmpty()
    {
        string path = CreateSeekFixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 100, count: 5, CancellationToken.None));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ReadRowRange_RespectsProjectionPushdown()
    {
        string path = CreateSeekFixture();
        Hdf5TableProvider provider = new();
        HashSet<string> requested = new(["label"]);
        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), requested, startRow: 2, count: 3, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal("row2", rows[0]["label"].AsString());
        Assert.Equal("row3", rows[1]["label"].AsString());
        Assert.Equal("row4", rows[2]["label"].AsString());

        Assert.Throws<KeyNotFoundException>(() => rows[0]["id"]);
    }

    [Fact]
    public async Task ReadRowRange_Float64ValuesSlicedCorrectly()
    {
        string path = CreateSeekFixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 5, count: 2, CancellationToken.None));

        Assert.Equal(2, rows.Count);
        Assert.Equal(7.5f, rows[0]["value"].AsScalar());
        Assert.Equal(9.0f, rows[1]["value"].AsScalar());
    }

    [Fact]
    public async Task ReadRowRange_2DDataset_SlicesVectors()
    {
        string path = Create2DFixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 1, count: 2, CancellationToken.None));

        Assert.Equal(2, rows.Count);

        // Row 1 of [3,4] matrix: [5, 6, 7, 8]
        float[] firstVector = rows[0]["matrix"].AsVector();
        Assert.Equal(4, firstVector.Length);
        Assert.Equal(5.0f, firstVector[0]);
        Assert.Equal(8.0f, firstVector[3]);

        // Row 2 of [3,4] matrix: [9, 10, 11, 12]
        float[] secondVector = rows[1]["matrix"].AsVector();
        Assert.Equal(9.0f, secondVector[0]);
        Assert.Equal(12.0f, secondVector[3]);
    }

    [Fact]
    public async Task ReadRowRange_UInt8Dataset_SlicedCorrectly()
    {
        string path = CreateUInt8Fixture();
        Hdf5TableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 1, count: 3, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal((byte)20, rows[0]["bytes"].AsUInt8());
        Assert.Equal((byte)30, rows[1]["bytes"].AsUInt8());
        Assert.Equal((byte)40, rows[2]["bytes"].AsUInt8());
    }
}

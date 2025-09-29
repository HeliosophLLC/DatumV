namespace DatumIngest.Tests.Catalog;

using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Output.Writers;
using DatumIngest.Parsing.Ast;

/// <summary>
/// Integration tests for <see cref="DatumFileTableProvider"/>.
/// Covers full write-then-query round-trips, projection pushdown,
/// and zone-map-based row group pruning.
/// </summary>
public sealed class DatumFileTableProviderTests : IAsyncLifetime
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"datum_provider_{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDirectory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ──────────────────── Basic read ────────────────────

    [Fact]
    public async Task OpenAsync_ReturnsAllRowsAndColumns()
    {
        string path = await WriteFixture("basic.datum", [
            MultiRow(("id", DataValue.FromFloat32(1f)), ("name", DataValue.FromString("Alice"))),
            MultiRow(("id", DataValue.FromFloat32(2f)), ("name", DataValue.FromString("Bob"))),
            MultiRow(("id", DataValue.FromFloat32(3f)), ("name", DataValue.FromString("Charlie"))),
        ]);

        List<Row> rows = await ReadAll(path);

        Assert.Equal(3, rows.Count);
        Assert.Equal(1f, rows[0]["id"].AsFloat32(), 0.0001f);
        Assert.Equal("Alice", rows[0]["name"].AsString());
        Assert.Equal(3f, rows[2]["id"].AsFloat32(), 0.0001f);
        Assert.Equal("Charlie", rows[2]["name"].AsString());
    }

    [Fact]
    public async Task OpenAsync_EmptyFile_ReturnsNoRows()
    {
        string path = await WriteEmptyFixture("empty.datum",
            [new ColumnInfo("x", DataKind.Float32, false)]);

        List<Row> rows = await ReadAll(path);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetSchemaAsync_ReturnsCorrectColumnNames()
    {
        string path = await WriteFixture("schema.datum", [
            MultiRow(("score", DataValue.FromFloat32(0.9f)), ("label", DataValue.FromString("cat")))
        ]);

        DatumFileTableProvider provider = new();
        TableDescriptor descriptor = Descriptor(path);
        Schema schema = await provider.GetSchemaAsync(descriptor, CancellationToken.None);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("score", schema.Columns[0].Name);
        Assert.Equal("label", schema.Columns[1].Name);
    }

    // ──────────────────── Projection pushdown ────────────────────

    [Fact]
    public async Task OpenAsync_WithRequiredColumns_ProjectsSubset()
    {
        string path = await WriteFixture("projection.datum", [
            MultiRow(("a", DataValue.FromFloat32(1f)), ("b", DataValue.FromString("x")), ("c", DataValue.FromBoolean(true))),
        ]);

        DatumFileTableProvider provider = new();
        TableDescriptor descriptor = Descriptor(path);
        HashSet<string> required = ["a", "c"];

        List<Row> rows = await ReadAll(provider, descriptor, required);

        Assert.Single(rows);
        // Only projected columns are present.
        Assert.Equal(1f, rows[0]["a"].AsFloat32(), 0.0001f);
        Assert.True(rows[0]["c"].AsBoolean());
        Assert.False(rows[0].TryGetValue("b", out _));
    }

    // ──────────────────── Multi-row-group ────────────────────

    [Fact]
    public async Task OpenAsync_MultipleRowGroups_ReturnsAllRowsInOrder()
    {
        // Force two row groups by using row group size of 3.
        string path = Path.Combine(_tempDirectory, "twogroups.datum");
        Schema schema = new([new ColumnInfo("n", DataKind.Float32, false)]);

        using (DatumFileWriter fileWriter = new(path))
        {
            fileWriter.SetRowGroupSize(3);
            fileWriter.Initialize(DatumFileSchema.FromSchema(schema));
            for (int i = 0; i < 5; i++)
            {
                fileWriter.WriteRow(new Row(["n"], [DataValue.FromFloat32((float)i)]));
            }

            fileWriter.Finalize();
        }

        using DatumFileReader reader = DatumFileReader.Open(path);
        Assert.Equal(2, reader.RowGroupCount);
        Assert.Equal(5, reader.TotalRowCount);

        // Read all via provider to confirm ordered delivery.
        List<Row> rows = await ReadAll(path);
        Assert.Equal(5, rows.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal((float)i, rows[i]["n"].AsFloat32(), 0.0001f);
        }
    }

    // ──────────────────── Zone map pruning ────────────────────

    /// <summary>
    /// Writes two row groups with non-overlapping scalar ranges (0-9, 100-109).
    /// A predicate <c>score &gt; 50</c> should prune the first group entirely.
    /// </summary>
    [Fact]
    public async Task ZoneMap_NonOverlappingGroups_PrunesCorrectGroup()
    {
        string path = Path.Combine(_tempDirectory, "zonemap.datum");
        Schema schema = new([new ColumnInfo("score", DataKind.Float32, false)]);

        using (DatumFileWriter fileWriter = new(path))
        {
            fileWriter.SetRowGroupSize(10);
            fileWriter.Initialize(DatumFileSchema.FromSchema(schema));

            // Row group 0: 0..9
            for (int i = 0; i < 10; i++)
            {
                fileWriter.WriteRow(new Row(["score"], [DataValue.FromFloat32((float)i)]));
            }

            // Row group 1: 100..109
            for (int i = 100; i < 110; i++)
            {
                fileWriter.WriteRow(new Row(["score"], [DataValue.FromFloat32((float)i)]));
            }

            fileWriter.Finalize();
        }

        DatumFileTableProvider provider = new();
        TableDescriptor descriptor = Descriptor(path);

        // score > 50 — row group 0 (max=9) can be skipped, row group 1 (min=100) cannot.
        Expression filter = new BinaryExpression(
            new ColumnReference("score"),
            BinaryOperator.GreaterThan,
            new LiteralExpression(50.0));

        List<Row> rows = await ReadAll(provider, descriptor, requiredColumns: null, filter);

        // Provider should prune group 0, so all 10 returned rows come from group 1.
        Assert.Equal(10, rows.Count);
        Assert.All(rows, row => Assert.True(row["score"].AsFloat32() >= 100f));

        Assert.Equal(2, provider.TotalRowGroups);
        Assert.Equal(1, provider.PrunedRowGroups);
    }

    /// <summary>
    /// Predicate that overlaps both row groups must not prune anything.
    /// </summary>
    [Fact]
    public async Task ZoneMap_OverlappingPredicate_PrunesNothing()
    {
        string path = Path.Combine(_tempDirectory, "zonemap_overlap.datum");
        Schema schema = new([new ColumnInfo("score", DataKind.Float32, false)]);

        using (DatumFileWriter fileWriter = new(path))
        {
            fileWriter.SetRowGroupSize(5);
            fileWriter.Initialize(DatumFileSchema.FromSchema(schema));

            for (int i = 0; i < 5; i++)
            {
                fileWriter.WriteRow(new Row(["score"], [DataValue.FromFloat32((float)i)]));
            }

            for (int i = 10; i < 15; i++)
            {
                fileWriter.WriteRow(new Row(["score"], [DataValue.FromFloat32((float)i)]));
            }

            fileWriter.Finalize();
        }

        DatumFileTableProvider provider = new();
        TableDescriptor descriptor = Descriptor(path);

        // score > 3 — row group 0 has max=4 so cannot be skipped; row group 1 has min=10 so also cannot.
        Expression filter = new BinaryExpression(
            new ColumnReference("score"),
            BinaryOperator.GreaterThan,
            new LiteralExpression(3.0));

        await ReadAll(provider, descriptor, requiredColumns: null, filter);

        Assert.Equal(0, provider.PrunedRowGroups);
    }

    // ──────────────────── ReadRowRangeAsync (seeking) ────────────────────

    /// <summary>
    /// Mid-range seek spanning a single row group returns the correct rows.
    /// </summary>
    [Fact]
    public async Task ReadRowRangeAsync_MiddleRange_ReturnsCorrectRows()
    {
        string path = Path.Combine(_tempDirectory, "seek_middle.datum");
        Schema schema = new([new ColumnInfo("n", DataKind.Float32, false)]);

        using (DatumFileWriter fileWriter = new(path))
        {
            fileWriter.SetRowGroupSize(10);
            fileWriter.Initialize(DatumFileSchema.FromSchema(schema));
            for (int i = 0; i < 10; i++)
            {
                fileWriter.WriteRow(new Row(["n"], [DataValue.FromFloat32((float)i)]));
            }

            fileWriter.Finalize();
        }

        DatumFileTableProvider provider = new();
        TableDescriptor descriptor = Descriptor(path);

        List<Row> rows = await ReadRange(provider, descriptor, requiredColumns: null, startRow: 3, count: 4);

        Assert.Equal(4, rows.Count);
        Assert.Equal(3f, rows[0]["n"].AsFloat32(), 0.0001f);
        Assert.Equal(4f, rows[1]["n"].AsFloat32(), 0.0001f);
        Assert.Equal(5f, rows[2]["n"].AsFloat32(), 0.0001f);
        Assert.Equal(6f, rows[3]["n"].AsFloat32(), 0.0001f);
    }

    /// <summary>
    /// Seeks across row group boundaries and returns rows from multiple groups.
    /// </summary>
    [Fact]
    public async Task ReadRowRangeAsync_SpanningMultipleRowGroups_ReturnsCorrectRows()
    {
        string path = Path.Combine(_tempDirectory, "seek_span.datum");
        Schema schema = new([new ColumnInfo("n", DataKind.Float32, false)]);

        using (DatumFileWriter fileWriter = new(path))
        {
            fileWriter.SetRowGroupSize(3);
            fileWriter.Initialize(DatumFileSchema.FromSchema(schema));
            // Row group 0: 0, 1, 2 | Row group 1: 3, 4, 5 | Row group 2: 6, 7
            for (int i = 0; i < 8; i++)
            {
                fileWriter.WriteRow(new Row(["n"], [DataValue.FromFloat32((float)i)]));
            }

            fileWriter.Finalize();
        }

        DatumFileTableProvider provider = new();
        TableDescriptor descriptor = Descriptor(path);

        // Read rows 2-5 (spans row group 0 and 1).
        List<Row> rows = await ReadRange(provider, descriptor, requiredColumns: null, startRow: 2, count: 4);

        Assert.Equal(4, rows.Count);
        Assert.Equal(2f, rows[0]["n"].AsFloat32(), 0.0001f);
        Assert.Equal(3f, rows[1]["n"].AsFloat32(), 0.0001f);
        Assert.Equal(4f, rows[2]["n"].AsFloat32(), 0.0001f);
        Assert.Equal(5f, rows[3]["n"].AsFloat32(), 0.0001f);
    }

    /// <summary>
    /// Seeking with projection returns only the requested columns.
    /// </summary>
    [Fact]
    public async Task ReadRowRangeAsync_WithProjection_ReturnsOnlyRequestedColumns()
    {
        string path = await WriteFixture("seek_projection.datum", [
            MultiRow(("a", DataValue.FromFloat32(1f)), ("b", DataValue.FromString("x")), ("c", DataValue.FromBoolean(true))),
            MultiRow(("a", DataValue.FromFloat32(2f)), ("b", DataValue.FromString("y")), ("c", DataValue.FromBoolean(false))),
            MultiRow(("a", DataValue.FromFloat32(3f)), ("b", DataValue.FromString("z")), ("c", DataValue.FromBoolean(true))),
        ]);

        DatumFileTableProvider provider = new();
        TableDescriptor descriptor = Descriptor(path);
        HashSet<string> required = ["a", "c"];

        List<Row> rows = await ReadRange(provider, descriptor, required, startRow: 1, count: 2);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2f, rows[0]["a"].AsFloat32(), 0.0001f);
        Assert.True(rows[0].TryGetValue("c", out _));
        Assert.False(rows[0].TryGetValue("b", out _));
    }

    /// <summary>
    /// Requesting more rows than available clamps to the file length.
    /// </summary>
    [Fact]
    public async Task ReadRowRangeAsync_BeyondEnd_ClampsToAvailable()
    {
        string path = await WriteFixture("seek_clamp.datum", [
            MultiRow(("n", DataValue.FromFloat32(0f))),
            MultiRow(("n", DataValue.FromFloat32(1f))),
            MultiRow(("n", DataValue.FromFloat32(2f))),
        ]);

        DatumFileTableProvider provider = new();
        TableDescriptor descriptor = Descriptor(path);

        List<Row> rows = await ReadRange(provider, descriptor, requiredColumns: null, startRow: 1, count: 100);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1f, rows[0]["n"].AsFloat32(), 0.0001f);
        Assert.Equal(2f, rows[1]["n"].AsFloat32(), 0.0001f);
    }

    /// <summary>
    /// Start row beyond file length returns empty result.
    /// </summary>
    [Fact]
    public async Task ReadRowRangeAsync_StartBeyondEnd_ReturnsEmpty()
    {
        string path = await WriteFixture("seek_empty.datum", [
            MultiRow(("n", DataValue.FromFloat32(0f))),
        ]);

        DatumFileTableProvider provider = new();
        TableDescriptor descriptor = Descriptor(path);

        List<Row> rows = await ReadRange(provider, descriptor, requiredColumns: null, startRow: 10, count: 5);

        Assert.Empty(rows);
    }

    /// <summary>
    /// Reading all rows via <see cref="ISeekableTableProvider.ReadRowRangeAsync"/>
    /// produces the same result as <see cref="ITableProvider.OpenAsync"/>.
    /// </summary>
    [Fact]
    public async Task ReadRowRangeAsync_AllRows_MatchesOpenAsync()
    {
        string path = Path.Combine(_tempDirectory, "seek_all.datum");
        Schema schema = new([new ColumnInfo("n", DataKind.Float32, false)]);

        using (DatumFileWriter fileWriter = new(path))
        {
            fileWriter.SetRowGroupSize(3);
            fileWriter.Initialize(DatumFileSchema.FromSchema(schema));
            for (int i = 0; i < 7; i++)
            {
                fileWriter.WriteRow(new Row(["n"], [DataValue.FromFloat32((float)i)]));
            }

            fileWriter.Finalize();
        }

        DatumFileTableProvider provider = new();
        TableDescriptor descriptor = Descriptor(path);

        List<Row> allRows = await ReadAll(path);
        List<Row> seekRows = await ReadRange(provider, descriptor, requiredColumns: null, startRow: 0, count: 7);

        Assert.Equal(allRows.Count, seekRows.Count);
        for (int i = 0; i < allRows.Count; i++)
        {
            Assert.Equal(allRows[i]["n"].AsFloat32(), seekRows[i]["n"].AsFloat32(), 0.0001f);
        }
    }

    // ──────────────────── Helpers ────────────────────

    private async Task<string> WriteFixture(string fileName, IEnumerable<Row> rows)
    {
        string path = Path.Combine(_tempDirectory, fileName);
        Row first = rows.First();

        Schema schema = new(first.ColumnNames.Select(n => new ColumnInfo(n, first[n].Kind, nullable: false)).ToArray());

        await using DatumOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        foreach (Row row in rows)
        {
            await writer.WriteRowAsync(row);
        }

        await writer.FinalizeAsync();
        return path;
    }

    private async Task<string> WriteEmptyFixture(string fileName, IReadOnlyList<ColumnInfo> columnInfos)
    {
        string path = Path.Combine(_tempDirectory, fileName);
        await using DatumOutputWriter writer = new(path);
        await writer.InitializeAsync(new Schema(columnInfos));
        await writer.FinalizeAsync();
        return path;
    }

    private static TableDescriptor Descriptor(string filePath)
        => new("datum", "test", filePath, new Dictionary<string, string>());

    private static async Task<List<Row>> ReadAll(string filePath)
    {
        DatumFileTableProvider provider = new();
        return await ReadAll(provider, Descriptor(filePath), requiredColumns: null);
    }

    private static async Task<List<Row>> ReadAll(
        DatumFileTableProvider provider,
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        Expression? filter = null)
    {
        IAsyncEnumerable<RowBatch> source = filter is not null
            ? provider.OpenAsync(descriptor, requiredColumns, filter, CancellationToken.None)
            : provider.OpenAsync(descriptor, requiredColumns, CancellationToken.None);

        return await source.CollectRowsAsync();
    }

    private static async Task<List<Row>> ReadRange(
        DatumFileTableProvider provider,
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        long startRow,
        int count)
    {
        List<Row> rows = await provider.ReadRowRangeAsync(
            descriptor, requiredColumns, startRow, count, CancellationToken.None)
            .CollectRowsAsync();

        // Dispose closes the cached DatumFileReader so the file is not held open
        // when the test teardown deletes the temp directory.
        provider.Dispose();

        return rows;
    }

    private static Row MultiRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = new string[columns.Length];
        DataValue[] values = new DataValue[columns.Length];
        for (int index = 0; index < columns.Length; index++)
        {
            names[index] = columns[index].Name;
            values[index] = columns[index].Value;
        }
        return new Row(names, values);
    }
}

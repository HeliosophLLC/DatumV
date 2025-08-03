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
            MultiRow(("id", DataValue.FromScalar(1f)), ("name", DataValue.FromString("Alice"))),
            MultiRow(("id", DataValue.FromScalar(2f)), ("name", DataValue.FromString("Bob"))),
            MultiRow(("id", DataValue.FromScalar(3f)), ("name", DataValue.FromString("Charlie"))),
        ]);

        List<Row> rows = await ReadAll(path);

        Assert.Equal(3, rows.Count);
        Assert.Equal(1f, rows[0]["id"].AsScalar(), 0.0001f);
        Assert.Equal("Alice", rows[0]["name"].AsString());
        Assert.Equal(3f, rows[2]["id"].AsScalar(), 0.0001f);
        Assert.Equal("Charlie", rows[2]["name"].AsString());
    }

    [Fact]
    public async Task OpenAsync_EmptyFile_ReturnsNoRows()
    {
        string path = await WriteEmptyFixture("empty.datum",
            [new ColumnInfo("x", DataKind.Scalar, false)]);

        List<Row> rows = await ReadAll(path);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetSchemaAsync_ReturnsCorrectColumnNames()
    {
        string path = await WriteFixture("schema.datum", [
            MultiRow(("score", DataValue.FromScalar(0.9f)), ("label", DataValue.FromString("cat")))
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
            MultiRow(("a", DataValue.FromScalar(1f)), ("b", DataValue.FromString("x")), ("c", DataValue.FromBoolean(true))),
        ]);

        DatumFileTableProvider provider = new();
        TableDescriptor descriptor = Descriptor(path);
        HashSet<string> required = ["a", "c"];

        List<Row> rows = await ReadAll(provider, descriptor, required);

        Assert.Single(rows);
        // Only projected columns are present.
        Assert.Equal(1f, rows[0]["a"].AsScalar(), 0.0001f);
        Assert.True(rows[0]["c"].AsBoolean());
        Assert.False(rows[0].TryGetValue("b", out _));
    }

    // ──────────────────── Multi-row-group ────────────────────

    [Fact]
    public async Task OpenAsync_MultipleRowGroups_ReturnsAllRowsInOrder()
    {
        // Force two row groups by using row group size of 3.
        string path = Path.Combine(_tempDirectory, "twogroups.datum");
        Schema schema = new([new ColumnInfo("n", DataKind.Scalar, false)]);

        using (DatumFileWriter fileWriter = new(path))
        {
            fileWriter.SetRowGroupSize(3);
            fileWriter.Initialize(DatumFileSchema.FromSchema(schema));
            for (int i = 0; i < 5; i++)
            {
                fileWriter.WriteRow(new Row(["n"], [DataValue.FromScalar((float)i)]));
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
            Assert.Equal((float)i, rows[i]["n"].AsScalar(), 0.0001f);
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
        Schema schema = new([new ColumnInfo("score", DataKind.Scalar, false)]);

        using (DatumFileWriter fileWriter = new(path))
        {
            fileWriter.SetRowGroupSize(10);
            fileWriter.Initialize(DatumFileSchema.FromSchema(schema));

            // Row group 0: 0..9
            for (int i = 0; i < 10; i++)
            {
                fileWriter.WriteRow(new Row(["score"], [DataValue.FromScalar((float)i)]));
            }

            // Row group 1: 100..109
            for (int i = 100; i < 110; i++)
            {
                fileWriter.WriteRow(new Row(["score"], [DataValue.FromScalar((float)i)]));
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
        Assert.All(rows, row => Assert.True(row["score"].AsScalar() >= 100f));

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
        Schema schema = new([new ColumnInfo("score", DataKind.Scalar, false)]);

        using (DatumFileWriter fileWriter = new(path))
        {
            fileWriter.SetRowGroupSize(5);
            fileWriter.Initialize(DatumFileSchema.FromSchema(schema));

            for (int i = 0; i < 5; i++)
            {
                fileWriter.WriteRow(new Row(["score"], [DataValue.FromScalar((float)i)]));
            }

            for (int i = 10; i < 15; i++)
            {
                fileWriter.WriteRow(new Row(["score"], [DataValue.FromScalar((float)i)]));
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
        List<Row> rows = new();
        IAsyncEnumerable<Row> source = filter is not null
            ? provider.OpenAsync(descriptor, requiredColumns, filter, CancellationToken.None)
            : provider.OpenAsync(descriptor, requiredColumns, CancellationToken.None);

        await foreach (Row row in source)
        {
            rows.Add(row);
        }

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

namespace DatumQuery.Tests.Output;

using DatumQuery.Model;
using DatumQuery.Output;
using DatumQuery.Output.Writers;

public sealed class ShardingOutputWriterTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"shard_writer_{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SampleCount_350Rows_At100_Creates4Shards()
    {
        string basePath = Path.Combine(_tempDir, "output.csv");
        ShardStrategy strategy = new(ShardMode.SampleCount, 100);

        await using ShardingOutputWriter writer = new(
            path => new CsvOutputWriter(path),
            strategy,
            basePath);

        Schema schema = new([new ColumnInfo("id", DataKind.Scalar, false)]);
        await writer.InitializeAsync(schema);

        for (int i = 0; i < 350; i++)
        {
            await writer.WriteRowAsync(CreateRow(("id", DataValue.FromScalar(i))));
        }

        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(350, summary.RowsWritten);
        Assert.Equal(4, summary.FilesCreated.Count);

        // Verify shard naming pattern
        Assert.EndsWith("_shard_00000.csv", summary.FilesCreated[0]);
        Assert.EndsWith("_shard_00001.csv", summary.FilesCreated[1]);
        Assert.EndsWith("_shard_00002.csv", summary.FilesCreated[2]);
        Assert.EndsWith("_shard_00003.csv", summary.FilesCreated[3]);
    }

    [Fact]
    public async Task SampleCount_ExactMultiple_CreatesExactShards()
    {
        string basePath = Path.Combine(_tempDir, "exact.csv");
        ShardStrategy strategy = new(ShardMode.SampleCount, 50);

        await using ShardingOutputWriter writer = new(
            path => new CsvOutputWriter(path),
            strategy,
            basePath);

        Schema schema = new([new ColumnInfo("val", DataKind.Scalar, false)]);
        await writer.InitializeAsync(schema);

        for (int i = 0; i < 100; i++)
        {
            await writer.WriteRowAsync(CreateRow(("val", DataValue.FromScalar(i))));
        }

        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(100, summary.RowsWritten);
        Assert.Equal(2, summary.FilesCreated.Count);
    }

    [Fact]
    public async Task SampleCount_FewerThanThreshold_CreatesSingleShard()
    {
        string basePath = Path.Combine(_tempDir, "small.csv");
        ShardStrategy strategy = new(ShardMode.SampleCount, 100);

        await using ShardingOutputWriter writer = new(
            path => new CsvOutputWriter(path),
            strategy,
            basePath);

        Schema schema = new([new ColumnInfo("val", DataKind.Scalar, false)]);
        await writer.InitializeAsync(schema);

        for (int i = 0; i < 10; i++)
        {
            await writer.WriteRowAsync(CreateRow(("val", DataValue.FromScalar(i))));
        }

        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(10, summary.RowsWritten);
        Assert.Single(summary.FilesCreated);
    }

    [Fact]
    public async Task SampleCount_AllShardFilesExist()
    {
        string basePath = Path.Combine(_tempDir, "exists.csv");
        ShardStrategy strategy = new(ShardMode.SampleCount, 5);

        await using ShardingOutputWriter writer = new(
            path => new CsvOutputWriter(path),
            strategy,
            basePath);

        Schema schema = new([new ColumnInfo("val", DataKind.String, false)]);
        await writer.InitializeAsync(schema);

        for (int i = 0; i < 12; i++)
        {
            await writer.WriteRowAsync(CreateRow(("val", DataValue.FromString($"row_{i}"))));
        }

        OutputSummary summary = await writer.FinalizeAsync();

        foreach (string file in summary.FilesCreated)
        {
            Assert.True(File.Exists(file), $"Shard file should exist: {file}");
        }
    }

    [Fact]
    public async Task SampleCount_ShardContentsAreReadable()
    {
        string basePath = Path.Combine(_tempDir, "readable.csv");
        ShardStrategy strategy = new(ShardMode.SampleCount, 3);

        await using ShardingOutputWriter writer = new(
            path => new CsvOutputWriter(path),
            strategy,
            basePath);

        Schema schema = new([new ColumnInfo("val", DataKind.Scalar, false)]);
        await writer.InitializeAsync(schema);

        for (int i = 1; i <= 7; i++)
        {
            await writer.WriteRowAsync(CreateRow(("val", DataValue.FromScalar(i))));
        }

        OutputSummary summary = await writer.FinalizeAsync();

        // Read first shard and verify it has 3 rows
        DatumQuery.Catalog.Providers.CsvTableProvider provider = new();
        DatumQuery.Catalog.TableDescriptor descriptor = new("csv", "test", summary.FilesCreated[0], new Dictionary<string, string>());

        List<Row> rows = new();
        await foreach (Row row in provider.OpenAsync(descriptor, null, CancellationToken.None))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task SampleCount_Empty_CreatesSingleEmptyShard()
    {
        string basePath = Path.Combine(_tempDir, "empty.csv");
        ShardStrategy strategy = new(ShardMode.SampleCount, 100);

        await using ShardingOutputWriter writer = new(
            path => new CsvOutputWriter(path),
            strategy,
            basePath);

        Schema schema = new([new ColumnInfo("val", DataKind.Scalar, false)]);
        await writer.InitializeAsync(schema);
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(0, summary.RowsWritten);
        Assert.Single(summary.FilesCreated);
    }

    [Fact]
    public async Task TotalBytesWritten_ReportsNonZero()
    {
        string basePath = Path.Combine(_tempDir, "bytes.csv");
        ShardStrategy strategy = new(ShardMode.SampleCount, 5);

        await using ShardingOutputWriter writer = new(
            path => new CsvOutputWriter(path),
            strategy,
            basePath);

        Schema schema = new([new ColumnInfo("data", DataKind.String, false)]);
        await writer.InitializeAsync(schema);

        for (int i = 0; i < 10; i++)
        {
            await writer.WriteRowAsync(CreateRow(("data", DataValue.FromString("hello world"))));
        }

        OutputSummary summary = await writer.FinalizeAsync();

        Assert.True(summary.BytesWritten > 0);
    }

    private static Row CreateRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = new string[columns.Length];
        DataValue[] values = new DataValue[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            names[i] = columns[i].Name;
            values[i] = columns[i].Value;
        }
        return new Row(names, values);
    }
}

using DatumIngest.Catalog;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

public sealed class SourceIndexBuilderTests
{
    [Fact]
    public async Task BuildAsync_EmptyProvider_ProducesEmptyIndex()
    {
        InMemoryTableProvider provider = new([]);
        TableDescriptor descriptor = CreateDescriptor("empty");
        SourceIndexBuilder builder = new(chunkSize: 5);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.Equal(0, index.Schema.TotalRowCount);
        Assert.Empty(index.Chunks);
    }

    [Fact]
    public async Task BuildAsync_SingleChunk_CapturesSchemaAndStats()
    {
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromScalar(1.0f)), ("name", DataValue.FromString("alice"))),
            MakeRow(("id", DataValue.FromScalar(2.0f)), ("name", DataValue.FromString("bob"))),
            MakeRow(("id", DataValue.FromScalar(3.0f)), ("name", DataValue.FromString("charlie"))),
        ];

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("test");
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.Equal(3, index.Schema.TotalRowCount);
        Assert.Equal(2, index.Schema.Schema.Columns.Count);
        Assert.Single(index.Chunks);
        Assert.Equal(0, index.Chunks[0].RowOffset);
        Assert.Equal(3, index.Chunks[0].RowCount);
    }

    [Fact]
    public async Task BuildAsync_MultipleChunks_SplitsCorrectly()
    {
        Row[] rows = Enumerable.Range(0, 25).Select(i =>
            MakeRow(("value", DataValue.FromScalar((float)i)))).ToArray();

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("data");
        SourceIndexBuilder builder = new(chunkSize: 10);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.Equal(25, index.Schema.TotalRowCount);
        Assert.Equal(3, index.Chunks.Count);

        Assert.Equal(0, index.Chunks[0].RowOffset);
        Assert.Equal(10, index.Chunks[0].RowCount);

        Assert.Equal(10, index.Chunks[1].RowOffset);
        Assert.Equal(10, index.Chunks[1].RowCount);

        Assert.Equal(20, index.Chunks[2].RowOffset);
        Assert.Equal(5, index.Chunks[2].RowCount);
    }

    [Fact]
    public async Task BuildAsync_TracksMinMax_ForScalarColumn()
    {
        Row[] rows =
        [
            MakeRow(("x", DataValue.FromScalar(10.0f))),
            MakeRow(("x", DataValue.FromScalar(5.0f))),
            MakeRow(("x", DataValue.FromScalar(20.0f))),
            MakeRow(("x", DataValue.FromScalar(1.0f))),
        ];

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("minmax");
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        ChunkColumnStatistics stats = index.Chunks[0].ColumnStatistics["x"];
        Assert.Equal(1.0f, stats.Minimum!.AsScalar());
        Assert.Equal(20.0f, stats.Maximum!.AsScalar());
    }

    [Fact]
    public async Task BuildAsync_TracksNullCount()
    {
        Row[] rows =
        [
            MakeRow(("x", DataValue.FromScalar(1.0f))),
            MakeRow(("x", DataValue.Null(DataKind.Scalar))),
            MakeRow(("x", DataValue.Null(DataKind.Scalar))),
            MakeRow(("x", DataValue.FromScalar(2.0f))),
        ];

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("nulls");
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        ChunkColumnStatistics stats = index.Chunks[0].ColumnStatistics["x"];
        Assert.Equal(2, stats.NullCount);
    }

    [Fact]
    public async Task BuildAsync_WithStream_ComputesFingerprint()
    {
        Row[] rows = [MakeRow(("x", DataValue.FromScalar(1.0f)))];
        byte[] fileContent = new byte[256];
        Random.Shared.NextBytes(fileContent);

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("fingerprinted");
        SourceIndexBuilder builder = new(chunkSize: 100);

        using MemoryStream stream = new(fileContent);
        SourceIndex index = await builder.BuildAsync(descriptor, provider, stream, CancellationToken.None);

        Assert.Equal(256, index.Fingerprint.FileSize);
        Assert.Equal(32, index.Fingerprint.StripedHash.Length);
    }

    [Fact]
    public async Task BuildAsync_WithoutStream_ProducesEmptyFingerprint()
    {
        Row[] rows = [MakeRow(("x", DataValue.FromScalar(1.0f)))];
        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("no-stream");
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.Equal(0, index.Fingerprint.FileSize);
        Assert.Empty(index.Fingerprint.StripedHash);
    }

    [Fact]
    public async Task BuildAsync_EstimatesCardinality()
    {
        Row[] rows = Enumerable.Range(0, 100).Select(i =>
            MakeRow(("category", DataValue.FromString(i < 50 ? "A" : "B")))).ToArray();

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("cardinality");
        SourceIndexBuilder builder = new(chunkSize: 200);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        ChunkColumnStatistics stats = index.Chunks[0].ColumnStatistics["category"];
        // HyperLogLog estimate; with only 2 distinct values it should be close to 2.
        Assert.InRange(stats.EstimatedCardinality, 1, 5);
    }

    [Fact]
    public void IncrementalBuilder_ProducesSameResultAsFullBuild()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        SourceIndexBuilder builder = new(chunkSize: 5);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        Row[] rows = Enumerable.Range(0, 12).Select(i =>
            MakeRow(("value", DataValue.FromScalar((float)i)))).ToArray();

        foreach (Row row in rows)
        {
            incremental.AddRow(row);
        }

        SourceIndex index = incremental.Finalize();

        Assert.Equal(12, index.Schema.TotalRowCount);
        Assert.Equal(3, index.Chunks.Count);

        Assert.Equal(0, index.Chunks[0].RowOffset);
        Assert.Equal(5, index.Chunks[0].RowCount);

        Assert.Equal(5, index.Chunks[1].RowOffset);
        Assert.Equal(5, index.Chunks[1].RowCount);

        Assert.Equal(10, index.Chunks[2].RowOffset);
        Assert.Equal(2, index.Chunks[2].RowCount);
    }

    [Fact]
    public void IncrementalBuilder_NoRows_ProducesEmptyIndex()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        SourceIndexBuilder builder = new(chunkSize: 10);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        SourceIndex index = incremental.Finalize();

        Assert.Equal(0, index.Schema.TotalRowCount);
        Assert.Empty(index.Chunks);
    }

    [Fact]
    public async Task BuildAsync_StringMinMax_TracksCorrectly()
    {
        Row[] rows =
        [
            MakeRow(("name", DataValue.FromString("charlie"))),
            MakeRow(("name", DataValue.FromString("alice"))),
            MakeRow(("name", DataValue.FromString("bob"))),
        ];

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("strings");
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        ChunkColumnStatistics stats = index.Chunks[0].ColumnStatistics["name"];
        Assert.Equal("alice", stats.Minimum!.AsString());
        Assert.Equal("charlie", stats.Maximum!.AsString());
    }

    // ───────────── Helpers ─────────────

    private static TableDescriptor CreateDescriptor(string name)
    {
        return new TableDescriptor("test", name, $"{name}.test", new Dictionary<string, string>());
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    /// <summary>
    /// Simple in-memory provider for testing. Yields pre-built rows.
    /// </summary>
    private sealed class InMemoryTableProvider : ITableProvider
    {
        private readonly Row[] _rows;

        public InMemoryTableProvider(Row[] rows)
        {
            _rows = rows;
        }

        public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            if (_rows.Length == 0)
            {
                return Task.FromResult(new Schema([new ColumnInfo("empty", DataKind.String, nullable: true)]));
            }

            List<ColumnInfo> columns = new();

            foreach (string name in _rows[0].ColumnNames)
            {
                columns.Add(new ColumnInfo(name, _rows[0][name].Kind, nullable: true));
            }

            return Task.FromResult(new Schema(columns));
        }

        public Task<ProviderCapabilities> GetCapabilitiesAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderCapabilities(
                EstimatedRowCount: _rows.Length,
                EstimatedRowSizeBytes: null,
                SupportsSeek: false,
                ColumnCosts: new Dictionary<string, ColumnCost>()));
        }

        public async IAsyncEnumerable<Row> OpenAsync(
            TableDescriptor descriptor,
            IReadOnlySet<string>? requiredColumns,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (Row row in _rows)
            {
                yield return row;
            }

            await Task.CompletedTask;
        }
    }
}

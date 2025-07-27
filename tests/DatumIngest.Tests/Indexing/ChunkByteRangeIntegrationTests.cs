using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Integration tests verifying that <see cref="SourceIndexBuilder"/> populates
/// <see cref="IndexChunk.SourceByteOffset"/> and <see cref="IndexChunk.SourceByteLength"/>
/// when the provider implements <see cref="IChunkMeasuringProvider"/>.
/// </summary>
public sealed class ChunkByteRangeIntegrationTests : IDisposable
{
    private readonly string _tempDirectory;

    public ChunkByteRangeIntegrationTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"datum-byterange-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private string WriteTempFile(string fileName, string content)
    {
        string path = Path.Combine(_tempDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    // ───────────────────── CSV integration ─────────────────────

    [Fact]
    public async Task BuildAsync_CsvProvider_PopulatesByteOffsets()
    {
        string content = "name,value\nAlice,1\nBob,2\nCharlie,3\nDiana,4\nEve,5\n";
        string path = WriteTempFile("csv-offsets.csv", content);

        CsvTableProvider provider = new();
        TableDescriptor descriptor = new("csv", "test", path, new Dictionary<string, string>());
        SourceIndexBuilder builder = new(chunkSize: 2);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.Equal(3, index.Chunks.Count);

        // All chunks should have valid byte offsets (not -1).
        foreach (IndexChunk chunk in index.Chunks)
        {
            Assert.NotEqual(-1, chunk.SourceByteOffset);
            Assert.NotEqual(-1, chunk.SourceByteLength);
            Assert.True(chunk.SourceByteOffset >= 0);
            Assert.True(chunk.SourceByteLength > 0);
        }

        // First chunk starts after the header line.
        Assert.True(index.Chunks[0].SourceByteOffset > 0);
    }

    [Fact]
    public async Task BuildAsync_CsvProvider_ByteRangesAreContiguous()
    {
        string content = "x\n1\n2\n3\n4\n5\n6\n";
        string path = WriteTempFile("csv-contiguous.csv", content);

        CsvTableProvider provider = new();
        TableDescriptor descriptor = new("csv", "test", path, new Dictionary<string, string>());
        SourceIndexBuilder builder = new(chunkSize: 2);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        // Verify chunks are contiguous: each chunk starts where the previous one ended.
        for (int i = 1; i < index.Chunks.Count; i++)
        {
            long previousEnd = index.Chunks[i - 1].SourceByteOffset + index.Chunks[i - 1].SourceByteLength;
            Assert.Equal(previousEnd, index.Chunks[i].SourceByteOffset);
        }
    }

    // ───────────────────── JSONL integration ─────────────────────

    [Fact]
    public async Task BuildAsync_JsonlProvider_PopulatesByteOffsets()
    {
        string content = "{\"id\":1}\n{\"id\":2}\n{\"id\":3}\n{\"id\":4}\n{\"id\":5}\n";
        string path = WriteTempFile("jsonl-offsets.jsonl", content);

        JsonlTableProvider provider = new();
        TableDescriptor descriptor = new("jsonl", "test", path, new Dictionary<string, string>());
        SourceIndexBuilder builder = new(chunkSize: 2);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.Equal(3, index.Chunks.Count);

        foreach (IndexChunk chunk in index.Chunks)
        {
            Assert.NotEqual(-1, chunk.SourceByteOffset);
            Assert.NotEqual(-1, chunk.SourceByteLength);
            Assert.True(chunk.SourceByteOffset >= 0);
            Assert.True(chunk.SourceByteLength > 0);
        }

        // JSONL has no header, so first chunk should start at offset 0.
        Assert.Equal(0, index.Chunks[0].SourceByteOffset);
    }

    [Fact]
    public async Task BuildAsync_JsonlProvider_ByteRangesAreContiguous()
    {
        string content = "{\"v\":1}\n{\"v\":2}\n{\"v\":3}\n{\"v\":4}\n";
        string path = WriteTempFile("jsonl-contiguous.jsonl", content);

        JsonlTableProvider provider = new();
        TableDescriptor descriptor = new("jsonl", "test", path, new Dictionary<string, string>());
        SourceIndexBuilder builder = new(chunkSize: 2);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        for (int i = 1; i < index.Chunks.Count; i++)
        {
            long previousEnd = index.Chunks[i - 1].SourceByteOffset + index.Chunks[i - 1].SourceByteLength;
            Assert.Equal(previousEnd, index.Chunks[i].SourceByteOffset);
        }
    }

    // ───────────────────── Non-measurable provider ─────────────────────

    [Fact]
    public async Task BuildAsync_NonMeasurableProvider_ByteOffsetsRemainNegativeOne()
    {
        Row[] rows =
        [
            MakeRow(("x", DataValue.FromScalar(1.0f))),
            MakeRow(("x", DataValue.FromScalar(2.0f))),
            MakeRow(("x", DataValue.FromScalar(3.0f))),
        ];

        PlainInMemoryProvider provider = new(rows);
        TableDescriptor descriptor = new("test", "mem", "mem.test", new Dictionary<string, string>());
        SourceIndexBuilder builder = new(chunkSize: 2);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        foreach (IndexChunk chunk in index.Chunks)
        {
            Assert.Equal(-1, chunk.SourceByteOffset);
            Assert.Equal(-1, chunk.SourceByteLength);
        }
    }

    // ───────────────────── Round-trip persistence ─────────────────────

    [Fact]
    public async Task ByteOffsets_SurviveWriterReaderRoundTrip()
    {
        string content = "{\"id\":1}\n{\"id\":2}\n{\"id\":3}\n{\"id\":4}\n";
        string path = WriteTempFile("roundtrip.jsonl", content);

        JsonlTableProvider provider = new();
        TableDescriptor descriptor = new("jsonl", "test", path, new Dictionary<string, string>());
        SourceIndexBuilder builder = new(chunkSize: 2);

        SourceIndex original = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        // Write and read back.
        using MemoryStream stream = new();
        IndexWriter writer = new();
        SourceIndexSet indexSet = SourceIndexSet.Create("test", original);
        writer.Write(indexSet, stream);

        stream.Position = 0;
        IndexReader reader = new();
        SourceIndexSet deserializedSet = reader.Read(stream);
        SourceIndex deserialized = deserializedSet.Tables["test"];

        Assert.Equal(original.Chunks.Count, deserialized.Chunks.Count);

        for (int i = 0; i < original.Chunks.Count; i++)
        {
            Assert.Equal(original.Chunks[i].SourceByteOffset, deserialized.Chunks[i].SourceByteOffset);
            Assert.Equal(original.Chunks[i].SourceByteLength, deserialized.Chunks[i].SourceByteLength);
        }
    }

    // ───────────────────── Helpers ─────────────────────

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    /// <summary>
    /// In-memory provider that does NOT implement <see cref="IChunkMeasuringProvider"/>.
    /// </summary>
    private sealed class PlainInMemoryProvider : ITableProvider
    {
        private readonly Row[] _rows;

        public PlainInMemoryProvider(Row[] rows)
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

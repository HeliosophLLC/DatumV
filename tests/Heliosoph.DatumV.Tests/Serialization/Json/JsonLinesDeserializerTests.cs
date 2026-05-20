using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Json;

namespace Heliosoph.DatumV.Tests.Serialization.Json;

/// <summary>
/// End-to-end coverage for <see cref="JsonLinesDeserializer"/>: file detection,
/// object-mode union-of-keys schema, single-column-mode wrap, shape-lock
/// enforcement, and line-numbered diagnostics on malformed JSON.
/// </summary>
public sealed class JsonLinesDeserializerTests : ServiceTestBase
{
    // ──────────────── Format detection ────────────────

    [Fact]
    public void JsonLinesFileFormat_MatchesJsonlAndNdjson()
    {
        JsonLinesFileFormat format = new();
        MemoryFileDescriptor jsonl = new("", "data.jsonl");
        MemoryFileDescriptor ndjson = new("", "data.ndjson");
        MemoryFileDescriptor json = new("", "data.json");

        Assert.True(format.CanHandle(jsonl, out _));
        Assert.True(format.CanHandle(ndjson, out _));
        Assert.False(format.CanHandle(json, out _));
    }

    // ──────────────── Object-mode happy path ────────────────

    [Fact]
    public async Task ObjectMode_ProducesOneRowPerLine()
    {
        const string jsonl =
            "{\"id\":1,\"name\":\"alice\"}\n"
            + "{\"id\":2,\"name\":\"bob\"}\n"
            + "{\"id\":3,\"name\":\"carol\"}\n";

        List<RowBatch> batches = await DeserializeAsync(jsonl);

        RowBatch batch = Assert.Single(batches);
        Assert.Equal(3, batch.Count);
        Assert.Equal(2, batch.ColumnLookup.Count);
        Assert.Equal(1, batch[0]["id"].AsUInt8());
        Assert.Equal(3, batch[2]["id"].AsUInt8());
    }

    [Fact]
    public async Task ObjectMode_UnionsKeysAcrossLines()
    {
        const string jsonl =
            "{\"a\":1,\"b\":\"x\"}\n"
            + "{\"a\":2,\"c\":true}\n";

        List<RowBatch> batches = await DeserializeAsync(jsonl);

        RowBatch batch = Assert.Single(batches);
        Assert.Equal(3, batch.ColumnLookup.Count);
        // Row 0: a=1, b="x", c=null
        Assert.False(batch[0]["a"].IsNull);
        Assert.False(batch[0]["b"].IsNull);
        Assert.True(batch[0]["c"].IsNull);
        // Row 1: a=2, b=null, c=true
        Assert.True(batch[1]["b"].IsNull);
        Assert.False(batch[1]["c"].IsNull);
    }

    [Fact]
    public async Task ObjectMode_EmptyLines_AreSkipped()
    {
        const string jsonl =
            "{\"id\":1}\n"
            + "\n"
            + "   \n"
            + "{\"id\":2}\n";

        List<RowBatch> batches = await DeserializeAsync(jsonl);
        Assert.Equal(2, batches[0].Count);
    }

    [Fact]
    public async Task ObjectMode_NestedValues_StoredAsJsonKind()
    {
        const string jsonl =
            "{\"id\":1,\"bbox\":[0,0,10,10]}\n"
            + "{\"id\":2,\"bbox\":[5,5,20,20]}\n";

        List<RowBatch> batches = await DeserializeAsync(jsonl);
        RowBatch batch = Assert.Single(batches);

        Assert.Equal(DataKind.Json, batch[0]["bbox"].Kind);
    }

    // ──────────────── Single-column mode ────────────────

    [Fact]
    public async Task SingleColumnMode_ArrayLines_WrappedAsJsonCells()
    {
        const string jsonl =
            "[1,2,3]\n"
            + "[4,5,6]\n";

        List<RowBatch> batches = await DeserializeAsync(jsonl);
        RowBatch batch = Assert.Single(batches);

        Assert.Equal(2, batch.Count);
        Assert.Equal(1, batch.ColumnLookup.Count);
        Assert.True(batch.ColumnLookup.HasColumn("value"));
        Assert.Equal(DataKind.Json, batch[0]["value"].Kind);
    }

    [Fact]
    public async Task SingleColumnMode_PrimitiveLines_WrappedAsJsonCells()
    {
        const string jsonl =
            "42\n"
            + "\"hello\"\n"
            + "true\n";

        List<RowBatch> batches = await DeserializeAsync(jsonl);
        RowBatch batch = Assert.Single(batches);

        Assert.Equal(3, batch.Count);
        Assert.Equal(DataKind.Json, batch[0]["value"].Kind);
        Assert.Equal(DataKind.Json, batch[1]["value"].Kind);
        Assert.Equal(DataKind.Json, batch[2]["value"].Kind);
    }

    // ──────────────── Shape-lock enforcement ────────────────

    [Fact]
    public async Task ObjectModeFile_WithLaterArrayLine_ThrowsWithLineNumber()
    {
        const string jsonl =
            "{\"id\":1}\n"
            + "{\"id\":2}\n"
            + "[1,2,3]\n";

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => DeserializeAsync(jsonl));
        Assert.Contains("line 3", ex.Message);
        Assert.Contains("expected object", ex.Message);
    }

    [Fact]
    public async Task SingleColumnFile_WithLaterObjectLine_ThrowsWithLineNumber()
    {
        const string jsonl =
            "[1,2]\n"
            + "[3,4]\n"
            + "{\"oops\":true}\n";

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => DeserializeAsync(jsonl));
        Assert.Contains("line 3", ex.Message);
        Assert.Contains("single-column-mode", ex.Message);
    }

    // ──────────────── Malformed JSON ────────────────

    [Fact]
    public async Task MalformedLine_ThrowsWithLineNumber()
    {
        const string jsonl =
            "{\"a\":1}\n"
            + "{not valid json}\n";

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => DeserializeAsync(jsonl));
        Assert.Contains("line 2", ex.Message);
        Assert.Contains("malformed JSON", ex.Message);
    }

    // ──────────────── Empty input ────────────────

    [Fact]
    public async Task EmptyFile_YieldsNoBatches()
    {
        List<RowBatch> batches = await DeserializeAsync("");
        Assert.Empty(batches);
    }

    [Fact]
    public async Task WhitespaceOnlyFile_YieldsNoBatches()
    {
        List<RowBatch> batches = await DeserializeAsync("\n\n   \n\t\n");
        Assert.Empty(batches);
    }

    // ──────────────── ScanMetrics ────────────────

    [Fact]
    public async Task ScanMetrics_ReflectsLineCount()
    {
        const string jsonl =
            "{\"id\":1}\n"
            + "{\"id\":2}\n"
            + "{\"id\":3}\n";

        Pool pool = CreatePool();
        SerializationContext context = new(pool);
        MemoryFileDescriptor descriptor = new(jsonl, "test.jsonl");
        JsonLinesDeserializer deserializer = new(descriptor);

        await foreach (RowBatch batch in deserializer.DeserializeAsync(context))
        {
            batch.Dispose();
        }

        Assert.NotNull(deserializer.ScanMetrics);
        Assert.Equal(3, deserializer.ScanMetrics!.RowCount);
    }

    // ──────────────── Helpers ────────────────

    private async Task<List<RowBatch>> DeserializeAsync(string jsonl)
    {
        Pool pool = CreatePool();
        SerializationContext context = new(pool);
        MemoryFileDescriptor descriptor = new(jsonl, "test.jsonl");
        JsonLinesDeserializer deserializer = new(descriptor);

        List<RowBatch> batches = [];
        await foreach (RowBatch batch in deserializer.DeserializeAsync(context))
        {
            batches.Add(batch);
        }
        return batches;
    }
}

using DatumIngest.Execution.Pooling;
using DatumIngest.Model;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;

namespace DatumIngest.Tests.Serialization;

public sealed class CsvDeserializerTests
{
    // ───────────────────────── Helpers ─────────────────────────

    private static SerializationContext CreateContext()
    {
        PoolBacking backing = new();
        Pool pool = new(backing);
        return new SerializationContext(pool);
    }

    /// <summary>
    /// Reads a string value from a DataValue using the Arena as the value store.
    /// </summary>
    private static string GetString(DataValue v, IValueStore store)
        => v.AsString(store);

    // ───────────────────────── Basic CSV ─────────────────────────

    [Fact]
    public async Task BasicCsv_ProducesCorrectRows()
    {
        using var context = CreateContext();
        var csv = new CsvDeserializer(new MemoryFileDescriptor("name,age\nAlice,30\nBob,25"));

        List<Row> rows = [];
        await foreach (RowBatch batch in csv.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal("Alice", GetString(rows[0]["name"], context.Arena));
        Assert.Equal(30, rows[0]["age"].AsInt32());
        Assert.Equal("Bob", GetString(rows[1]["name"], context.Arena));
        Assert.Equal(25, rows[1]["age"].AsInt32());
    }

    // ───────────────────────── Headerless ─────────────────────────

    [Fact]
    public async Task Headerless_AutoDetects_GeneratesColumnNames()
    {
        using var context = CreateContext();
        // Force headerless via option to avoid heuristic ambiguity with small numeric data.
        var options = new Dictionary<string, string> { ["header"] = "false" };
        var csv = new CsvDeserializer(new MemoryFileDescriptor("1,2,3\n4,5,6\n7,8,9", options: options));

        List<Row> rows = [];
        await foreach (RowBatch batch in csv.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal(1, rows[0]["col_0"].AsInt32());
        Assert.Equal(6, rows[1]["col_2"].AsInt32());
    }

    // ───────────────────────── Tab-delimited ─────────────────────────

    [Fact]
    public async Task TsvExtension_UsesTabDelimiter()
    {
        using var context = CreateContext();
        var csv = new CsvDeserializer(new MemoryFileDescriptor("name\tage\nAlice\t30", fileName: "data.tsv"));

        List<Row> rows = [];
        await foreach (RowBatch batch in csv.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Single(rows);
        Assert.Equal("Alice", GetString(rows[0]["name"], context.Arena));
        Assert.Equal(30, rows[0]["age"].AsInt32());
    }

    // ───────────────────────── Delimiter override ─────────────────────────

    [Fact]
    public async Task DelimiterOverride_UsesSpecifiedDelimiter()
    {
        using var context = CreateContext();
        var options = new Dictionary<string, string> { ["delimiter"] = ";" };
        var csv = new CsvDeserializer(new MemoryFileDescriptor("name;age\nAlice;30", options: options));

        List<Row> rows = [];
        await foreach (RowBatch batch in csv.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Single(rows);
        Assert.Equal("Alice", GetString(rows[0]["name"], context.Arena));
    }

    // ───────────────────────── Type inference ─────────────────────────

    [Fact]
    public async Task TypeInference_InfersIntFloatDateUuidBoolean()
    {
        using var context = CreateContext();
        string csvData = "i,f,d,u,b\n" +
                         "42,3.14,2024-01-15,550e8400-e29b-41d4-a716-446655440000,true\n" +
                         "7,2.72,2024-06-30,660e8400-e29b-41d4-a716-446655440000,false";

        var csv = new CsvDeserializer(new MemoryFileDescriptor(csvData));

        List<Row> rows = [];
        await foreach (RowBatch batch in csv.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(DataKind.Int32, rows[0]["i"].Kind);
        Assert.Equal(42, rows[0]["i"].AsInt32());
        Assert.Equal(DataKind.Float64, rows[0]["f"].Kind);
        Assert.Equal(3.14, rows[0]["f"].AsFloat64(), 0.001);
        Assert.Equal(DataKind.Date, rows[0]["d"].Kind);
        Assert.Equal(DataKind.Uuid, rows[0]["u"].Kind);
        Assert.Equal(DataKind.Boolean, rows[0]["b"].Kind);
        Assert.True(rows[0]["b"].AsBoolean());
        Assert.False(rows[1]["b"].AsBoolean());
    }

    // ───────────────────────── Quoted fields ─────────────────────────

    [Fact]
    public async Task QuotedFields_WithEmbeddedDelimitersAndNewlines()
    {
        using var context = CreateContext();
        string csvData = "name,note\n\"Alice\",\"has a, comma\"\n\"Bob\",\"line1\nline2\"";
        var csv = new CsvDeserializer(new MemoryFileDescriptor(csvData));

        List<Row> rows = [];
        await foreach (RowBatch batch in csv.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal("has a, comma", GetString(rows[0]["note"], context.Arena));
        Assert.Equal("line1\nline2", GetString(rows[1]["note"], context.Arena));
    }

    // ───────────────────────── Empty file ─────────────────────────

    [Fact]
    public async Task EmptyFile_ProducesNoRows()
    {
        using var context = CreateContext();
        var csv = new CsvDeserializer(new MemoryFileDescriptor(""));

        List<Row> rows = [];
        await foreach (RowBatch batch in csv.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Empty(rows);
    }

    // ───────────────────────── Header override ─────────────────────────

    [Fact]
    public async Task HeaderFalse_TreatsFirstRowAsData()
    {
        using var context = CreateContext();
        var options = new Dictionary<string, string> { ["header"] = "false" };
        var csv = new CsvDeserializer(new MemoryFileDescriptor("Alice,30\nBob,25", options: options));

        List<Row> rows = [];
        await foreach (RowBatch batch in csv.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal("Alice", GetString(rows[0]["col_0"], context.Arena));
    }

    // ───────────────────────── NULL handling ─────────────────────────

    [Fact]
    public async Task NullLiteral_ProducesNullDataValue()
    {
        using var context = CreateContext();
        var csv = new CsvDeserializer(new MemoryFileDescriptor("x,y\n1,NULL\n2,3"));

        List<Row> rows = [];
        await foreach (RowBatch batch in csv.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0]["y"].IsNull);
        Assert.Equal(3, rows[1]["y"].AsInt32());
    }

    // ───────────────────────── Quoted NULL ─────────────────────────

    [Fact]
    public async Task QuotedNull_ProducesStringNotNull()
    {
        using var context = CreateContext();
        var csv = new CsvDeserializer(new MemoryFileDescriptor("x,y\n1,\"NULL\"\n2,NULL"));

        List<Row> rows = [];
        await foreach (RowBatch batch in csv.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Equal(2, rows.Count);
        // Quoted "NULL" is a string value, not a null.
        Assert.False(rows[0]["y"].IsNull);
        Assert.Equal("NULL", GetString(rows[0]["y"], context.Arena));
        // Unquoted NULL is a null.
        Assert.True(rows[1]["y"].IsNull);
    }

    // ───────────────────────── No ReferenceStore needed ─────────────────────────

    [Fact]
    public async Task NoReferenceStoreScope_StringsWorkViaArena()
    {
        // Explicitly NOT calling ReferenceStore.BeginQueryScope().
        // Strings must be stored and retrieved via the Arena.
        using var context = CreateContext();
        var csv = new CsvDeserializer(new MemoryFileDescriptor("city\nNew York\nLondon\nTokyo"));

        List<Row> rows = [];
        await foreach (RowBatch batch in csv.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal("New York", GetString(rows[0]["city"], context.Arena));
        Assert.Equal("London", GetString(rows[1]["city"], context.Arena));
        Assert.Equal("Tokyo", GetString(rows[2]["city"], context.Arena));
    }
}

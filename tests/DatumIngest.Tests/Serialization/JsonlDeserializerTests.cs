using System.Text;
using DatumIngest.Execution.Pooling;
using DatumIngest.Model;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Jsonl;

namespace DatumIngest.Tests.Serialization;

public sealed class JsonlDeserializerTests
{
    // ───────────────────────── Helpers ─────────────────────────

    private sealed class JsonlMockDescriptor : FileFormatDescriptor
    {
        private readonly string _content;

        public JsonlMockDescriptor(string content, string fileName = "mock.jsonl")
            : base(fileName)
        {
            _content = content;
        }

        public override Task<Stream> OpenAsync(CancellationToken cancellationToken = default)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(_content);
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }
    }

    private static DeserializationContext CreateContext()
    {
        PoolBacking backing = new();
        Pool pool = new(backing);
        return new DeserializationContext(pool);
    }

    private static string GetString(DataValue v, IValueStore store)
        => v.AsString(store);

    // ───────────────────────── Basic ─────────────────────────

    [Fact]
    public async Task BasicJsonl_ProducesCorrectRows()
    {
        using var context = CreateContext();
        string data = """
            {"name":"Alice","age":30}
            {"name":"Bob","age":25}
            """;

        var jsonl = new JsonlDeserializer(new JsonlMockDescriptor(data));

        List<Row> rows = [];
        await foreach (RowBatch batch in jsonl.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal("Alice", GetString(rows[0]["name"], context.Arena));
        Assert.Equal(30, rows[0]["age"].AsInt64());
        Assert.Equal("Bob", GetString(rows[1]["name"], context.Arena));
        Assert.Equal(25, rows[1]["age"].AsInt64());
    }

    // ───────────────────────── Type inference ─────────────────────────

    [Fact]
    public async Task TypeInference_InfersIntFloatBooleanDateString()
    {
        using var context = CreateContext();
        string data = """
            {"i":42,"f":3.14,"b":true,"d":"2024-01-15","s":"hello"}
            {"i":7,"f":2.72,"b":false,"d":"2024-06-30","s":"world"}
            """;

        var jsonl = new JsonlDeserializer(new JsonlMockDescriptor(data));

        List<Row> rows = [];
        await foreach (RowBatch batch in jsonl.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(DataKind.Int64, rows[0]["i"].Kind);
        Assert.Equal(42L, rows[0]["i"].AsInt64());
        Assert.Equal(DataKind.Float64, rows[0]["f"].Kind);
        Assert.Equal(3.14, rows[0]["f"].AsFloat64(), 0.001);
        Assert.Equal(DataKind.Boolean, rows[0]["b"].Kind);
        Assert.True(rows[0]["b"].AsBoolean());
        Assert.Equal(DataKind.Date, rows[0]["d"].Kind);
        Assert.Equal(DataKind.String, rows[0]["s"].Kind);
    }

    // ───────────────────────── Null handling ─────────────────────────

    [Fact]
    public async Task NullValues_ProduceNullDataValues()
    {
        using var context = CreateContext();
        // JSON null values in the sample widen the column to String.
        // The null row still produces a null DataValue regardless of kind.
        string data = """
            {"x":1,"y":3}
            {"x":2,"y":null}
            """;

        var jsonl = new JsonlDeserializer(new JsonlMockDescriptor(data));

        List<Row> rows = [];
        await foreach (RowBatch batch in jsonl.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Equal(2, rows.Count);
        Assert.False(rows[0]["y"].IsNull);
        Assert.True(rows[1]["y"].IsNull);
    }

    // ───────────────────────── Missing properties ─────────────────────────

    [Fact]
    public async Task MissingProperties_ProduceNullDataValues()
    {
        using var context = CreateContext();
        string data = """
            {"a":1,"b":2}
            {"a":3}
            """;

        var jsonl = new JsonlDeserializer(new JsonlMockDescriptor(data));

        List<Row> rows = [];
        await foreach (RowBatch batch in jsonl.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(2L, rows[0]["b"].AsInt64());
        Assert.True(rows[1]["b"].IsNull);
    }

    // ───────────────────────── Empty file ─────────────────────────

    [Fact]
    public async Task EmptyFile_ProducesNoRows()
    {
        using var context = CreateContext();
        var jsonl = new JsonlDeserializer(new JsonlMockDescriptor(""));

        List<Row> rows = [];
        await foreach (RowBatch batch in jsonl.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Empty(rows);
    }

    // ───────────────────────── Blank lines skipped ─────────────────────────

    [Fact]
    public async Task BlankLines_AreSkipped()
    {
        using var context = CreateContext();
        string data = """
            {"x":1}

            {"x":2}

            {"x":3}
            """;

        var jsonl = new JsonlDeserializer(new JsonlMockDescriptor(data));

        List<Row> rows = [];
        await foreach (RowBatch batch in jsonl.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Equal(3, rows.Count);
    }

    // ───────────────────────── JSON values preserved ─────────────────────────

    [Fact]
    public async Task NestedObjects_PreservedAsJsonValue()
    {
        using var context = CreateContext();
        string data = """
            {"id":1,"meta":{"key":"value"}}
            {"id":2,"meta":[1,2,3]}
            """;

        var jsonl = new JsonlDeserializer(new JsonlMockDescriptor(data));

        List<Row> rows = [];
        await foreach (RowBatch batch in jsonl.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(DataKind.JsonValue, rows[0]["meta"].Kind);
    }

    // ───────────────────────── No ReferenceStore needed ─────────────────────────

    [Fact]
    public async Task NoReferenceStoreScope_StringsWorkViaArena()
    {
        using var context = CreateContext();
        string data = """
            {"city":"New York"}
            {"city":"London"}
            {"city":"Tokyo"}
            """;

        var jsonl = new JsonlDeserializer(new JsonlMockDescriptor(data));

        List<Row> rows = [];
        await foreach (RowBatch batch in jsonl.DeserializeAsync(context))
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

    // ───────────────────────── Malformed line ─────────────────────────

    [Fact]
    public async Task MalformedLine_ThrowsWithLineNumber()
    {
        using var context = CreateContext();
        string data = """
            {"x":1}
            {"x":2}
            not json
            {"x":4}
            """;

        var jsonl = new JsonlDeserializer(new JsonlMockDescriptor(data));

        var ex = await Assert.ThrowsAsync<DeserializationException>(async () =>
        {
            await foreach (RowBatch batch in jsonl.DeserializeAsync(context))
            {
                context.Pool.ReturnRowBatch(batch, returnDataValues: true);
            }
        });

        Assert.Contains("3", ex.Message); // Line number
    }
}

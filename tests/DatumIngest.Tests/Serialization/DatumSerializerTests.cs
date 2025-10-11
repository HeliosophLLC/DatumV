using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution.Pooling;
using DatumIngest.Model;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;
using DatumIngest.Serialization.Datum;

namespace DatumIngest.Tests.Serialization;

public sealed class DatumSerializerTests : IDisposable
{
    private readonly string _tempDir;

    public DatumSerializerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"datumtest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static SerializationContext CreateContext()
    {
        PoolBacking backing = new();
        Pool pool = new(backing);
        return new SerializationContext(pool);
    }

    private static async IAsyncEnumerable<RowBatch> RowsToBatches(
        SerializationContext context, IReadOnlyList<string> names, params DataValue[][] rowValues)
    {
        Dictionary<string, int> nameIndex = new(names.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
            nameIndex[names[i]] = i;

        RowBatch batch = context.Pool.RentBatch(1024);
        foreach (DataValue[] values in rowValues)
            batch.Add(new Row(names, values, nameIndex));

        yield return batch;
        await Task.CompletedTask;
    }

    private string TempPath(string name) => Path.Combine(_tempDir, name);

    private static TableDescriptor Descriptor(string filePath)
        => new("datum", "test", filePath, new Dictionary<string, string>());

    private static async Task<List<Row>> ReadDatum(string filePath)
    {
        DatumFileTableProvider provider = new();
        return await provider.OpenAsync(Descriptor(filePath), requiredColumns: null, CancellationToken.None)
            .CollectRowsAsync();
    }

    // ───────────────────────── Basic ─────────────────────────

    [Fact]
    public async Task BasicDatum_RoundTripsIntAndFloat()
    {
        using var context = CreateContext();
        string path = TempPath("basic.datum");
        string[] names = ["x", "y"];
        var rows = new[]
        {
            new DataValue[] { DataValue.FromInt32(1), DataValue.FromFloat64(1.5) },
            new DataValue[] { DataValue.FromInt32(2), DataValue.FromFloat64(2.7) },
            new DataValue[] { DataValue.FromInt32(3), DataValue.FromFloat64(3.9) },
        };

        var serializer = new DatumSerializer(new OutputDescriptor(path));
        await serializer.SerializeAsync(context, RowsToBatches(context, names, rows));

        List<Row> result = await ReadDatum(path);

        Assert.Equal(3, result.Count);
        Assert.Equal(1, result[0]["x"].AsInt32());
        Assert.Equal(1.5, result[0]["y"].AsFloat64(), 0.001);
        Assert.Equal(3, result[2]["x"].AsInt32());
        Assert.Equal(3.9, result[2]["y"].AsFloat64(), 0.001);
    }

    // ───────────────────────── String columns ─────────────────────────

    [Fact]
    public async Task StringColumns_RoundTrip()
    {
        using var context = CreateContext();
        IValueStore store = context.Arena;
        string path = TempPath("strings.datum");
        string[] names = ["name", "city"];
        var rows = new[]
        {
            new DataValue[] { DataValue.FromString("Alice", store), DataValue.FromString("NYC", store) },
            new DataValue[] { DataValue.FromString("Bob", store), DataValue.FromString("London", store) },
        };

        var serializer = new DatumSerializer(new OutputDescriptor(path));
        await serializer.SerializeAsync(context, RowsToBatches(context, names, rows));

        List<Row> result = await ReadDatum(path);

        Assert.Equal(2, result.Count);
        Assert.Equal("Alice", result[0]["name"].AsString());
        Assert.Equal("NYC", result[0]["city"].AsString());
        Assert.Equal("Bob", result[1]["name"].AsString());
        Assert.Equal("London", result[1]["city"].AsString());
    }

    // ───────────────────────── Type coverage ─────────────────────────

    [Fact]
    public async Task TypeCoverage_RoundTripsScalarTypes()
    {
        using var context = CreateContext();
        IValueStore store = context.Arena;
        string path = TempPath("types.datum");
        var date = new DateOnly(2024, 6, 15);
        var dateTime = new DateTimeOffset(2024, 6, 15, 10, 30, 0, TimeSpan.Zero);

        string[] names = ["bool", "i32", "i64", "f32", "f64", "date", "datetime", "str"];
        var rows = new[]
        {
            new DataValue[]
            {
                DataValue.FromBoolean(true),
                DataValue.FromInt32(42),
                DataValue.FromInt64(123456789L),
                DataValue.FromFloat32(3.14f),
                DataValue.FromFloat64(2.718),
                DataValue.FromDate(date),
                DataValue.FromDateTime(dateTime),
                DataValue.FromString("hello", store),
            },
        };

        var serializer = new DatumSerializer(new OutputDescriptor(path));
        await serializer.SerializeAsync(context, RowsToBatches(context, names, rows));

        List<Row> result = await ReadDatum(path);

        Assert.Single(result);
        Row r = result[0];
        Assert.True(r["bool"].AsBoolean());
        Assert.Equal(42, r["i32"].AsInt32());
        Assert.Equal(123456789L, r["i64"].AsInt64());
        Assert.Equal(3.14f, r["f32"].AsFloat32(), 0.001f);
        Assert.Equal(2.718, r["f64"].AsFloat64(), 0.001);
        Assert.Equal(date, r["date"].AsDate());
        Assert.Equal(dateTime, r["datetime"].AsDateTime());
        Assert.Equal("hello", r["str"].AsString());
    }

    // ───────────────────────── Null handling ─────────────────────────

    [Fact]
    public async Task NullValues_RoundTripAsNull()
    {
        using var context = CreateContext();
        string path = TempPath("nulls.datum");
        string[] names = ["x", "y"];
        var rows = new[]
        {
            new DataValue[] { DataValue.FromInt32(1), DataValue.FromInt32(10) },
            new DataValue[] { DataValue.FromInt32(2), DataValue.Null(DataKind.Int32) },
            new DataValue[] { DataValue.FromInt32(3), DataValue.FromInt32(30) },
        };

        var serializer = new DatumSerializer(new OutputDescriptor(path));
        await serializer.SerializeAsync(context, RowsToBatches(context, names, rows));

        List<Row> result = await ReadDatum(path);

        Assert.Equal(3, result.Count);
        Assert.Equal(10, result[0]["y"].AsInt32());
        Assert.True(result[1]["y"].IsNull);
        Assert.Equal(30, result[2]["y"].AsInt32());
    }

    // ───────────────────────── Empty input ─────────────────────────

    [Fact]
    public async Task EmptyInput_WritesNothing()
    {
        using var context = CreateContext();
        string path = TempPath("empty.datum");
        var serializer = new DatumSerializer(new OutputDescriptor(path));

        static async IAsyncEnumerable<RowBatch> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }

        await serializer.SerializeAsync(context, Empty());

        Assert.False(File.Exists(path));
    }

    // ───────────────────────── Cross-format: CSV → .datum ─────────────────────────

    [Fact]
    public async Task CsvToDatum_RoundTrips()
    {
        string csv = "name,age,score\r\nAlice,30,95.5\r\nBob,25,87.3\r\nCharlie,35,92.1\r\n";

        using var csvContext = CreateContext();
        var csvDeserializer = new CsvDeserializer(new MemoryFileDescriptor(csv, "data.csv"));

        string path = TempPath("from_csv.datum");
        var datumSerializer = new DatumSerializer(new OutputDescriptor(path));
        await datumSerializer.SerializeAsync(csvContext, csvDeserializer.DeserializeAsync(csvContext));

        List<Row> result = await ReadDatum(path);

        Assert.Equal(3, result.Count);
        Assert.Equal("Alice", result[0]["name"].AsString());
        // CSV infers Int64 for age; .datum may downsize to Int32 on read.
        long age0 = result[0]["age"].Kind == DataKind.Int64 ? result[0]["age"].AsInt64() : result[0]["age"].AsInt32();
        Assert.Equal(30L, age0);
        Assert.Equal(95.5, result[0]["score"].AsFloat64(), 0.01);
        Assert.Equal("Charlie", result[2]["name"].AsString());
        long age2 = result[2]["age"].Kind == DataKind.Int64 ? result[2]["age"].AsInt64() : result[2]["age"].AsInt32();
        Assert.Equal(35L, age2);
        Assert.Equal(92.1, result[2]["score"].AsFloat64(), 0.01);
    }

    // ───────────────────────── Schema from DatumFileTableProvider ─────────────────────────

    [Fact]
    public async Task WrittenFile_HasCorrectSchema()
    {
        using var context = CreateContext();
        IValueStore store = context.Arena;
        string path = TempPath("schema.datum");
        string[] names = ["id", "label", "score"];
        var rows = new[]
        {
            new DataValue[]
            {
                DataValue.FromInt64(1),
                DataValue.FromString("cat", store),
                DataValue.FromFloat64(0.95),
            },
        };

        var serializer = new DatumSerializer(new OutputDescriptor(path));
        await serializer.SerializeAsync(context, RowsToBatches(context, names, rows));

        DatumFileTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.Equal("id", schema.Columns[0].Name);
        Assert.Equal(DataKind.Int64, schema.Columns[0].Kind);
        Assert.Equal("label", schema.Columns[1].Name);
        Assert.Equal(DataKind.String, schema.Columns[1].Kind);
        Assert.Equal("score", schema.Columns[2].Name);
        Assert.Equal(DataKind.Float64, schema.Columns[2].Kind);
    }
}

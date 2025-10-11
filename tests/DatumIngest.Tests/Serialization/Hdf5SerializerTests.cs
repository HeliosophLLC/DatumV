using DatumIngest.Execution.Pooling;
using DatumIngest.Model;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;
using DatumIngest.Serialization.Hdf5;

namespace DatumIngest.Tests.Serialization;

public sealed class Hdf5SerializerTests : IDisposable
{
    private readonly string _tempDir;

    public Hdf5SerializerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hdf5test_{Guid.NewGuid():N}");
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

    /// <summary>
    /// Writes rows to HDF5, then reads back via deserializer.
    /// Returns the rows and the read context (caller must dispose).
    /// </summary>
    private async Task<(List<Row> Rows, SerializationContext ReadContext)> RoundTrip(
        SerializationContext writeContext,
        IReadOnlyList<string> names,
        DataValue[][] rowValues,
        string fileName = "test.h5")
    {
        string path = TempPath(fileName);
        var output = new OutputDescriptor(path);
        var serializer = new Hdf5Serializer(output);
        await serializer.SerializeAsync(writeContext, RowsToBatches(writeContext, names, rowValues));

        var readContext = CreateContext();
        var descriptor = new FileFormatDescriptor(path);
        var deserializer = new Hdf5Deserializer(descriptor);

        List<Row> rows = [];
        await foreach (RowBatch batch in deserializer.DeserializeAsync(readContext))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            readContext.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }
        return (rows, readContext);
    }

    // ───────────────────────── Basic ─────────────────────────

    [Fact]
    public async Task BasicHdf5_RoundTripsIntAndFloat()
    {
        using var context = CreateContext();
        string[] names = ["x", "y"];
        var rows = new[]
        {
            new DataValue[] { DataValue.FromInt32(1), DataValue.FromFloat64(1.5) },
            new DataValue[] { DataValue.FromInt32(2), DataValue.FromFloat64(2.7) },
            new DataValue[] { DataValue.FromInt32(3), DataValue.FromFloat64(3.9) },
        };

        (List<Row> result, SerializationContext readCtx) = await RoundTrip(context, names, rows);
        using (readCtx)
        {
            Assert.Equal(3, result.Count);
            Assert.Equal(1, result[0]["x"].AsInt32());
            Assert.Equal(1.5, result[0]["y"].AsFloat64(), 0.001);
            Assert.Equal(2, result[1]["x"].AsInt32());
            Assert.Equal(2.7, result[1]["y"].AsFloat64(), 0.001);
            Assert.Equal(3, result[2]["x"].AsInt32());
            Assert.Equal(3.9, result[2]["y"].AsFloat64(), 0.001);
        }
    }

    // ───────────────────────── String columns ─────────────────────────

    [Fact]
    public async Task StringColumns_RoundTrip()
    {
        using var context = CreateContext();
        IValueStore store = context.Arena;
        string[] names = ["name", "city"];
        var rows = new[]
        {
            new DataValue[] { DataValue.FromString("Alice", store), DataValue.FromString("New York", store) },
            new DataValue[] { DataValue.FromString("Bob", store), DataValue.FromString("London", store) },
        };

        (List<Row> result, SerializationContext readCtx) = await RoundTrip(context, names, rows);
        using (readCtx)
        {
            IValueStore readStore = readCtx.Arena;
            Assert.Equal(2, result.Count);
            Assert.Equal("Alice", result[0]["name"].AsString(readStore));
            Assert.Equal("New York", result[0]["city"].AsString(readStore));
            Assert.Equal("Bob", result[1]["name"].AsString(readStore));
            Assert.Equal("London", result[1]["city"].AsString(readStore));
        }
    }

    // ───────────────────────── Type coverage ─────────────────────────

    [Fact]
    public async Task TypeCoverage_RoundTripsScalarTypes()
    {
        using var context = CreateContext();
        IValueStore store = context.Arena;
        string[] names = ["i32", "i64", "f32", "f64", "u8", "str"];
        var rows = new[]
        {
            new DataValue[]
            {
                DataValue.FromInt32(42),
                DataValue.FromInt64(123456789L),
                DataValue.FromFloat32(3.14f),
                DataValue.FromFloat64(2.718),
                DataValue.FromUInt8(255),
                DataValue.FromString("hello", store),
            },
        };

        (List<Row> result, SerializationContext readCtx) = await RoundTrip(context, names, rows);
        using (readCtx)
        {
            Assert.Single(result);
            Row r = result[0];
            Assert.Equal(42, r["i32"].AsInt32());
            Assert.Equal(123456789L, r["i64"].AsInt64());
            Assert.Equal(3.14f, r["f32"].AsFloat32(), 0.001f);
            Assert.Equal(2.718, r["f64"].AsFloat64(), 0.001);
            Assert.Equal(255, r["u8"].AsUInt8());
            Assert.Equal("hello", r["str"].AsString((IValueStore)readCtx.Arena));
        }
    }

    // ───────────────────────── Empty input ─────────────────────────

    [Fact]
    public async Task EmptyInput_WritesNothing()
    {
        using var context = CreateContext();
        string path = TempPath("empty.h5");
        var output = new OutputDescriptor(path);
        var serializer = new Hdf5Serializer(output);

        static async IAsyncEnumerable<RowBatch> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }

        await serializer.SerializeAsync(context, Empty());

        // No rows → no file written (stream never opened).
        Assert.False(File.Exists(path));
    }

    // ───────────────────────── Cross-format: CSV → HDF5 ─────────────────────────

    [Fact]
    public async Task CsvToHdf5_RoundTrips()
    {
        string csv = "name,value,score\r\nAlpha,10,1.1\r\nBeta,20,2.2\r\nGamma,30,3.3\r\n";

        using var csvContext = CreateContext();
        var csvDeserializer = new CsvDeserializer(new MemoryFileDescriptor(csv, "data.csv"));

        string path = TempPath("csv_to_hdf5.h5");
        var hdf5Output = new OutputDescriptor(path);
        var hdf5Serializer = new Hdf5Serializer(hdf5Output);
        await hdf5Serializer.SerializeAsync(csvContext, csvDeserializer.DeserializeAsync(csvContext));

        // Read back.
        using var readContext = CreateContext();
        var hdf5Deserializer = new Hdf5Deserializer(new FileFormatDescriptor(path));

        List<Row> rows = [];
        await foreach (RowBatch batch in hdf5Deserializer.DeserializeAsync(readContext))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            readContext.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        IValueStore store = readContext.Arena;

        Assert.Equal(3, rows.Count);
        Assert.Equal("Alpha", rows[0]["name"].AsString(store));
        Assert.Equal("Beta", rows[1]["name"].AsString(store));
        Assert.Equal("Gamma", rows[2]["name"].AsString(store));
        Assert.Equal(1.1, rows[0]["score"].AsFloat64(), 0.01);
        Assert.Equal(2.2, rows[1]["score"].AsFloat64(), 0.01);
        Assert.Equal(3.3, rows[2]["score"].AsFloat64(), 0.01);
    }
}

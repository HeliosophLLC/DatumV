using DatumIngest.Execution.Pooling;
using DatumIngest.Model;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;
using DatumIngest.Serialization.Parquet;

namespace DatumIngest.Tests.Serialization;

public sealed class ParquetSerializerTests
{
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

    /// <summary>
    /// Writes rows to Parquet via serializer, then reads back via deserializer.
    /// Returns the rows and the read context (caller must dispose the context).
    /// </summary>
    private static async Task<(List<Row> Rows, SerializationContext ReadContext)> RoundTrip(
        SerializationContext writeContext,
        IReadOnlyList<string> names,
        DataValue[][] rowValues)
    {
        // Serialize to memory.
        var output = new MemoryOutputDescriptor("test.parquet");
        var serializer = new ParquetSerializer(output);
        await serializer.SerializeAsync(writeContext, RowsToBatches(writeContext, names, rowValues));

        // Deserialize back.
        byte[] parquetBytes = output.GetBytes();
        var readContext = CreateContext();
        var descriptor = new MemoryFileDescriptor(parquetBytes, "test.parquet");
        var deserializer = new ParquetDeserializer(descriptor);

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
    public async Task BasicParquet_RoundTripsIntAndString()
    {
        using var context = CreateContext();
        IValueStore store = context.Arena;
        string[] names = ["name", "age"];
        var rows = new[]
        {
            new DataValue[] { DataValue.FromString("Alice", store), DataValue.FromInt64(30) },
            new DataValue[] { DataValue.FromString("Bob", store), DataValue.FromInt64(25) },
        };

        (List<Row> result, SerializationContext readCtx) = await RoundTrip(context, names, rows);
        using (readCtx)
        {
            Assert.Equal(2, result.Count);
            Assert.Equal("Alice", result[0]["name"].AsString((IValueStore)readCtx.Arena));
            Assert.Equal(30L, result[0]["age"].AsInt64());
            Assert.Equal("Bob", result[1]["name"].AsString((IValueStore)readCtx.Arena));
            Assert.Equal(25L, result[1]["age"].AsInt64());
        }
    }

    // ───────────────────────── Null handling ─────────────────────────

    [Fact]
    public async Task NullValues_RoundTripAsNull()
    {
        using var context = CreateContext();
        string[] names = ["x", "y"];
        var rows = new[]
        {
            new DataValue[] { DataValue.FromInt32(1), DataValue.FromInt32(2) },
            new DataValue[] { DataValue.FromInt32(3), DataValue.Null(DataKind.Int32) },
        };

        (List<Row> result, SerializationContext readCtx) = await RoundTrip(context, names, rows);
        using (readCtx)
        {
            Assert.Equal(2, result.Count);
            Assert.False(result[0]["y"].IsNull);
            Assert.Equal(2, result[0]["y"].AsInt32());
            Assert.True(result[1]["y"].IsNull);
        }
    }

    // ───────────────────────── Type coverage ─────────────────────────

    [Fact]
    public async Task TypeCoverage_RoundTripsAllScalarTypes()
    {
        using var context = CreateContext();
        IValueStore store = context.Arena;
        var guid = Guid.Parse("12345678-1234-1234-1234-123456789abc");
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
                DataValue.FromFloat64(2.718281828),
                DataValue.FromDate(date),
                DataValue.FromDateTime(dateTime),
                DataValue.FromString("hello", store),
            },
        };

        (List<Row> result, SerializationContext readCtx) = await RoundTrip(context, names, rows);
        using (readCtx)
        {
            Assert.Single(result);
            Row r = result[0];
            Assert.True(r["bool"].AsBoolean());
            Assert.Equal(42, r["i32"].AsInt32());
            Assert.Equal(123456789L, r["i64"].AsInt64());
            Assert.Equal(3.14f, r["f32"].AsFloat32(), 0.001f);
            Assert.Equal(2.718281828, r["f64"].AsFloat64(), 0.0001);
            // Parquet stores Date as int32 days; Parquet.Net reads it back as DateTime.
            // Verify the date value is preserved by comparing the date portion.
            if (r["date"].Kind == DataKind.Date)
                Assert.Equal(date, r["date"].AsDate());
            else
                Assert.Equal(date, DateOnly.FromDateTime(r["date"].AsDateTime().Date));

            Assert.Equal(dateTime.UtcDateTime, r["datetime"].AsDateTime().UtcDateTime);
            Assert.Equal("hello", r["str"].AsString((IValueStore)readCtx.Arena));
        }
    }

    // ───────────────────────── Empty batches ─────────────────────────

    [Fact]
    public async Task EmptyBatches_ProducesValidEmptyOutput()
    {
        using var context = CreateContext();
        var output = new MemoryOutputDescriptor("test.parquet");
        var serializer = new ParquetSerializer(output);

        static async IAsyncEnumerable<RowBatch> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }

        await serializer.SerializeAsync(context, Empty());

        // No rows, no writer created — output should be empty.
        Assert.Empty(output.GetBytes());
    }

    // ───────────────────────── Float precision ─────────────────────────

    [Fact]
    public async Task FloatPrecision_PreservedThroughRoundTrip()
    {
        using var context = CreateContext();
        string[] names = ["f32", "f64"];
        var rows = new[]
        {
            new DataValue[] { DataValue.FromFloat32(float.MaxValue), DataValue.FromFloat64(double.MaxValue) },
            new DataValue[] { DataValue.FromFloat32(float.MinValue), DataValue.FromFloat64(double.MinValue) },
            new DataValue[] { DataValue.FromFloat32(float.Epsilon), DataValue.FromFloat64(double.Epsilon) },
        };

        (List<Row> result, SerializationContext readCtx) = await RoundTrip(context, names, rows);
        using (readCtx)
        {
            Assert.Equal(3, result.Count);
            Assert.Equal(float.MaxValue, result[0]["f32"].AsFloat32());
            Assert.Equal(double.MaxValue, result[0]["f64"].AsFloat64());
            Assert.Equal(float.MinValue, result[1]["f32"].AsFloat32());
            Assert.Equal(double.MinValue, result[1]["f64"].AsFloat64());
        }
    }

    // ───────────────────────── Multiple batches = multiple row groups ─────────────────────────

    [Fact]
    public async Task MultipleBatches_WritesMultipleRowGroups()
    {
        using var context = CreateContext();
        string[] names = ["x"];

        Dictionary<string, int> nameIndex = new(1, StringComparer.OrdinalIgnoreCase) { ["x"] = 0 };

        async IAsyncEnumerable<RowBatch> ProduceBatches()
        {
            for (int batchNum = 0; batchNum < 3; batchNum++)
            {
                RowBatch batch = context.Pool.RentBatch(1024);
                batch.Add(new Row(names, [DataValue.FromInt32(batchNum)], nameIndex));
                yield return batch;
            }
            await Task.CompletedTask;
        }

        var output = new MemoryOutputDescriptor("test.parquet");
        var serializer = new ParquetSerializer(output);
        await serializer.SerializeAsync(context, ProduceBatches());

        // Read back — should have 3 rows total.
        byte[] parquetBytes = output.GetBytes();
        using var readContext = CreateContext();
        var descriptor = new MemoryFileDescriptor(parquetBytes, "test.parquet");
        var deserializer = new ParquetDeserializer(descriptor);

        List<Row> rows = [];
        await foreach (RowBatch batch in deserializer.DeserializeAsync(readContext))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            readContext.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal(0, rows[0]["x"].AsInt32());
        Assert.Equal(1, rows[1]["x"].AsInt32());
        Assert.Equal(2, rows[2]["x"].AsInt32());
    }

    // ───────────────────────── Cross-format: CSV → Parquet ─────────────────────────

    [Fact]
    public async Task CsvToParquet_RoundTripsAllTypes()
    {
        string csv = "name,age,score,active,date\r\nAlice,30,95.5,true,2024-01-15\r\nBob,25,87.3,false,2024-06-30\r\n";

        // CSV → rows.
        using var csvContext = CreateContext();
        var csvDescriptor = new MemoryFileDescriptor(csv, "data.csv");
        var csvDeserializer = new CsvDeserializer(csvDescriptor);

        // Pipe CSV batches directly into Parquet serializer.
        var parquetOutput = new MemoryOutputDescriptor("data.parquet");
        var parquetSerializer = new ParquetSerializer(parquetOutput);
        await parquetSerializer.SerializeAsync(csvContext, csvDeserializer.DeserializeAsync(csvContext));

        // Parquet → rows.
        byte[] parquetBytes = parquetOutput.GetBytes();
        using var parquetContext = CreateContext();
        var parquetDescriptor = new MemoryFileDescriptor(parquetBytes, "data.parquet");
        var parquetDeserializer = new ParquetDeserializer(parquetDescriptor);

        List<Row> rows = [];
        await foreach (RowBatch batch in parquetDeserializer.DeserializeAsync(parquetContext))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            parquetContext.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        IValueStore store = parquetContext.Arena;

        Assert.Equal(2, rows.Count);

        // Row 0: Alice — CSV infers Int64 for age, Parquet may round-trip as Int32 or Int64.
        Assert.Equal("Alice", rows[0]["name"].AsString(store));
        Assert.Equal(30, rows[0]["age"].Kind == DataKind.Int64 ? (int)rows[0]["age"].AsInt64() : rows[0]["age"].AsInt32());
        Assert.Equal(95.5, rows[0]["score"].AsFloat64(), 0.01);
        Assert.True(rows[0]["active"].AsBoolean());

        // Row 1: Bob
        Assert.Equal("Bob", rows[1]["name"].AsString(store));
        Assert.Equal(25, rows[1]["age"].Kind == DataKind.Int64 ? (int)rows[1]["age"].AsInt64() : rows[1]["age"].AsInt32());
        Assert.Equal(87.3, rows[1]["score"].AsFloat64(), 0.01);
        Assert.False(rows[1]["active"].AsBoolean());
    }

    [Fact]
    public async Task CsvToParquet_NullHandling()
    {
        string csv = "x,y\r\n1,hello\r\n2,\r\n3,world\r\n";

        using var csvContext = CreateContext();
        var csvDeserializer = new CsvDeserializer(new MemoryFileDescriptor(csv, "data.csv"));

        var parquetOutput = new MemoryOutputDescriptor("data.parquet");
        var parquetSerializer = new ParquetSerializer(parquetOutput);
        await parquetSerializer.SerializeAsync(csvContext, csvDeserializer.DeserializeAsync(csvContext));

        byte[] parquetBytes = parquetOutput.GetBytes();
        using var parquetContext = CreateContext();
        var parquetDeserializer = new ParquetDeserializer(
            new MemoryFileDescriptor(parquetBytes, "data.parquet"));

        List<Row> rows = [];
        await foreach (RowBatch batch in parquetDeserializer.DeserializeAsync(parquetContext))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            parquetContext.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        IValueStore store = parquetContext.Arena;

        Assert.Equal(3, rows.Count);
        Assert.Equal("hello", rows[0]["y"].AsString(store));
        // Empty CSV field → null
        Assert.True(rows[1]["y"].IsNull || rows[1]["y"].AsString(store) == "");
        Assert.Equal("world", rows[2]["y"].AsString(store));
    }
}

using DatumIngest.Execution.Pooling;
using DatumIngest.Model;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;

namespace DatumIngest.Tests.Serialization;

public sealed class CsvSerializerTests
{
    // ───────────────────────── Helpers ─────────────────────────

    private static SerializationContext CreateContext()
    {
        PoolBacking backing = new();
        Pool pool = new(backing);
        return new SerializationContext(pool);
    }

    private static async Task<List<Row>> DeserializeAsync(string csv, SerializationContext context)
    {
        var deserializer = new CsvDeserializer(new MemoryFileDescriptor(csv));
        List<Row> rows = [];
        await foreach (RowBatch batch in deserializer.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
                rows.Add(batch[i].Clone());
            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }
        return rows;
    }

    private static async IAsyncEnumerable<RowBatch> RowsToBatches(
        SerializationContext context, IReadOnlyList<string> names, params DataValue[][] rowValues)
    {
        Dictionary<string, int> nameIndex = new(names.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
            nameIndex[names[i]] = i;

        RowBatch batch = context.Pool.RentBatch(1024);
        foreach (DataValue[] values in rowValues)
        {
            batch.Add(new Row(names, values, nameIndex));
        }

        yield return batch;
        await Task.CompletedTask;
    }

    // ───────────────────────── Basic ─────────────────────────

    [Fact]
    public async Task BasicCsv_WritesHeaderAndRows()
    {
        using var context = CreateContext();
        var output = new MemoryOutputDescriptor();
        var serializer = new CsvSerializer(output);

        IValueStore store = context.Arena;
        string[] names = ["name", "age"];
        var rows = new[]
        {
            new DataValue[] { DataValue.FromString("Alice", store), DataValue.FromInt64(30) },
            new DataValue[] { DataValue.FromString("Bob", store), DataValue.FromInt64(25) },
        };

        await serializer.SerializeAsync(context, RowsToBatches(context, names, rows));

        string csv = output.GetOutput();
        string[] lines = csv.TrimEnd('\r', '\n').Split('\n');

        Assert.Equal(3, lines.Length);
        Assert.Equal("name,age", lines[0].TrimEnd('\r'));
        Assert.Equal("Alice,30", lines[1].TrimEnd('\r'));
        Assert.Equal("Bob,25", lines[2].TrimEnd('\r'));
    }

    // ───────────────────────── No header ─────────────────────────

    [Fact]
    public async Task NoHeader_OmitsHeaderRow()
    {
        using var context = CreateContext();
        var options = new Dictionary<string, string> { ["header"] = "false" };
        var output = new MemoryOutputDescriptor(options: options);
        var serializer = new CsvSerializer(output);

        IValueStore store = context.Arena;
        string[] names = ["x", "y"];
        var rows = new[]
        {
            new DataValue[] { DataValue.FromInt32(1), DataValue.FromInt32(2) },
        };

        await serializer.SerializeAsync(context, RowsToBatches(context, names, rows));

        string csv = output.GetOutput();
        string[] lines = csv.TrimEnd('\r', '\n').Split('\n');

        Assert.Single(lines);
        Assert.Equal("1,2", lines[0].TrimEnd('\r'));
    }

    // ───────────────────────── TSV delimiter ─────────────────────────

    [Fact]
    public async Task TabDelimiter_UsesTabs()
    {
        using var context = CreateContext();
        var options = new Dictionary<string, string> { ["delimiter"] = "\t" };
        var output = new MemoryOutputDescriptor(options: options);
        var serializer = new CsvSerializer(output);

        IValueStore store = context.Arena;
        string[] names = ["a", "b"];
        var rows = new[]
        {
            new DataValue[] { DataValue.FromInt32(1), DataValue.FromInt32(2) },
        };

        await serializer.SerializeAsync(context, RowsToBatches(context, names, rows));

        string csv = output.GetOutput();
        Assert.Contains("a\tb", csv);
        Assert.Contains("1\t2", csv);
    }

    // ───────────────────────── Null values ─────────────────────────

    [Fact]
    public async Task NullValues_WriteEmptyFields()
    {
        using var context = CreateContext();
        var output = new MemoryOutputDescriptor();
        var serializer = new CsvSerializer(output);

        string[] names = ["a", "b", "c"];
        var rows = new[]
        {
            new DataValue[]
            {
                DataValue.FromInt32(1),
                DataValue.Null(DataKind.Int32),
                DataValue.FromInt32(3),
            },
        };

        await serializer.SerializeAsync(context, RowsToBatches(context, names, rows));

        string csv = output.GetOutput();
        string[] lines = csv.TrimEnd('\r', '\n').Split('\n');

        Assert.Equal("1,,3", lines[1].TrimEnd('\r'));
    }

    // ───────────────────────── Quoting ─────────────────────────

    [Fact]
    public async Task StringsWithDelimiter_AreQuoted()
    {
        using var context = CreateContext();
        var output = new MemoryOutputDescriptor();
        var serializer = new CsvSerializer(output);

        IValueStore store = context.Arena;
        string[] names = ["val"];
        var rows = new[]
        {
            new DataValue[] { DataValue.FromString("hello,world", store) },
        };

        await serializer.SerializeAsync(context, RowsToBatches(context, names, rows));

        string csv = output.GetOutput();
        Assert.Contains("\"hello,world\"", csv);
    }

    [Fact]
    public async Task StringsWithQuotes_AreEscaped()
    {
        using var context = CreateContext();
        var output = new MemoryOutputDescriptor();
        var serializer = new CsvSerializer(output);

        IValueStore store = context.Arena;
        string[] names = ["val"];
        var rows = new[]
        {
            new DataValue[] { DataValue.FromString("say \"hi\"", store) },
        };

        await serializer.SerializeAsync(context, RowsToBatches(context, names, rows));

        string csv = output.GetOutput();
        Assert.Contains("\"say \"\"hi\"\"\"", csv);
    }

    [Fact]
    public async Task StringsWithNewlines_AreQuoted()
    {
        using var context = CreateContext();
        var output = new MemoryOutputDescriptor();
        var serializer = new CsvSerializer(output);

        IValueStore store = context.Arena;
        string[] names = ["val"];
        var rows = new[]
        {
            new DataValue[] { DataValue.FromString("line1\nline2", store) },
        };

        await serializer.SerializeAsync(context, RowsToBatches(context, names, rows));

        string csv = output.GetOutput();
        Assert.Contains("\"line1\nline2\"", csv);
    }

    // ───────────────────────── Type coverage ─────────────────────────

    [Fact]
    public async Task TypeCoverage_FormatsAllScalarTypes()
    {
        using var context = CreateContext();
        var output = new MemoryOutputDescriptor();
        var serializer = new CsvSerializer(output);

        IValueStore store = context.Arena;
        string[] names = ["bool", "i32", "i64", "f32", "f64", "date", "uuid", "str"];
        var guid = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var rows = new[]
        {
            new DataValue[]
            {
                DataValue.FromBoolean(true),
                DataValue.FromInt32(42),
                DataValue.FromInt64(123456789L),
                DataValue.FromFloat32(3.14f),
                DataValue.FromFloat64(2.718281828),
                DataValue.FromDate(new DateOnly(2024, 1, 15)),
                DataValue.FromUuid(guid),
                DataValue.FromString("hello", store),
            },
        };

        await serializer.SerializeAsync(context, RowsToBatches(context, names, rows));

        string csv = output.GetOutput();
        string[] lines = csv.TrimEnd('\r', '\n').Split('\n');
        string dataLine = lines[1].TrimEnd('\r');

        Assert.Contains("true", dataLine);
        Assert.Contains("42", dataLine);
        Assert.Contains("123456789", dataLine);
        Assert.Contains("3.14", dataLine);
        Assert.Contains("2.71828", dataLine);
        Assert.Contains("2024-01-15", dataLine);
        Assert.Contains("12345678-1234-1234-1234-123456789abc", dataLine);
        Assert.Contains("hello", dataLine);
    }

    // ───────────────────────── Round-trip ─────────────────────────

    [Fact]
    public async Task RoundTrip_DeserializeThenSerialize_PreservesData()
    {
        string inputCsv = "name,age,score\r\nAlice,30,95.5\r\nBob,25,87.3\r\n";

        // Deserialize.
        using var context = CreateContext();
        List<Row> rows = await DeserializeAsync(inputCsv, context);

        // Serialize using the same context (arena still has the string data).
        var output = new MemoryOutputDescriptor();
        var serializer = new CsvSerializer(output);

        await serializer.SerializeAsync(context,
            RowsToBatches(context, rows[0].ColumnNames,
                rows.Select(r => Enumerable.Range(0, r.FieldCount).Select(i => r[i]).ToArray()).ToArray()));

        string outputCsv = output.GetOutput();
        string[] lines = outputCsv.TrimEnd('\r', '\n').Split('\n');

        Assert.Equal(3, lines.Length); // header + 2 data rows
        Assert.Equal("name,age,score", lines[0].TrimEnd('\r'));
        Assert.Equal("Alice,30,95.5", lines[1].TrimEnd('\r'));
        Assert.Equal("Bob,25,87.3", lines[2].TrimEnd('\r'));
    }

    // ───────────────────────── Empty input ─────────────────────────

    [Fact]
    public async Task EmptyBatches_WritesNothing()
    {
        using var context = CreateContext();
        var output = new MemoryOutputDescriptor();
        var serializer = new CsvSerializer(output);

        static async IAsyncEnumerable<RowBatch> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }

        await serializer.SerializeAsync(context, Empty());

        string csv = output.GetOutput();
        Assert.Empty(csv);
    }
}

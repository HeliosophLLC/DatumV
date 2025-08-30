using DatumIngest.DatumFile;
using DatumIngest.Model;
using DatumIngest.Output.Writers;

namespace DatumIngest.Tests.DatumFile;

/// <summary>
/// Round-trip tests for the <see cref="DatumColumnDecoder.DecodeIntoColumn"/> path that
/// decodes directly into <see cref="ColumnBatch"/> buffers.  Each test writes a
/// <c>.datum</c> file, reads it back via <see cref="DatumFileReader.ReadColumnsAsColumnBatch"/>,
/// and verifies every value matches.
/// </summary>
public sealed class ColumnBatchDecodeTests : IAsyncLifetime
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), $"datum_colbatch_{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDirectory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task IntegerColumnsRoundTrip()
    {
        Schema schema = new([
            new ColumnInfo("a", DataKind.Int32, false),
            new ColumnInfo("b", DataKind.Int64, false),
        ]);

        List<Row> rows =
        [
            MakeRow(("a", DataValue.FromInt32(1)), ("b", DataValue.FromInt64(100L))),
            MakeRow(("a", DataValue.FromInt32(2)), ("b", DataValue.FromInt64(200L))),
            MakeRow(("a", DataValue.FromInt32(3)), ("b", DataValue.FromInt64(300L))),
        ];

        using ColumnBatch batch = await WriteAndReadAsColumnBatch("int.datum", schema, rows);

        Assert.Equal(2, batch.ColumnCount);
        Assert.Equal(3, batch.RowCount);
        Assert.Equal(DataValue.FromInt32(1), batch.GetValue(0, 0));
        Assert.Equal(DataValue.FromInt32(2), batch.GetValue(1, 0));
        Assert.Equal(DataValue.FromInt64(200L), batch.GetValue(1, 1));
    }

    [Fact]
    public async Task StringColumnUsesArena()
    {
        Schema schema = new([
            new ColumnInfo("name", DataKind.String, false),
        ]);

        List<Row> rows =
        [
            MakeRow(("name", DataValue.FromString("Alice"))),
            MakeRow(("name", DataValue.FromString("Bob"))),
            MakeRow(("name", DataValue.FromString("Charlie"))),
        ];

        using ColumnBatch batch = await WriteAndReadAsColumnBatch("str.datum", schema, rows);

        Assert.Equal(3, batch.RowCount);

        // Arena-backed: IsArenaBacked should be true.
        DataValue first = batch.GetValue(0, 0);
        Assert.True(first.IsArenaBacked);

        // MaterializeString returns the actual string.
        Assert.Equal("Alice", batch.MaterializeString(0, 0));
        Assert.Equal("Bob", batch.MaterializeString(1, 0));
        Assert.Equal("Charlie", batch.MaterializeString(2, 0));
    }

    [Fact]
    public async Task StringBytesAccessAvoidsManagedAllocation()
    {
        Schema schema = new([
            new ColumnInfo("text", DataKind.String, false),
        ]);

        List<Row> rows =
        [
            MakeRow(("text", DataValue.FromString("hello"))),
        ];

        using ColumnBatch batch = await WriteAndReadAsColumnBatch("strbytes.datum", schema, rows);

        ReadOnlySpan<byte> bytes = batch.GetStringBytes(0, 0);
        Assert.Equal(5, bytes.Length);
        Assert.Equal("hello"u8.ToArray(), bytes.ToArray());
    }

    [Fact]
    public async Task NullsPreservedInColumnBatch()
    {
        Schema schema = new([
            new ColumnInfo("value", DataKind.Float32, true),
            new ColumnInfo("label", DataKind.String, true),
        ]);

        List<Row> rows =
        [
            MakeRow(("value", DataValue.FromFloat32(1.5f)), ("label", DataValue.FromString("ok"))),
            MakeRow(("value", DataValue.Null(DataKind.Float32)), ("label", DataValue.Null(DataKind.String))),
            MakeRow(("value", DataValue.FromFloat32(3.0f)), ("label", DataValue.FromString("done"))),
        ];

        using ColumnBatch batch = await WriteAndReadAsColumnBatch("nulls.datum", schema, rows);

        Assert.Equal(3, batch.RowCount);

        Assert.False(batch.GetValue(0, 0).IsNull);
        Assert.True(batch.GetValue(1, 0).IsNull);
        Assert.False(batch.GetValue(2, 0).IsNull);

        Assert.True(batch.GetValue(1, 1).IsNull);
        Assert.Equal("done", batch.MaterializeString(2, 1));
    }

    [Fact]
    public async Task BooleanColumnRoundTrips()
    {
        Schema schema = new([new ColumnInfo("flag", DataKind.Boolean, false)]);

        List<Row> rows =
        [
            MakeRow(("flag", DataValue.FromBoolean(true))),
            MakeRow(("flag", DataValue.FromBoolean(false))),
            MakeRow(("flag", DataValue.FromBoolean(true))),
        ];

        using ColumnBatch batch = await WriteAndReadAsColumnBatch("bool.datum", schema, rows);

        Assert.True(batch.GetValue(0, 0).AsBoolean());
        Assert.False(batch.GetValue(1, 0).AsBoolean());
        Assert.True(batch.GetValue(2, 0).AsBoolean());
    }

    [Fact]
    public async Task ScalarColumnRoundTrips()
    {
        Schema schema = new([new ColumnInfo("score", DataKind.Float32, false)]);

        List<Row> rows =
        [
            MakeRow(("score", DataValue.FromFloat32(1.5f))),
            MakeRow(("score", DataValue.FromFloat32(-99.5f))),
            MakeRow(("score", DataValue.FromFloat32(0f))),
        ];

        using ColumnBatch batch = await WriteAndReadAsColumnBatch("scalar.datum", schema, rows);

        Assert.Equal(1.5f, batch.GetValue(0, 0).AsFloat32(), 0.0001f);
        Assert.Equal(-99.5f, batch.GetValue(1, 0).AsFloat32(), 0.0001f);
        Assert.Equal(0f, batch.GetValue(2, 0).AsFloat32(), 0.0001f);
    }

    [Fact]
    public async Task GetRowMaterialisesArenaBackedStrings()
    {
        Schema schema = new([
            new ColumnInfo("id", DataKind.Int32, false),
            new ColumnInfo("name", DataKind.String, false),
        ]);

        List<Row> rows =
        [
            MakeRow(("id", DataValue.FromInt32(1)), ("name", DataValue.FromString("one"))),
            MakeRow(("id", DataValue.FromInt32(2)), ("name", DataValue.FromString("two"))),
        ];

        using ColumnBatch batch = await WriteAndReadAsColumnBatch("getrow.datum", schema, rows);

        Row row = batch.GetRow(0);
        Assert.Equal(DataValue.FromInt32(1), row[0]);
        Assert.Equal("one", row[1].AsString());

        Row row2 = batch.GetRow(1);
        Assert.Equal("two", row2[1].AsString());
    }

    [Fact]
    public async Task MatchesRowBasedDecode()
    {
        Schema schema = new([
            new ColumnInfo("id", DataKind.Int32, false),
            new ColumnInfo("name", DataKind.String, false),
            new ColumnInfo("score", DataKind.Float32, true),
        ]);

        List<Row> rows =
        [
            MakeRow(("id", DataValue.FromInt32(10)), ("name", DataValue.FromString("alpha")), ("score", DataValue.FromFloat32(0.9f))),
            MakeRow(("id", DataValue.FromInt32(20)), ("name", DataValue.FromString("beta")), ("score", DataValue.Null(DataKind.Float32))),
            MakeRow(("id", DataValue.FromInt32(30)), ("name", DataValue.FromString("gamma")), ("score", DataValue.FromFloat32(0.1f))),
        ];

        string path = Path.Combine(_tempDirectory, "match.datum");
        await WriteFile(path, schema, rows);

        int[] allIndices = [0, 1, 2];
        string[] names = ["id", "name", "score"];
        Dictionary<string, int> nameIndex = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = 0, ["name"] = 1, ["score"] = 2
        };

        using DatumFileReader reader = DatumFileReader.Open(path);

        // Old path: row-based decode.
        DataValue[][] rowBased = reader.ReadColumns(0, allIndices);

        // New path: column-batch decode.
        using ColumnBatch columnBatch = reader.ReadColumnsAsColumnBatch(0, allIndices, names, nameIndex);

        Assert.Equal(rowBased[0].Length, columnBatch.RowCount);

        for (int row = 0; row < columnBatch.RowCount; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                DataValue expected = rowBased[column][row];
                DataValue actual = columnBatch.GetValue(row, column);

                if (expected.Kind is DataKind.String or DataKind.JsonValue)
                {
                    // Arena-backed strings aren't directly equal; compare materialised values.
                    Assert.Equal(expected.AsString(), columnBatch.MaterializeString(row, column));
                }
                else
                {
                    Assert.Equal(expected, actual);
                }
            }
        }
    }

    // ───────────────────────── Helpers ─────────────────────────

    private async Task<ColumnBatch> WriteAndReadAsColumnBatch(string fileName, Schema schema, List<Row> rows)
    {
        string path = Path.Combine(_tempDirectory, fileName);
        await WriteFile(path, schema, rows);

        using DatumFileReader reader = DatumFileReader.Open(path);
        int[] allIndices = Enumerable.Range(0, schema.Columns.Count).ToArray();
        string[] names = schema.Columns.Select(c => c.Name).ToArray();
        Dictionary<string, int> nameIndex = new(names.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Length; i++)
        {
            nameIndex[names[i]] = i;
        }

        return reader.ReadColumnsAsColumnBatch(0, allIndices, names, nameIndex);
    }

    private static async Task WriteFile(string path, Schema schema, IEnumerable<Row> rows)
    {
        await using DatumOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        foreach (Row row in rows)
        {
            await writer.WriteRowAsync(row);
        }

        await writer.FinalizeAsync();
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = new string[columns.Length];
        DataValue[] values = new DataValue[columns.Length];
        for (int index = 0; index < columns.Length; index++)
        {
            names[index] = columns[index].Name;
            values[index] = columns[index].Value;
        }

        return new Row(names, values);
    }
}

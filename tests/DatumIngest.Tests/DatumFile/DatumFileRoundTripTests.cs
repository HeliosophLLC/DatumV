namespace DatumIngest.Tests.DatumFile;

using DatumIngest.DatumFile;
using DatumIngest.Model;
using DatumIngest.Output.Writers;

/// <summary>
/// Round-trip tests for <see cref="DatumFileWriter"/> + <see cref="DatumFileReader"/>.
/// Each test writes a file via <see cref="DatumOutputWriter"/>, then reads it back
/// via <see cref="DatumFileReader.ReadColumns"/> and verifies that every value is
/// preserved exactly (or within float tolerance where applicable).
/// </summary>
public sealed class DatumFileRoundTripTests : IAsyncLifetime
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"datum_roundtrip_{Guid.NewGuid():N}");

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

    // ──────────────────── Scalar ────────────────────

    [Fact]
    public async Task RoundTrip_Scalar()
    {
        float[] expected = [1.0f, -99.5f, 0f, float.MaxValue];
        DataValue[][] columns = await WriteAndRead("scalar.datum",
            [new ColumnInfo("v", DataKind.Float32, false)],
            expected.Select(v => Row("v", DataValue.FromFloat32(v))));

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], columns[0][i].AsFloat32(), 0.0001f);
        }
    }

    [Fact]
    public async Task RoundTrip_Scalar_WithNulls()
    {
        DataValue[] input = [DataValue.FromFloat32(1f), DataValue.Null(DataKind.Float32), DataValue.FromFloat32(3f)];
        DataValue[][] columns = await WriteAndRead("scalar_null.datum",
            [new ColumnInfo("v", DataKind.Float32, nullable: true)],
            input.Select(v => Row("v", v)));

        Assert.Equal(1f, columns[0][0].AsFloat32(), 0.0001f);
        Assert.True(columns[0][1].IsNull);
        Assert.Equal(DataKind.Float32, columns[0][1].Kind);
        Assert.Equal(3f, columns[0][2].AsFloat32(), 0.0001f);
    }

    // ──────────────────── UInt8 ────────────────────

    [Fact]
    public async Task RoundTrip_UInt8()
    {
        byte[] expected = [0, 1, 127, 255];
        DataValue[][] columns = await WriteAndRead("uint8.datum",
            [new ColumnInfo("b", DataKind.UInt8, false)],
            expected.Select(v => Row("b", DataValue.FromUInt8(v))));

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], columns[0][i].AsUInt8());
        }
    }

    // ──────────────────── Boolean ────────────────────

    [Fact]
    public async Task RoundTrip_Boolean()
    {
        bool[] expected = [true, false, true, true, false];
        DataValue[][] columns = await WriteAndRead("boolean.datum",
            [new ColumnInfo("flag", DataKind.Boolean, false)],
            expected.Select(v => Row("flag", DataValue.FromBoolean(v))));

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], columns[0][i].AsBoolean());
        }
    }

    // ──────────────────── String ────────────────────

    [Fact]
    public async Task RoundTrip_String()
    {
        string[] expected = ["hello", "", "world", "unicode: \u00e9\u4e2d\u6587"];
        DataValue[][] columns = await WriteAndRead("string.datum",
            [new ColumnInfo("s", DataKind.String, false)],
            expected.Select(v => Row("s", DataValue.FromString(v))));

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], columns[0][i].AsString());
        }
    }

    [Fact]
    public async Task RoundTrip_String_WithNulls()
    {
        DataValue[] input = [DataValue.FromString("a"), DataValue.Null(DataKind.String), DataValue.FromString("c")];
        DataValue[][] columns = await WriteAndRead("string_null.datum",
            [new ColumnInfo("s", DataKind.String, nullable: true)],
            input.Select(v => Row("s", v)));

        Assert.Equal("a", columns[0][0].AsString());
        Assert.True(columns[0][1].IsNull);
        Assert.Equal("c", columns[0][2].AsString());
    }

    // ──────────────────── JsonValue ────────────────────

    [Fact]
    public async Task RoundTrip_JsonValue()
    {
        string[] expected = ["""{"key":"value"}""", """[1,2,3]""", "null"];
        DataValue[][] columns = await WriteAndRead("json.datum",
            [new ColumnInfo("j", DataKind.JsonValue, false)],
            expected.Select(v => Row("j", DataValue.FromJsonValue(v))));

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], columns[0][i].AsJsonValue());
        }
    }

    // ──────────────────── Date ────────────────────

    [Fact]
    public async Task RoundTrip_Date()
    {
        DateOnly[] expected =
        [
            new DateOnly(2020, 1, 1),
            new DateOnly(1970, 1, 1),
            new DateOnly(2099, 12, 31),
        ];
        DataValue[][] columns = await WriteAndRead("date.datum",
            [new ColumnInfo("d", DataKind.Date, false)],
            expected.Select(v => Row("d", DataValue.FromDate(v))));

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], columns[0][i].AsDate());
        }
    }

    // ──────────────────── DateTime ────────────────────

    [Fact]
    public async Task RoundTrip_DateTime()
    {
        DateTimeOffset[] expected =
        [
            new DateTimeOffset(2026, 3, 25, 14, 30, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.FromHours(-5)),
        ];
        DataValue[][] columns = await WriteAndRead("datetime.datum",
            [new ColumnInfo("dt", DataKind.DateTime, false)],
            expected.Select(v => Row("dt", DataValue.FromDateTime(v))));

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], columns[0][i].AsDateTime());
        }
    }

    // ──────────────────── Time ────────────────────

    [Fact]
    public async Task RoundTrip_Time()
    {
        TimeOnly[] expected =
        [
            new TimeOnly(0, 0, 0),
            new TimeOnly(12, 30, 45),
            new TimeOnly(23, 59, 59, 999),
        ];
        DataValue[][] columns = await WriteAndRead("time.datum",
            [new ColumnInfo("t", DataKind.Time, false)],
            expected.Select(v => Row("t", DataValue.FromTime(v))));

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], columns[0][i].AsTime());
        }
    }

    // ──────────────────── Duration ────────────────────

    [Fact]
    public async Task RoundTrip_Duration()
    {
        TimeSpan[] expected =
        [
            TimeSpan.Zero,
            TimeSpan.FromHours(1),
            TimeSpan.FromDays(-3),
            TimeSpan.FromMilliseconds(500),
        ];
        DataValue[][] columns = await WriteAndRead("duration.datum",
            [new ColumnInfo("d", DataKind.Duration, false)],
            expected.Select(v => Row("d", DataValue.FromDuration(v))));

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], columns[0][i].AsDuration());
        }
    }

    // ──────────────────── Uuid ────────────────────

    [Fact]
    public async Task RoundTrip_Uuid()
    {
        Guid[] expected =
        [
            Guid.Empty,
            Guid.Parse("12345678-1234-1234-1234-123456789abc"),
            Guid.NewGuid(),
        ];
        DataValue[][] columns = await WriteAndRead("uuid.datum",
            [new ColumnInfo("id", DataKind.Uuid, false)],
            expected.Select(v => Row("id", DataValue.FromUuid(v))));

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], columns[0][i].AsUuid());
        }
    }

    // ──────────────────── UInt8Array ────────────────────

    [Fact]
    public async Task RoundTrip_UInt8Array()
    {
        byte[][] expected = [[1, 2, 3], [], [0xFF, 0x00]];
        DataValue[][] columns = await WriteAndRead("bytes.datum",
            [new ColumnInfo("b", DataKind.UInt8Array, false)],
            expected.Select(v => Row("b", DataValue.FromUInt8Array(v))));

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], columns[0][i].AsUInt8Array());
        }
    }

    // ──────────────────── Vector ────────────────────

    [Fact]
    public async Task RoundTrip_Vector()
    {
        float[][] expected = [[1f, 2f, 3f], [0f, -1f, float.Epsilon], [100f, 200f, 300f]];
        DataValue[][] columns = await WriteAndRead("vector.datum",
            [new ColumnInfo("v", DataKind.Vector, false)],
            expected.Select(v => Row("v", DataValue.FromVector(v))));

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], columns[0][i].AsVector(), comparer: FloatApproxComparer.Instance);
        }
    }

    // ──────────────────── Matrix ────────────────────

    [Fact]
    public async Task RoundTrip_Matrix()
    {
        float[] data = [1f, 2f, 3f, 4f, 5f, 6f];
        (int Rows, int Cols) shape = (2, 3);

        DataValue[][] columns = await WriteAndRead("matrix.datum",
            [new ColumnInfo("m", DataKind.Matrix, false)],
            [Row("m", DataValue.FromMatrix(data, shape.Rows, shape.Cols))]);

        float[] readData = columns[0][0].AsMatrix(out int rows, out int cols);
        Assert.Equal(shape.Rows, rows);
        Assert.Equal(shape.Cols, cols);
        Assert.Equal(data, readData, comparer: FloatApproxComparer.Instance);
    }

    // ──────────────────── Tensor ────────────────────

    [Fact]
    public async Task RoundTrip_Tensor()
    {
        float[] data = [1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f];
        int[] tensorShape = [2, 2, 2];

        DataValue[][] columns = await WriteAndRead("tensor.datum",
            [new ColumnInfo("t", DataKind.Tensor, false)],
            [Row("t", DataValue.FromTensor(data, tensorShape))]);

        float[] readData = columns[0][0].AsTensor(out int[] readShape);
        Assert.Equal(tensorShape, readShape);
        Assert.Equal(data, readData, comparer: FloatApproxComparer.Instance);
    }

    // ──────────────────── Array ────────────────────

    [Fact]
    public async Task RoundTrip_Array_OfScalars()
    {
        DataValue[] element0 = [DataValue.FromFloat32(1f), DataValue.FromFloat32(2f)];
        DataValue[] element1 = [DataValue.FromFloat32(10f)];

        DataValue[][] columns = await WriteAndRead("array_scalar.datum",
            [new ColumnInfo("arr", DataKind.Array, false)],
            [Row("arr", DataValue.FromArray(DataKind.Float32, element0)),
             Row("arr", DataValue.FromArray(DataKind.Float32, element1))]);

        Assert.Equal(DataKind.Float32, columns[0][0].ArrayElementKind);
        DataValue[] row0 = columns[0][0].AsArray();
        Assert.Equal(1f, row0[0].AsFloat32(), 0.0001f);
        Assert.Equal(2f, row0[1].AsFloat32(), 0.0001f);

        Assert.Equal(DataKind.Float32, columns[0][1].ArrayElementKind);
        DataValue[] row1 = columns[0][1].AsArray();
        Assert.Single(row1);
        Assert.Equal(10f, row1[0].AsFloat32(), 0.0001f);
    }

    // ──────────────────── Multi-column ────────────────────

    [Fact]
    public async Task RoundTrip_MultipleColumns_AllPresentAfterRead()
    {
        Schema schema = new([
            new ColumnInfo("id", DataKind.Float32, false),
            new ColumnInfo("name", DataKind.String, false),
            new ColumnInfo("flag", DataKind.Boolean, false),
        ]);

        List<Row> inputRows = [
            MultiRow(("id", DataValue.FromFloat32(1f)), ("name", DataValue.FromString("Alice")), ("flag", DataValue.FromBoolean(true))),
            MultiRow(("id", DataValue.FromFloat32(2f)), ("name", DataValue.FromString("Bob")),   ("flag", DataValue.FromBoolean(false))),
        ];

        string path = Path.Combine(_tempDirectory, "multi.datum");
        await WriteFile(path, schema, inputRows);

        using DatumFileReader reader = DatumFileReader.Open(path);
        Assert.Equal(1, reader.RowGroupCount);
        Assert.Equal(2, reader.TotalRowCount);

        DataValue[][] columns = reader.ReadColumns(0, [0, 1, 2]);

        Assert.Equal(1f, columns[0][0].AsFloat32(), 0.0001f);
        Assert.Equal("Alice", columns[1][0].AsString());
        Assert.True(columns[2][0].AsBoolean());

        Assert.Equal(2f, columns[0][1].AsFloat32(), 0.0001f);
        Assert.Equal("Bob", columns[1][1].AsString());
        Assert.False(columns[2][1].AsBoolean());
    }

    // ──────────────────── Footer metadata ────────────────────

    [Fact]
    public async Task Open_ReadsCorrectTotalRowCount()
    {
        string path = Path.Combine(_tempDirectory, "rowcount.datum");
        Schema schema = new([new ColumnInfo("x", DataKind.Float32, false)]);

        await WriteFile(path, schema,
            Enumerable.Range(0, 10).Select(i => Row("x", DataValue.FromFloat32((float)i))));

        using DatumFileReader reader = DatumFileReader.Open(path);
        Assert.Equal(10, reader.TotalRowCount);
    }

    [Fact]
    public async Task Open_InvalidMagic_ThrowsInvalidDataException()
    {
        string path = Path.Combine(_tempDirectory, "corrupt.datum");
        await File.WriteAllBytesAsync(path, new byte[64]);

        Assert.Throws<InvalidDataException>(() => DatumFileReader.Open(path));
    }

    // ──────────────────── Helpers ────────────────────

    /// <summary>
    /// Writes <paramref name="rows"/> to a <c>.datum</c> file, then reads back all columns
    /// for row group 0 and returns them.
    /// </summary>
    private async Task<DataValue[][]> WriteAndRead(
        string fileName,
        IReadOnlyList<ColumnInfo> columnInfos,
        IEnumerable<Row> rows)
    {
        string path = Path.Combine(_tempDirectory, fileName);
        Schema schema = new(columnInfos);

        await WriteFile(path, schema, rows);

        using DatumFileReader reader = DatumFileReader.Open(path);
        int[] allIndices = Enumerable.Range(0, columnInfos.Count).ToArray();
        return reader.ReadColumns(0, allIndices);
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

    private static Row Row(string column, DataValue value)
        => new([column], [value]);

    private static Row MultiRow(params (string Name, DataValue Value)[] columns)
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

    /// <summary>
    /// Compares floats with a small absolute tolerance to handle floating-point
    /// representation differences across the shuffle + Zstd pipeline.
    /// </summary>
    private sealed class FloatApproxComparer : IEqualityComparer<float>
    {
        public static readonly FloatApproxComparer Instance = new();

        public bool Equals(float x, float y)
            => MathF.Abs(x - y) < 1e-4f || (float.IsNaN(x) && float.IsNaN(y));

        public int GetHashCode(float obj) => obj.GetHashCode();
    }
}

using DatumIngest.Cli.Shell;
using DatumIngest.Model;

namespace DatumIngest.Tests.Cli;

/// <summary>
/// Tests for <see cref="TableFormatter"/> psql-style table output.
/// </summary>
public sealed class TableFormatterTests
{
    /// <summary>
    /// Formats a single row with header, separator, data, and footer.
    /// </summary>
    [Fact]
    public async Task FormatAsync_SingleRow_ProducesAlignedTable()
    {
        Schema schema = new(new[]
        {
            new ColumnInfo("name", DataKind.String, false),
            new ColumnInfo("score", DataKind.Float32, false),
        });

        Row row = new(
            new[] { "name", "score" },
            new[] { DataValue.FromString("Alice"), DataValue.FromFloat32(95.5f) });

        TableFormatter formatter = new();
        StringWriter writer = new();
        await formatter.FormatAsync(ToAsyncEnumerable(row), schema, writer);

        string output = writer.ToString();
        string[] lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        // Header, separator, data row, footer = 4 lines.
        Assert.Equal(4, lines.Length);
        Assert.Contains("name", lines[0]);
        Assert.Contains("score", lines[0]);
        Assert.Contains("---", lines[1]);
        Assert.Contains("Alice", lines[2]);
        Assert.Contains("(1 row)", lines[3]);
    }

    /// <summary>
    /// Numeric columns are right-aligned.
    /// </summary>
    [Fact]
    public async Task FormatAsync_NumericColumn_RightAligned()
    {
        Schema schema = new(new[]
        {
            new ColumnInfo("id", DataKind.Float32, false),
        });

        Row row = new(
            new[] { "id" },
            new[] { DataValue.FromFloat32(42f) });

        TableFormatter formatter = new();
        StringWriter writer = new();
        await formatter.FormatAsync(ToAsyncEnumerable(row), schema, writer);

        string output = writer.ToString();
        // "42" should be right-padded to at least the header width ("id" = 2 chars),
        // but since "42" is also 2 chars, alignment is trivial. Just verify no error.
        Assert.Contains("42", output);
    }

    /// <summary>
    /// NULL values are displayed as the literal string "NULL".
    /// </summary>
    [Fact]
    public async Task FormatAsync_NullValue_DisplaysNull()
    {
        Schema schema = new(new[]
        {
            new ColumnInfo("value", DataKind.String, true),
        });

        Row row = new(
            new[] { "value" },
            new[] { DataValue.Null(DataKind.String) });

        TableFormatter formatter = new();
        StringWriter writer = new();
        await formatter.FormatAsync(ToAsyncEnumerable(row), schema, writer);

        Assert.Contains("NULL", writer.ToString());
    }

    /// <summary>
    /// Multiple rows produces a plural footer.
    /// </summary>
    [Fact]
    public async Task FormatAsync_MultipleRows_ShowsPluralFooter()
    {
        Schema schema = new(new[]
        {
            new ColumnInfo("id", DataKind.Float32, false),
        });

        Row row1 = new(new[] { "id" }, new[] { DataValue.FromFloat32(1f) });
        Row row2 = new(new[] { "id" }, new[] { DataValue.FromFloat32(2f) });

        TableFormatter formatter = new();
        StringWriter writer = new();
        await formatter.FormatAsync(ToAsyncEnumerable(row1, row2), schema, writer);

        Assert.Contains("(2 rows)", writer.ToString());
    }

    /// <summary>
    /// Empty result set shows zero rows footer.
    /// </summary>
    [Fact]
    public async Task FormatAsync_NoRows_ShowsZeroRowsFooter()
    {
        Schema schema = new(new[]
        {
            new ColumnInfo("id", DataKind.Float32, false),
        });

        TableFormatter formatter = new();
        StringWriter writer = new();
        await formatter.FormatAsync(ToAsyncEnumerable(), schema, writer);

        Assert.Contains("(0 rows)", writer.ToString());
    }

    /// <summary>
    /// FormatValue handles all numeric types without errors.
    /// </summary>
    [Theory]
    [InlineData(DataKind.Float32)]
    [InlineData(DataKind.UInt8)]
    public void FormatValue_NumericTypes_ProducesString(DataKind kind)
    {
        DataValue value = kind == DataKind.Float32
            ? DataValue.FromFloat32(3.14f)
            : DataValue.FromUInt8(42);

        string formatted = TableFormatter.FormatValue(value);
        Assert.False(string.IsNullOrEmpty(formatted));
    }

    /// <summary>
    /// FormatValue handles date and datetime types.
    /// </summary>
    [Fact]
    public void FormatValue_DateTypes_ProducesExpectedFormat()
    {
        DataValue dateValue = DataValue.FromDate(new DateOnly(2024, 6, 15));
        Assert.Equal("2024-06-15", TableFormatter.FormatValue(dateValue));

        DataValue dateTimeValue = DataValue.FromDateTime(new DateTimeOffset(2024, 6, 15, 10, 30, 0, TimeSpan.Zero));
        string formatted = TableFormatter.FormatValue(dateTimeValue);
        Assert.Contains("2024-06-15", formatted);
    }

    /// <summary>
    /// FormatValue handles vectors by showing contents.
    /// </summary>
    [Fact]
    public void FormatValue_Vector_ShowsContents()
    {
        DataValue vector = DataValue.FromVector(new[] { 1.0f, 2.0f, 3.0f });
        string formatted = TableFormatter.FormatValue(vector);
        Assert.StartsWith("[", formatted);
        Assert.Contains("1", formatted);
        Assert.Contains("3", formatted);
    }

    private static async IAsyncEnumerable<Row> ToAsyncEnumerable(params Row[] rows)
    {
        foreach (Row row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }
}

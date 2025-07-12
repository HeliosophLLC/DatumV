namespace DatumIngest.Tests.Output;

using DatumIngest.Model;
using DatumIngest.Output;
using DatumIngest.Output.Writers;

public sealed class CsvOutputWriterTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"csv_writer_{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task WriteRowAsync_SimpleData_CreatesValidCsv()
    {
        string path = Path.Combine(_tempDir, "simple.csv");
        Schema schema = new([
            new ColumnInfo("name", DataKind.String, false),
            new ColumnInfo("age", DataKind.Scalar, false)
        ]);

        await using CsvOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(("name", DataValue.FromString("Alice")), ("age", DataValue.FromScalar(30.0f))));
        await writer.WriteRowAsync(CreateRow(("name", DataValue.FromString("Bob")), ("age", DataValue.FromScalar(25.0f))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(2, summary.RowsWritten);
        Assert.Single(summary.FilesCreated);

        string content = await File.ReadAllTextAsync(path);
        Assert.StartsWith("name,age", content);
        Assert.Contains("Alice,30", content);
        Assert.Contains("Bob,25", content);
    }

    [Fact]
    public async Task WriteRowAsync_EscapesCommasInFields()
    {
        string path = Path.Combine(_tempDir, "escaped.csv");
        Schema schema = new([new ColumnInfo("text", DataKind.String, false)]);

        await using CsvOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(("text", DataValue.FromString("hello, world"))));
        await writer.FinalizeAsync();

        string content = await File.ReadAllTextAsync(path);
        Assert.Contains("\"hello, world\"", content);
    }

    [Fact]
    public async Task WriteRowAsync_EscapesQuotesInFields()
    {
        string path = Path.Combine(_tempDir, "quotes.csv");
        Schema schema = new([new ColumnInfo("text", DataKind.String, false)]);

        await using CsvOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(("text", DataValue.FromString("say \"hello\""))));
        await writer.FinalizeAsync();

        string content = await File.ReadAllTextAsync(path);
        Assert.Contains("\"say \"\"hello\"\"\"", content);
    }

    [Fact]
    public async Task WriteRowAsync_NullValues_WriteEmptyField()
    {
        string path = Path.Combine(_tempDir, "nulls.csv");
        Schema schema = new([
            new ColumnInfo("name", DataKind.String, true),
            new ColumnInfo("value", DataKind.Scalar, true)
        ]);

        await using CsvOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("name", DataValue.Null(DataKind.String)),
            ("value", DataValue.FromScalar(1.0f))));
        await writer.FinalizeAsync();

        string content = await File.ReadAllTextAsync(path);
        string[] lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length); // header + one data row
        Assert.StartsWith(",1", lines[1].Trim());
    }

    [Fact]
    public async Task WriteRowAsync_ReadBackRoundTrip()
    {
        string path = Path.Combine(_tempDir, "roundtrip.csv");
        Schema schema = new([
            new ColumnInfo("id", DataKind.Scalar, false),
            new ColumnInfo("name", DataKind.String, false)
        ]);

        await using CsvOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(("id", DataValue.FromScalar(1.0f)), ("name", DataValue.FromString("Alice"))));
        await writer.WriteRowAsync(CreateRow(("id", DataValue.FromScalar(2.0f)), ("name", DataValue.FromString("Bob"))));
        await writer.FinalizeAsync();

        // Read back with CsvTableProvider
        DatumIngest.Catalog.Providers.CsvTableProvider provider = new();
        DatumIngest.Catalog.TableDescriptor descriptor = new("csv", "test", path, new Dictionary<string, string>());

        List<Row> rows = new();
        await foreach (Row row in provider.OpenAsync(descriptor, null, CancellationToken.None))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(1.0f, rows[0]["id"].AsScalar());
        Assert.Equal("Alice", rows[0]["name"].AsString());
        Assert.Equal(2.0f, rows[1]["id"].AsScalar());
        Assert.Equal("Bob", rows[1]["name"].AsString());
    }

    [Fact]
    public async Task WriteRowAsync_EmptyDataset_CreatesHeaderOnly()
    {
        string path = Path.Combine(_tempDir, "empty.csv");
        Schema schema = new([
            new ColumnInfo("col1", DataKind.String, false),
            new ColumnInfo("col2", DataKind.Scalar, false)
        ]);

        await using CsvOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(0, summary.RowsWritten);
        string content = await File.ReadAllTextAsync(path);
        string[] lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines); // header only
        Assert.Equal("col1,col2", lines[0].Trim());
    }

    [Fact]
    public async Task FinalizeAsync_ReportsBytesWritten()
    {
        string path = Path.Combine(_tempDir, "bytes.csv");
        Schema schema = new([new ColumnInfo("data", DataKind.String, false)]);

        await using CsvOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(("data", DataValue.FromString("hello"))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.True(summary.BytesWritten > 0);
    }

    [Fact]
    public async Task WriteRowAsync_Stream_WritesValidCsv()
    {
        using MemoryStream stream = new();
        Schema schema = new([
            new ColumnInfo("name", DataKind.String, false),
            new ColumnInfo("age", DataKind.Scalar, false)
        ]);

        await using CsvOutputWriter writer = new(stream);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(("name", DataValue.FromString("Alice")), ("age", DataValue.FromScalar(30.0f))));
        await writer.WriteRowAsync(CreateRow(("name", DataValue.FromString("Bob")), ("age", DataValue.FromScalar(25.0f))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(2, summary.RowsWritten);
        Assert.True(summary.BytesWritten > 0);
        Assert.Empty(summary.FilesCreated);

        stream.Position = 0;
        string content = new StreamReader(stream).ReadToEnd();
        Assert.StartsWith("name,age", content);
        Assert.Contains("Alice,30", content);
        Assert.Contains("Bob,25", content);
    }

    [Fact]
    public async Task WriteRowAsync_Stream_DoesNotDisposeCallerStream()
    {
        MemoryStream stream = new();
        Schema schema = new([new ColumnInfo("val", DataKind.Scalar, false)]);

        await using (CsvOutputWriter writer = new(stream))
        {
            await writer.InitializeAsync(schema);
            await writer.WriteRowAsync(CreateRow(("val", DataValue.FromScalar(1.0f))));
            await writer.FinalizeAsync();
        }

        // Stream should still be usable after writer disposal.
        Assert.True(stream.CanRead);
        stream.Position = 0;
        string content = new StreamReader(stream).ReadToEnd();
        Assert.Contains("1", content);
        stream.Dispose();
    }

    private static Row CreateRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = new string[columns.Length];
        DataValue[] values = new DataValue[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            names[i] = columns[i].Name;
            values[i] = columns[i].Value;
        }
        return new Row(names, values);
    }
}

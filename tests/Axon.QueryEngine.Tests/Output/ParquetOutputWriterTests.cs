namespace Axon.QueryEngine.Tests.Output;

using Axon.QueryEngine.Model;
using Axon.QueryEngine.Output;
using Axon.QueryEngine.Output.Writers;
using Parquet;
using Parquet.Schema;

public sealed class ParquetOutputWriterTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"parquet_writer_{Guid.NewGuid():N}");

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
    public async Task FinalizeAsync_ScalarAndString_CreatesValidParquet()
    {
        string path = Path.Combine(_tempDir, "simple.parquet");
        Schema schema = new([
            new ColumnInfo("id", DataKind.Scalar, false),
            new ColumnInfo("name", DataKind.String, false)
        ]);

        await using ParquetOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromScalar(1.0f)),
            ("name", DataValue.FromString("Alice"))));
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromScalar(2.0f)),
            ("name", DataValue.FromString("Bob"))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(2, summary.RowsWritten);
        Assert.True(summary.BytesWritten > 0);

        // Read back
        using FileStream stream = File.OpenRead(path);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);

        Assert.Equal(2, reader.Schema.DataFields.Length);
        Assert.Equal("id", reader.Schema.DataFields[0].Name);
        Assert.Equal("name", reader.Schema.DataFields[1].Name);

        using Parquet.ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        Parquet.Data.DataColumn idColumn = await rowGroup.ReadColumnAsync(reader.Schema.DataFields[0]);
        Parquet.Data.DataColumn nameColumn = await rowGroup.ReadColumnAsync(reader.Schema.DataFields[1]);

        float[] ids = (float[])idColumn.Data;
        string[] names = (string[])nameColumn.Data;
        Assert.Equal([1.0f, 2.0f], ids);
        Assert.Equal(["Alice", "Bob"], names);
    }

    [Fact]
    public async Task FinalizeAsync_EmptyDataset_CreatesValidFile()
    {
        string path = Path.Combine(_tempDir, "empty.parquet");
        Schema schema = new([new ColumnInfo("val", DataKind.Scalar, false)]);

        await using ParquetOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(0, summary.RowsWritten);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task FinalizeAsync_UInt8Column_WritesAsInt()
    {
        string path = Path.Combine(_tempDir, "uint8.parquet");
        Schema schema = new([new ColumnInfo("byte_val", DataKind.UInt8, false)]);

        await using ParquetOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(("byte_val", DataValue.FromUInt8(42))));
        await writer.WriteRowAsync(CreateRow(("byte_val", DataValue.FromUInt8(255))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(2, summary.RowsWritten);

        using FileStream stream = File.OpenRead(path);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        using Parquet.ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        Parquet.Data.DataColumn column = await rowGroup.ReadColumnAsync(reader.Schema.DataFields[0]);

        int[] data = (int[])column.Data;
        Assert.Equal([42, 255], data);
    }

    [Fact]
    public async Task FinalizeAsync_ReadBackRoundTrip()
    {
        string path = Path.Combine(_tempDir, "roundtrip.parquet");
        Schema schema = new([
            new ColumnInfo("score", DataKind.Scalar, false),
            new ColumnInfo("label", DataKind.String, false)
        ]);

        await using ParquetOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("score", DataValue.FromScalar(95.5f)),
            ("label", DataValue.FromString("high"))));
        await writer.WriteRowAsync(CreateRow(
            ("score", DataValue.FromScalar(60.2f)),
            ("label", DataValue.FromString("low"))));
        await writer.FinalizeAsync();

        // Read back with ParquetTableProvider
        Axon.QueryEngine.Catalog.Providers.ParquetTableProvider provider = new();
        Axon.QueryEngine.Catalog.TableDescriptor descriptor = new("parquet", "test", path, new Dictionary<string, string>());

        List<Row> rows = new();
        await foreach (Row row in provider.OpenAsync(descriptor, null, CancellationToken.None))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(95.5f, rows[0]["score"].AsScalar(), 0.01f);
        Assert.Equal("high", rows[0]["label"].AsString());
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

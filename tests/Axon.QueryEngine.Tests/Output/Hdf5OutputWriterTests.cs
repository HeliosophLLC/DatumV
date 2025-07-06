namespace Axon.QueryEngine.Tests.Output;

using Axon.QueryEngine.Model;
using Axon.QueryEngine.Output;
using Axon.QueryEngine.Output.Writers;
using PureHDF;
using PureHDF.VOL.Native;

public sealed class Hdf5OutputWriterTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hdf5_writer_{Guid.NewGuid():N}");

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
    public async Task FinalizeAsync_ScalarColumn_WritesFloatDataset()
    {
        string path = Path.Combine(_tempDir, "scalar.h5");
        Schema schema = new([new ColumnInfo("value", DataKind.Scalar, false)]);

        await using Hdf5OutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(("value", DataValue.FromScalar(1.5f))));
        await writer.WriteRowAsync(CreateRow(("value", DataValue.FromScalar(2.5f))));
        await writer.WriteRowAsync(CreateRow(("value", DataValue.FromScalar(3.5f))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(3, summary.RowsWritten);
        Assert.True(summary.BytesWritten > 0);

        // Read back
        using NativeFile file = H5File.OpenRead(path);
        float[] data = file.Dataset("value").Read<float[]>();
        Assert.Equal([1.5f, 2.5f, 3.5f], data);
    }

    [Fact]
    public async Task FinalizeAsync_StringColumn_WritesStringDataset()
    {
        string path = Path.Combine(_tempDir, "strings.h5");
        Schema schema = new([new ColumnInfo("name", DataKind.String, false)]);

        await using Hdf5OutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(("name", DataValue.FromString("Alice"))));
        await writer.WriteRowAsync(CreateRow(("name", DataValue.FromString("Bob"))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(2, summary.RowsWritten);

        using NativeFile file = H5File.OpenRead(path);
        string[] data = file.Dataset("name").Read<string[]>();
        Assert.Equal(["Alice", "Bob"], data);
    }

    [Fact]
    public async Task FinalizeAsync_UInt8Column_WritesByteDataset()
    {
        string path = Path.Combine(_tempDir, "bytes.h5");
        Schema schema = new([new ColumnInfo("val", DataKind.UInt8, false)]);

        await using Hdf5OutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(("val", DataValue.FromUInt8(10))));
        await writer.WriteRowAsync(CreateRow(("val", DataValue.FromUInt8(20))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(2, summary.RowsWritten);

        using NativeFile file = H5File.OpenRead(path);
        byte[] data = file.Dataset("val").Read<byte[]>();
        Assert.Equal([10, 20], data);
    }

    [Fact]
    public async Task FinalizeAsync_MultipleColumns_WritesAllDatasets()
    {
        string path = Path.Combine(_tempDir, "multi.h5");
        Schema schema = new([
            new ColumnInfo("id", DataKind.Scalar, false),
            new ColumnInfo("name", DataKind.String, false)
        ]);

        await using Hdf5OutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromScalar(1.0f)),
            ("name", DataValue.FromString("Alice"))));
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromScalar(2.0f)),
            ("name", DataValue.FromString("Bob"))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(2, summary.RowsWritten);

        using NativeFile file = H5File.OpenRead(path);
        float[] ids = file.Dataset("id").Read<float[]>();
        string[] names = file.Dataset("name").Read<string[]>();
        Assert.Equal([1.0f, 2.0f], ids);
        Assert.Equal(["Alice", "Bob"], names);
    }

    [Fact]
    public async Task FinalizeAsync_EmptyDataset_WritesEmptyFile()
    {
        string path = Path.Combine(_tempDir, "empty.h5");
        Schema schema = new([new ColumnInfo("val", DataKind.Scalar, false)]);

        await using Hdf5OutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(0, summary.RowsWritten);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task FinalizeAsync_VectorColumn_Writes2DDataset()
    {
        string path = Path.Combine(_tempDir, "vectors.h5");
        Schema schema = new([new ColumnInfo("embedding", DataKind.Vector, false)]);

        await using Hdf5OutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(("embedding", DataValue.FromVector([1.0f, 2.0f, 3.0f]))));
        await writer.WriteRowAsync(CreateRow(("embedding", DataValue.FromVector([4.0f, 5.0f, 6.0f]))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(2, summary.RowsWritten);

        using NativeFile file = H5File.OpenRead(path);
        IH5Dataset dataset = file.Dataset("embedding");
        Assert.Equal(2, (int)dataset.Space.Rank);
        Assert.Equal(2UL, dataset.Space.Dimensions[0]);
        Assert.Equal(3UL, dataset.Space.Dimensions[1]);
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

    [Fact]
    public async Task FinalizeAsync_Stream_ScalarColumn_WritesFloatDataset()
    {
        using MemoryStream stream = new();
        Schema schema = new([new ColumnInfo("value", DataKind.Scalar, false)]);

        await using Hdf5OutputWriter writer = new(stream);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(("value", DataValue.FromScalar(1.5f))));
        await writer.WriteRowAsync(CreateRow(("value", DataValue.FromScalar(2.5f))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(2, summary.RowsWritten);
        Assert.True(summary.BytesWritten > 0);
        Assert.Empty(summary.FilesCreated);

        stream.Position = 0;
        using NativeFile file = H5File.Open(stream, leaveOpen: true);
        float[] data = file.Dataset("value").Read<float[]>();
        Assert.Equal([1.5f, 2.5f], data);
    }

    [Fact]
    public async Task FinalizeAsync_Stream_MultipleColumns_WritesAllDatasets()
    {
        using MemoryStream stream = new();
        Schema schema = new([
            new ColumnInfo("id", DataKind.Scalar, false),
            new ColumnInfo("name", DataKind.String, false)
        ]);

        await using Hdf5OutputWriter writer = new(stream);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromScalar(1.0f)),
            ("name", DataValue.FromString("Alice"))));
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromScalar(2.0f)),
            ("name", DataValue.FromString("Bob"))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(2, summary.RowsWritten);
        Assert.Empty(summary.FilesCreated);

        stream.Position = 0;
        using NativeFile file = H5File.Open(stream, leaveOpen: true);
        float[] ids = file.Dataset("id").Read<float[]>();
        string[] names = file.Dataset("name").Read<string[]>();
        Assert.Equal([1.0f, 2.0f], ids);
        Assert.Equal(["Alice", "Bob"], names);
    }
}

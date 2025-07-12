namespace DatumIngest.Tests.Output;

using DatumIngest.Model;
using DatumIngest.Output;
using DatumIngest.Output.Writers;
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
        DatumIngest.Catalog.Providers.ParquetTableProvider provider = new();
        DatumIngest.Catalog.TableDescriptor descriptor = new("parquet", "test", path, new Dictionary<string, string>());

        List<Row> rows = new();
        await foreach (Row row in provider.OpenAsync(descriptor, null, CancellationToken.None))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(95.5f, rows[0]["score"].AsScalar(), 0.01f);
        Assert.Equal("high", rows[0]["label"].AsString());
    }

    [Fact]
    public async Task FinalizeAsync_BinaryColumn_ExternalizesToImagesFolder()
    {
        string path = Path.Combine(_tempDir, "with_images.parquet");

        // JPEG magic bytes + dummy payload.
        byte[] jpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. new byte[100]];

        Schema schema = new([
            new ColumnInfo("name", DataKind.String, false),
            new ColumnInfo("data", DataKind.UInt8Array, false)
        ]);

        await using ParquetOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("name", DataValue.FromString("photo1")),
            ("data", DataValue.FromUInt8Array(jpegBytes))));
        await writer.WriteRowAsync(CreateRow(
            ("name", DataValue.FromString("photo2")),
            ("data", DataValue.FromUInt8Array(jpegBytes))));
        OutputSummary summary = await writer.FinalizeAsync();

        // Verify images/ directory was created.
        string imagesDir = Path.Combine(_tempDir, "images");
        Assert.True(Directory.Exists(imagesDir));

        // Verify two .jpg files were created.
        string[] imageFiles = Directory.GetFiles(imagesDir, "*.jpg");
        Assert.Equal(2, imageFiles.Length);

        // Verify each file has the correct bytes.
        foreach (string imageFile in imageFiles)
        {
            byte[] written = File.ReadAllBytes(imageFile);
            Assert.Equal(jpegBytes, written);
        }

        // Verify the Parquet column contains relative paths, not raw bytes.
        using FileStream stream = File.OpenRead(path);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        using Parquet.ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        Parquet.Data.DataColumn dataColumn = await rowGroup.ReadColumnAsync(reader.Schema.DataFields[1]);

        string[] paths = (string[])dataColumn.Data;
        Assert.Equal(2, paths.Length);
        Assert.StartsWith("images/", paths[0]);
        Assert.EndsWith(".jpg", paths[0]);

        // Verify summary includes image files.
        Assert.True(summary.FilesCreated.Count > 1);
    }

    [Fact]
    public async Task FinalizeAsync_PngBytes_DetectedCorrectly()
    {
        string path = Path.Combine(_tempDir, "png_images.parquet");

        // PNG magic bytes.
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[50]];

        Schema schema = new([
            new ColumnInfo("image", DataKind.Image, false)
        ]);

        await using ParquetOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("image", DataValue.FromImage(pngBytes))));
        await writer.FinalizeAsync();

        string imagesDir = Path.Combine(_tempDir, "images");
        string[] imageFiles = Directory.GetFiles(imagesDir, "*.png");
        Assert.Single(imageFiles);
    }

    [Fact]
    public async Task FinalizeAsync_UnknownBytes_UsesBinExtension()
    {
        string path = Path.Combine(_tempDir, "binary.parquet");

        byte[] rawBytes = [0x01, 0x02, 0x03, 0x04, 0x05];

        Schema schema = new([
            new ColumnInfo("blob", DataKind.UInt8Array, false)
        ]);

        await using ParquetOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("blob", DataValue.FromUInt8Array(rawBytes))));
        await writer.FinalizeAsync();

        string imagesDir = Path.Combine(_tempDir, "images");
        string[] binFiles = Directory.GetFiles(imagesDir, "*.bin");
        Assert.Single(binFiles);
        Assert.Equal(rawBytes, File.ReadAllBytes(binFiles[0]));
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
    public async Task FinalizeAsync_Stream_ScalarAndString_WritesValidParquet()
    {
        using MemoryStream stream = new();
        Schema schema = new([
            new ColumnInfo("id", DataKind.Scalar, false),
            new ColumnInfo("name", DataKind.String, false)
        ]);

        await using ParquetOutputWriter writer = new(stream);
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
        Assert.Empty(summary.FilesCreated);

        stream.Position = 0;
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        Assert.Equal(2, reader.Schema.DataFields.Length);

        using Parquet.ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        Parquet.Data.DataColumn idColumn = await rowGroup.ReadColumnAsync(reader.Schema.DataFields[0]);
        Parquet.Data.DataColumn nameColumn = await rowGroup.ReadColumnAsync(reader.Schema.DataFields[1]);

        float[] ids = (float[])idColumn.Data;
        string[] names = (string[])nameColumn.Data;
        Assert.Equal([1.0f, 2.0f], ids);
        Assert.Equal(["Alice", "Bob"], names);
    }

    [Fact]
    public async Task FinalizeAsync_Stream_BinaryColumn_EmbedsBytesDirectly()
    {
        byte[] rawBytes = [0x01, 0x02, 0x03, 0x04, 0x05];
        using MemoryStream stream = new();
        Schema schema = new([
            new ColumnInfo("id", DataKind.Scalar, false),
            new ColumnInfo("data", DataKind.UInt8Array, false)
        ]);

        await using ParquetOutputWriter writer = new(stream);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromScalar(1.0f)),
            ("data", DataValue.FromUInt8Array(rawBytes))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(1, summary.RowsWritten);
        Assert.Empty(summary.FilesCreated);

        stream.Position = 0;
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);

        using Parquet.ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        Parquet.Data.DataColumn dataColumn = await rowGroup.ReadColumnAsync(reader.Schema.DataFields[1]);

        byte[][] binaryData = (byte[][])dataColumn.Data;
        Assert.Single(binaryData);
        Assert.Equal(rawBytes, binaryData[0]);
    }
}

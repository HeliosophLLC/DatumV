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
            new ColumnInfo("id", DataKind.Float32, false),
            new ColumnInfo("name", DataKind.String, false)
        ]);

        await using ParquetOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromFloat32(1.0f)),
            ("name", DataValue.FromString("Alice"))));
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromFloat32(2.0f)),
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
        Schema schema = new([new ColumnInfo("val", DataKind.Float32, false)]);

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
            new ColumnInfo("score", DataKind.Float32, false),
            new ColumnInfo("label", DataKind.String, false)
        ]);

        await using ParquetOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("score", DataValue.FromFloat32(95.5f)),
            ("label", DataValue.FromString("high"))));
        await writer.WriteRowAsync(CreateRow(
            ("score", DataValue.FromFloat32(60.2f)),
            ("label", DataValue.FromString("low"))));
        await writer.FinalizeAsync();

        // Read back with ParquetTableProvider
        DatumIngest.Catalog.Providers.ParquetTableProvider provider = new();
        DatumIngest.Catalog.TableDescriptor descriptor = new("parquet", "test", path, new Dictionary<string, string>());

        List<Row> rows = await provider.OpenAsync(descriptor, null, CancellationToken.None).CollectRowsAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(95.5f, rows[0]["score"].AsFloat32(), 0.01f);
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
            new ColumnInfo("id", DataKind.Float32, false),
            new ColumnInfo("name", DataKind.String, false)
        ]);

        await using ParquetOutputWriter writer = new(stream);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromFloat32(1.0f)),
            ("name", DataValue.FromString("Alice"))));
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromFloat32(2.0f)),
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
            new ColumnInfo("id", DataKind.Float32, false),
            new ColumnInfo("data", DataKind.UInt8Array, false)
        ]);

        await using ParquetOutputWriter writer = new(stream);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromFloat32(1.0f)),
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

    [Fact]
    public async Task FinalizeAsync_IntArrayColumn_WritesAsParquetList()
    {
        string path = Path.Combine(_tempDir, "int_array.parquet");
        Schema schema = new([
            new ColumnInfo("id", DataKind.Int32, false),
            new ColumnInfo("scores", DataKind.Array, false, arrayElementKind: DataKind.Int32)
        ]);

        await using ParquetOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromInt32(1)),
            ("scores", DataValue.FromArray(DataKind.Int32, [
                DataValue.FromInt32(10), DataValue.FromInt32(20), DataValue.FromInt32(30)
            ]))));
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromInt32(2)),
            ("scores", DataValue.FromArray(DataKind.Int32, [DataValue.FromInt32(99)]))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(2, summary.RowsWritten);

        // Verify the Parquet schema uses a ListField.
        using FileStream stream = File.OpenRead(path);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);

        Assert.Equal(2, reader.Schema.Fields.Count);
        Assert.IsType<Parquet.Schema.DataField>(reader.Schema.Fields[0]);
        Assert.IsType<Parquet.Schema.ListField>(reader.Schema.Fields[1]);
    }

    [Fact]
    public async Task FinalizeAsync_IntArrayColumn_RoundTripThroughProvider()
    {
        string path = Path.Combine(_tempDir, "int_array_rt.parquet");
        Schema schema = new([
            new ColumnInfo("id", DataKind.Int32, false),
            new ColumnInfo("values", DataKind.Array, false, arrayElementKind: DataKind.Int32)
        ]);

        await using ParquetOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromInt32(1)),
            ("values", DataValue.FromArray(DataKind.Int32, [
                DataValue.FromInt32(10), DataValue.FromInt32(20), DataValue.FromInt32(30)
            ]))));
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromInt32(2)),
            ("values", DataValue.FromArray(DataKind.Int32, [DataValue.FromInt32(99)]))));
        await writer.FinalizeAsync();

        DatumIngest.Catalog.Providers.ParquetTableProvider provider = new();
        DatumIngest.Catalog.TableDescriptor descriptor = new("parquet", "test", path, new Dictionary<string, string>());

        // Verify schema detection.
        Schema readSchema = await provider.GetSchemaAsync(descriptor, CancellationToken.None);
        ColumnInfo arrayColumn = readSchema.Columns.First(c => c.Name == "values");
        Assert.Equal(DataKind.Array, arrayColumn.Kind);
        Assert.Equal(DataKind.Int32, arrayColumn.ArrayElementKind);

        // Verify row values.
        List<Row> rows = await provider.OpenAsync(descriptor, null, CancellationToken.None).CollectRowsAsync();

        Assert.Equal(2, rows.Count);

        DataValue[] firstArray = rows[0]["values"].AsArray();
        Assert.Equal(3, firstArray.Length);
        Assert.Equal(10, firstArray[0].AsInt32());
        Assert.Equal(20, firstArray[1].AsInt32());
        Assert.Equal(30, firstArray[2].AsInt32());

        DataValue[] secondArray = rows[1]["values"].AsArray();
        Assert.Single(secondArray);
        Assert.Equal(99, secondArray[0].AsInt32());
    }

    [Fact]
    public async Task FinalizeAsync_NullAndEmptyArrays_RoundTrip()
    {
        string path = Path.Combine(_tempDir, "null_array_rt.parquet");
        Schema schema = new([
            new ColumnInfo("id", DataKind.Int32, false),
            new ColumnInfo("tags", DataKind.Array, true, arrayElementKind: DataKind.String)
        ]);

        await using ParquetOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromInt32(1)),
            ("tags", DataValue.FromArray(DataKind.String, [
                DataValue.FromString("a"), DataValue.FromString("b")
            ]))));
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromInt32(2)),
            ("tags", DataValue.NullArray(DataKind.String))));
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromInt32(3)),
            ("tags", DataValue.FromArray(DataKind.String, []))));
        await writer.WriteRowAsync(CreateRow(
            ("id", DataValue.FromInt32(4)),
            ("tags", DataValue.FromArray(DataKind.String, [DataValue.FromString("z")]))));
        await writer.FinalizeAsync();

        DatumIngest.Catalog.Providers.ParquetTableProvider provider = new();
        DatumIngest.Catalog.TableDescriptor descriptor = new("parquet", "test", path, new Dictionary<string, string>());

        List<Row> rows = await provider.OpenAsync(descriptor, null, CancellationToken.None).CollectRowsAsync();

        Assert.Equal(4, rows.Count);

        // Row 1: ["a", "b"]
        DataValue[] firstArray = rows[0]["tags"].AsArray();
        Assert.Equal(2, firstArray.Length);
        Assert.Equal("a", firstArray[0].AsString());
        Assert.Equal("b", firstArray[1].AsString());

        // Row 2: null array — reads back as null.
        Assert.True(rows[1]["tags"].IsNull);

        // Row 3: empty array — reads back as null (empty and null are equivalent in Parquet lists).
        Assert.True(rows[2]["tags"].IsNull);

        // Row 4: ["z"]
        DataValue[] fourthArray = rows[3]["tags"].AsArray();
        Assert.Single(fourthArray);
        Assert.Equal("z", fourthArray[0].AsString());
    }

    [Fact]
    public async Task FinalizeAsync_FloatArrayColumn_RoundTrip()
    {
        string path = Path.Combine(_tempDir, "float_array_rt.parquet");
        Schema schema = new([
            new ColumnInfo("embeddings", DataKind.Array, false, arrayElementKind: DataKind.Float32)
        ]);

        await using ParquetOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("embeddings", DataValue.FromArray(DataKind.Float32, [
                DataValue.FromFloat32(0.1f), DataValue.FromFloat32(0.2f), DataValue.FromFloat32(0.3f)
            ]))));
        await writer.WriteRowAsync(CreateRow(
            ("embeddings", DataValue.FromArray(DataKind.Float32, [
                DataValue.FromFloat32(1.0f), DataValue.FromFloat32(2.0f)
            ]))));
        await writer.FinalizeAsync();

        DatumIngest.Catalog.Providers.ParquetTableProvider provider = new();
        DatumIngest.Catalog.TableDescriptor descriptor = new("parquet", "test", path, new Dictionary<string, string>());

        List<Row> rows = await provider.OpenAsync(descriptor, null, CancellationToken.None).CollectRowsAsync();

        Assert.Equal(2, rows.Count);

        DataValue[] first = rows[0]["embeddings"].AsArray();
        Assert.Equal(3, first.Length);
        Assert.Equal(0.1f, first[0].AsFloat32(), 0.001f);
        Assert.Equal(0.2f, first[1].AsFloat32(), 0.001f);
        Assert.Equal(0.3f, first[2].AsFloat32(), 0.001f);

        DataValue[] second = rows[1]["embeddings"].AsArray();
        Assert.Equal(2, second.Length);
        Assert.Equal(1.0f, second[0].AsFloat32(), 0.001f);
        Assert.Equal(2.0f, second[1].AsFloat32(), 0.001f);
    }
}

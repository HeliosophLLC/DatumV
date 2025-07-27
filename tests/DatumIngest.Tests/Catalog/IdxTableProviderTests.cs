using System.Buffers.Binary;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <see cref="IdxTableProvider"/> using IDX fixture files
/// created programmatically in big-endian binary format.
/// </summary>
public sealed class IdxTableProviderTests : IDisposable
{
    private readonly string _fixtureDirectory;

    public IdxTableProviderTests()
    {
        _fixtureDirectory = Path.Combine(
            Path.GetTempPath(),
            "datum_idx_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_fixtureDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_fixtureDirectory))
        {
            Directory.Delete(_fixtureDirectory, recursive: true);
        }
    }

    private string FixturePath(string fileName) => Path.Combine(_fixtureDirectory, fileName);

    private static TableDescriptor Descriptor(string filePath)
    {
        return new TableDescriptor("idx", "test", filePath, new Dictionary<string, string>());
    }

    private static async Task<List<Row>> ReadAllAsync(IAsyncEnumerable<Row> source)
    {
        List<Row> rows = new();
        await foreach (Row row in source)
        {
            rows.Add(row);
        }

        return rows;
    }

    // ───────────────────── Fixture creation helpers ─────────────────────

    /// <summary>
    /// Writes an IDX file with the given type code, dimensions, and raw data bytes.
    /// </summary>
    private string WriteIdxFile(string fileName, byte typeCode, int[] dimensions, byte[] data)
    {
        string path = FixturePath(fileName);
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write);

        // Magic: [0, 0, typeCode, dimensionCount]
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(typeCode);
        stream.WriteByte((byte)dimensions.Length);

        // Dimension sizes (big-endian int32)
        Span<byte> buffer = stackalloc byte[4];
        foreach (int dimension in dimensions)
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer, dimension);
            stream.Write(buffer);
        }

        // Data
        stream.Write(data);
        return path;
    }

    /// <summary>
    /// Creates an IDX1 file with uint8 labels (MNIST-style label file).
    /// </summary>
    private string CreateLabelsFixture()
    {
        byte[] labels = [7, 2, 1, 0, 4];
        return WriteIdxFile("labels.idx1-ubyte", 0x08, [5], labels);
    }

    /// <summary>
    /// Creates an IDX3 file with uint8 images (MNIST-style image file).
    /// 3 images of 2×2 pixels.
    /// </summary>
    private string CreateImagesFixture()
    {
        // 3 images, each 2×2 pixels (4 bytes per image)
        byte[] pixels =
        [
            // Image 0: all white
            255, 255, 255, 255,
            // Image 1: gradient
            0, 85, 170, 255,
            // Image 2: all black
            0, 0, 0, 0,
        ];
        return WriteIdxFile("images.idx3-ubyte", 0x08, [3, 2, 2], pixels);
    }

    /// <summary>
    /// Creates an IDX2 file with float32 vectors (3 items, each 4 elements).
    /// </summary>
    private string CreateFloat32VectorsFixture()
    {
        float[] values = [1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f, 7.0f, 8.0f, 9.0f, 10.0f, 11.0f, 12.0f];
        byte[] data = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(i * 4), values[i]);
        }

        return WriteIdxFile("vectors.idx2-float32", 0x0D, [3, 4], data);
    }

    /// <summary>
    /// Creates an IDX1 file with float32 scalars (4 items).
    /// </summary>
    private string CreateFloat32ScalarsFixture()
    {
        float[] values = [1.5f, 2.5f, 3.5f, 4.5f];
        byte[] data = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(i * 4), values[i]);
        }

        return WriteIdxFile("scalars.idx1-float32", 0x0D, [4], data);
    }

    /// <summary>
    /// Creates an IDX2 file with uint8 1D arrays (5 items, each 3 bytes).
    /// </summary>
    private string CreateUInt8ArraysFixture()
    {
        byte[] data =
        [
            10, 20, 30,
            40, 50, 60,
            70, 80, 90,
            100, 110, 120,
            130, 140, 150,
        ];
        return WriteIdxFile("arrays.idx2-ubyte", 0x08, [5, 3], data);
    }

    /// <summary>
    /// Creates an IDX4 file with uint8 RGB images (2 items of 2×2×3).
    /// </summary>
    private string CreateRgbImagesFixture()
    {
        // 2 images, each 2×2 pixels, 3 channels (RGB)
        byte[] pixels =
        [
            // Image 0: red, green, blue, white
            255, 0, 0,   0, 255, 0,   0, 0, 255,   255, 255, 255,
            // Image 1: all gray
            128, 128, 128,   128, 128, 128,   128, 128, 128,   128, 128, 128,
        ];
        return WriteIdxFile("rgb.idx4-ubyte", 0x08, [2, 2, 2, 3], pixels);
    }

    // ───────────────────── Schema tests ─────────────────────

    [Fact]
    public async Task GetSchema_Idx1UInt8_HasIndexAndValueColumns()
    {
        string path = CreateLabelsFixture();
        IdxTableProvider provider = new();

        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("index", schema.Columns[0].Name);
        Assert.Equal(DataKind.Scalar, schema.Columns[0].Kind);
        Assert.Equal("value", schema.Columns[1].Name);
        Assert.Equal(DataKind.UInt8, schema.Columns[1].Kind);
    }

    [Fact]
    public async Task GetSchema_Idx3UInt8_HasIndexAndImageColumns()
    {
        string path = CreateImagesFixture();
        IdxTableProvider provider = new();

        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("index", schema.Columns[0].Name);
        Assert.Equal(DataKind.Scalar, schema.Columns[0].Kind);
        Assert.Equal("image", schema.Columns[1].Name);
        Assert.Equal(DataKind.Image, schema.Columns[1].Kind);
    }

    [Fact]
    public async Task GetSchema_Idx2Float32_HasIndexAndDataColumns()
    {
        string path = CreateFloat32VectorsFixture();
        IdxTableProvider provider = new();

        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("index", schema.Columns[0].Name);
        Assert.Equal("data", schema.Columns[1].Name);
        Assert.Equal(DataKind.Vector, schema.Columns[1].Kind);
    }

    [Fact]
    public async Task GetSchema_Idx1Float32_HasValueColumn()
    {
        string path = CreateFloat32ScalarsFixture();
        IdxTableProvider provider = new();

        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Equal("value", schema.Columns[1].Name);
        Assert.Equal(DataKind.Scalar, schema.Columns[1].Kind);
    }

    [Fact]
    public async Task GetSchema_Idx2UInt8_HasDataColumnAsUInt8Array()
    {
        string path = CreateUInt8ArraysFixture();
        IdxTableProvider provider = new();

        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);

        Assert.Equal("data", schema.Columns[1].Name);
        Assert.Equal(DataKind.UInt8Array, schema.Columns[1].Kind);
    }

    // ───────────────────── Row reading tests ─────────────────────

    [Fact]
    public async Task Open_Idx1UInt8Labels_ReadsCorrectValues()
    {
        string path = CreateLabelsFixture();
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(5, rows.Count);

        // Check indices are 0-based sequential.
        Assert.Equal(0f, rows[0]["index"].AsScalar());
        Assert.Equal(4f, rows[4]["index"].AsScalar());

        // Check label values.
        Assert.Equal((byte)7, rows[0]["value"].AsUInt8());
        Assert.Equal((byte)2, rows[1]["value"].AsUInt8());
        Assert.Equal((byte)1, rows[2]["value"].AsUInt8());
        Assert.Equal((byte)0, rows[3]["value"].AsUInt8());
        Assert.Equal((byte)4, rows[4]["value"].AsUInt8());
    }

    [Fact]
    public async Task Open_Idx3UInt8Images_ReadsCorrectNumberOfRows()
    {
        string path = CreateImagesFixture();
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task Open_Idx3UInt8Images_ProducesImageDataKind()
    {
        string path = CreateImagesFixture();
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(DataKind.Image, rows[0]["image"].Kind);
    }

    [Fact]
    public async Task Open_Idx3UInt8Images_ImageHasCorrectDimensions()
    {
        string path = CreateImagesFixture();
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        // Decode the image to check dimensions.
        byte[] imageBytes = rows[0]["image"].AsImage();
        using SKBitmap bitmap = SKBitmap.Decode(imageBytes);

        Assert.Equal(2, bitmap.Width);
        Assert.Equal(2, bitmap.Height);
    }

    [Fact]
    public async Task Open_Idx3UInt8Images_GrayscalePixelsAreCorrect()
    {
        string path = CreateImagesFixture();
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        // Image 0 is all-white (255).
        byte[] imageBytes = rows[0]["image"].AsImage();
        using SKBitmap bitmap = SKBitmap.Decode(imageBytes);

        // Top-left pixel should be white (255, 255, 255, 255).
        SKColor pixel = bitmap.GetPixel(0, 0);
        Assert.Equal(255, pixel.Red);
        Assert.Equal(255, pixel.Green);
        Assert.Equal(255, pixel.Blue);
        Assert.Equal(255, pixel.Alpha);

        // Image 2 is all-black (0).
        byte[] blackBytes = rows[2]["image"].AsImage();
        using SKBitmap blackBitmap = SKBitmap.Decode(blackBytes);

        SKColor blackPixel = blackBitmap.GetPixel(0, 0);
        Assert.Equal(0, blackPixel.Red);
        Assert.Equal(0, blackPixel.Green);
        Assert.Equal(0, blackPixel.Blue);
        Assert.Equal(255, blackPixel.Alpha);
    }

    [Fact]
    public async Task Open_Float32Vectors_ReadsCorrectValues()
    {
        string path = CreateFloat32VectorsFixture();
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);

        // First vector: [1, 2, 3, 4]
        float[] vector0 = rows[0]["data"].AsVector();
        Assert.Equal(4, vector0.Length);
        Assert.Equal(1.0f, vector0[0]);
        Assert.Equal(4.0f, vector0[3]);

        // Third vector: [9, 10, 11, 12]
        float[] vector2 = rows[2]["data"].AsVector();
        Assert.Equal(9.0f, vector2[0]);
        Assert.Equal(12.0f, vector2[3]);
    }

    [Fact]
    public async Task Open_Float32Scalars_ReadsCorrectValues()
    {
        string path = CreateFloat32ScalarsFixture();
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(4, rows.Count);
        Assert.Equal(1.5f, rows[0]["value"].AsScalar());
        Assert.Equal(4.5f, rows[3]["value"].AsScalar());
    }

    [Fact]
    public async Task Open_UInt8Arrays_ReadsCorrectValues()
    {
        string path = CreateUInt8ArraysFixture();
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(5, rows.Count);

        byte[] firstArray = rows[0]["data"].AsUInt8Array();
        Assert.Equal(3, firstArray.Length);
        Assert.Equal((byte)10, firstArray[0]);
        Assert.Equal((byte)30, firstArray[2]);
    }

    [Fact]
    public async Task Open_RgbImages_ProducesImageWithCorrectPixels()
    {
        string path = CreateRgbImagesFixture();
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(2, rows.Count);

        // Image 0, pixel (0,0) should be red.
        byte[] imageBytes = rows[0]["image"].AsImage();
        using SKBitmap bitmap = SKBitmap.Decode(imageBytes);

        Assert.Equal(2, bitmap.Width);
        Assert.Equal(2, bitmap.Height);

        SKColor red = bitmap.GetPixel(0, 0);
        Assert.Equal(255, red.Red);
        Assert.Equal(0, red.Green);
        Assert.Equal(0, red.Blue);
    }

    // ───────────────────── Projection pushdown ─────────────────────

    [Fact]
    public async Task Open_ProjectionIndexOnly_ReturnsOnlyIndex()
    {
        string path = CreateLabelsFixture();
        IdxTableProvider provider = new();

        HashSet<string> requiredColumns = ["index"];
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), requiredColumns, CancellationToken.None));

        Assert.Equal(5, rows.Count);
        Assert.Equal(1, rows[0].FieldCount);
        Assert.Equal(0f, rows[0]["index"].AsScalar());
    }

    [Fact]
    public async Task Open_ProjectionDataOnly_ReturnsOnlyData()
    {
        string path = CreateLabelsFixture();
        IdxTableProvider provider = new();

        HashSet<string> requiredColumns = ["value"];
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), requiredColumns, CancellationToken.None));

        Assert.Equal(5, rows.Count);
        Assert.Equal(1, rows[0].FieldCount);
        Assert.Equal((byte)7, rows[0]["value"].AsUInt8());
    }

    // ───────────────────── Capabilities ─────────────────────

    [Fact]
    public async Task GetCapabilities_ReportsRowCount()
    {
        string path = CreateLabelsFixture();
        IdxTableProvider provider = new();

        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            Descriptor(path), CancellationToken.None);

        Assert.Equal(5L, capabilities.EstimatedRowCount);
        Assert.True(capabilities.SupportsSeek);
    }

    [Fact]
    public async Task GetCapabilities_Images_ReportsRowSizeBytes()
    {
        string path = CreateImagesFixture();
        IdxTableProvider provider = new();

        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            Descriptor(path), CancellationToken.None);

        Assert.Equal(3L, capabilities.EstimatedRowCount);
        // 2×2 pixels × 1 byte + 4 bytes for index float
        Assert.Equal(4L + 4L, capabilities.EstimatedRowSizeBytes);
    }

    // ───────────────────── Error handling ─────────────────────

    [Fact]
    public async Task GetSchema_InvalidMagic_ThrowsInvalidDataException()
    {
        string path = FixturePath("bad_magic.idx");
        File.WriteAllBytes(path, [0xFF, 0xFF, 0x08, 0x01, 0x00, 0x00, 0x00, 0x01]);

        IdxTableProvider provider = new();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.GetSchemaAsync(Descriptor(path), CancellationToken.None));
    }

    [Fact]
    public async Task GetSchema_UnsupportedTypeCode_ThrowsInvalidDataException()
    {
        // Type code 0x0A is not a valid IDX type.
        string path = FixturePath("bad_type.idx");
        File.WriteAllBytes(path, [0x00, 0x00, 0x0A, 0x01, 0x00, 0x00, 0x00, 0x01]);

        IdxTableProvider provider = new();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.GetSchemaAsync(Descriptor(path), CancellationToken.None));
    }

    [Fact]
    public async Task GetSchema_TruncatedFile_ThrowsInvalidDataException()
    {
        // Only 2 bytes — not even a complete magic number.
        string path = FixturePath("truncated.idx");
        File.WriteAllBytes(path, [0x00, 0x00]);

        IdxTableProvider provider = new();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.GetSchemaAsync(Descriptor(path), CancellationToken.None));
    }

    // ───────────────────── Int16 type support ─────────────────────

    [Fact]
    public async Task Open_Int16Scalars_ReadsCorrectValues()
    {
        short[] values = [100, -200, 300];
        byte[] data = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(i * 2), values[i]);
        }

        string path = WriteIdxFile("int16.idx1", 0x0B, [3], data);
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(100f, rows[0]["value"].AsScalar());
        Assert.Equal(-200f, rows[1]["value"].AsScalar());
        Assert.Equal(300f, rows[2]["value"].AsScalar());
    }

    // ───────────────────── Int32 type support ─────────────────────

    [Fact]
    public async Task Open_Int32Scalars_ReadsCorrectValues()
    {
        int[] values = [42, -1, 1000000];
        byte[] data = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(i * 4), values[i]);
        }

        string path = WriteIdxFile("int32.idx1", 0x0C, [3], data);
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(42f, rows[0]["value"].AsScalar());
        Assert.Equal(-1f, rows[1]["value"].AsScalar());
    }

    // ───────────────────── Float64 type support ─────────────────────

    [Fact]
    public async Task Open_Float64Scalars_ReadsCorrectValues()
    {
        double[] values = [1.125, 2.25, 3.375];
        byte[] data = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteDoubleBigEndian(data.AsSpan(i * 8), values[i]);
        }

        string path = WriteIdxFile("float64.idx1", 0x0E, [3], data);
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(1.125f, rows[0]["value"].AsScalar());
        Assert.Equal(2.25f, rows[1]["value"].AsScalar());
        Assert.Equal(3.375f, rows[2]["value"].AsScalar());
    }

    // ───────────────────── Matrix type (float32 2D) ─────────────────────

    [Fact]
    public async Task Open_Float32Matrix_ReadsCorrectValues()
    {
        // 2 items, each a 2×3 matrix.
        float[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        byte[] data = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(i * 4), values[i]);
        }

        string path = WriteIdxFile("matrix.idx3-float32", 0x0D, [2, 2, 3], data);
        IdxTableProvider provider = new();

        Schema schema = await provider.GetSchemaAsync(Descriptor(path), CancellationToken.None);
        Assert.Equal(DataKind.Matrix, schema.Columns[1].Kind);

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Equal(2, rows.Count);

        float[] matrix0 = rows[0]["data"].AsMatrix(out int matrixRows, out int matrixColumns);
        Assert.Equal(2, matrixRows);
        Assert.Equal(3, matrixColumns);
        Assert.Equal(1f, matrix0[0]);
        Assert.Equal(6f, matrix0[5]);
    }

    // ───────────────────── Zero-item file ─────────────────────

    [Fact]
    public async Task Open_ZeroItems_ReturnsEmpty()
    {
        string path = WriteIdxFile("empty.idx1-ubyte", 0x08, [0], []);
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(path), null, CancellationToken.None));

        Assert.Empty(rows);
    }

    // ───────────────────── ReadRowRangeAsync (seeking) ─────────────────────

    [Fact]
    public async Task ReadRowRange_MiddleRange_ReturnsCorrectRows()
    {
        string path = CreateLabelsFixture(); // 5 labels: [7, 2, 1, 0, 4]
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 1, count: 3,
                CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(1f, rows[0]["index"].AsScalar()); // row 1
        Assert.Equal(2f, rows[0]["value"].AsUInt8());
        Assert.Equal(2f, rows[1]["index"].AsScalar()); // row 2
        Assert.Equal(1f, rows[1]["value"].AsUInt8());
        Assert.Equal(3f, rows[2]["index"].AsScalar()); // row 3
        Assert.Equal(0f, rows[2]["value"].AsUInt8());
    }

    [Fact]
    public async Task ReadRowRange_FirstRow_ReturnsSingleRow()
    {
        string path = CreateLabelsFixture(); // 5 labels: [7, 2, 1, 0, 4]
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 0, count: 1,
                CancellationToken.None));

        Assert.Single(rows);
        Assert.Equal(0f, rows[0]["index"].AsScalar());
        Assert.Equal(7f, rows[0]["value"].AsUInt8());
    }

    [Fact]
    public async Task ReadRowRange_LastRow_ReturnsSingleRow()
    {
        string path = CreateLabelsFixture(); // 5 labels: [7, 2, 1, 0, 4]
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 4, count: 1,
                CancellationToken.None));

        Assert.Single(rows);
        Assert.Equal(4f, rows[0]["index"].AsScalar());
        Assert.Equal(4f, rows[0]["value"].AsUInt8());
    }

    [Fact]
    public async Task ReadRowRange_BeyondEnd_ClampsToAvailable()
    {
        string path = CreateLabelsFixture(); // 5 labels: [7, 2, 1, 0, 4]
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 3, count: 100,
                CancellationToken.None));

        Assert.Equal(2, rows.Count);
        Assert.Equal(3f, rows[0]["index"].AsScalar());
        Assert.Equal(4f, rows[1]["index"].AsScalar());
    }

    [Fact]
    public async Task ReadRowRange_StartBeyondEnd_ReturnsEmpty()
    {
        string path = CreateLabelsFixture(); // 5 labels: [7, 2, 1, 0, 4]
        IdxTableProvider provider = new();

        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), null, startRow: 10, count: 5,
                CancellationToken.None));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ReadRowRange_WithProjection_ReturnsOnlyRequestedColumns()
    {
        string path = CreateLabelsFixture();
        IdxTableProvider provider = new();

        HashSet<string> requiredColumns = new(StringComparer.OrdinalIgnoreCase) { "index" };
        List<Row> rows = await ReadAllAsync(
            provider.ReadRowRangeAsync(Descriptor(path), requiredColumns, startRow: 2, count: 2,
                CancellationToken.None));

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0].FieldCount);
        Assert.Equal(2f, rows[0]["index"].AsScalar());
        Assert.Equal(3f, rows[1]["index"].AsScalar());
    }

    [Fact]
    public async Task ReadRowRange_AllRows_MatchesOpenAsync()
    {
        string path = CreateLabelsFixture(); // 5 labels
        IdxTableProvider provider = new();
        TableDescriptor descriptor = Descriptor(path);

        List<Row> allRows = await ReadAllAsync(
            provider.OpenAsync(descriptor, null, CancellationToken.None));
        List<Row> seekRows = await ReadAllAsync(
            provider.ReadRowRangeAsync(descriptor, null, startRow: 0, count: 5,
                CancellationToken.None));

        Assert.Equal(allRows.Count, seekRows.Count);
        for (int i = 0; i < allRows.Count; i++)
        {
            Assert.Equal(allRows[i]["index"].AsScalar(), seekRows[i]["index"].AsScalar());
            Assert.Equal(allRows[i]["value"].AsUInt8(), seekRows[i]["value"].AsUInt8());
        }
    }

    // ───────────────────── FetchByKeysAsync (keyed random access) ─────────────────────

    /// <summary>
    /// FetchByKeysAsync returns only the rows whose index values match the
    /// requested key set, with all columns populated.
    /// </summary>
    [Fact]
    public async Task FetchByKeys_Labels_ReturnsOnlyMatchingRows()
    {
        string path = CreateLabelsFixture(); // 5 labels: [7, 2, 1, 0, 4]
        IdxTableProvider provider = new();

        HashSet<DataValue> keys = [DataValue.FromScalar(1), DataValue.FromScalar(3)];
        List<Row> rows = await ReadAllAsync(
            provider.FetchByKeysAsync(Descriptor(path), "index", keys, null,
                CancellationToken.None));

        Assert.Equal(2, rows.Count);

        // Results should be ordered by key.
        Assert.Equal(1f, rows[0]["index"].AsScalar());
        Assert.Equal(2, rows[0]["value"].AsUInt8());

        Assert.Equal(3f, rows[1]["index"].AsScalar());
        Assert.Equal(0, rows[1]["value"].AsUInt8());
    }

    /// <summary>
    /// FetchByKeysAsync respects projection pushdown — when only the data
    /// column is requested, index is still included (per the interface contract)
    /// but the result is limited to the specified columns.
    /// </summary>
    [Fact]
    public async Task FetchByKeys_WithProjection_ReturnsOnlyRequestedColumns()
    {
        string path = CreateLabelsFixture(); // 5 labels: [7, 2, 1, 0, 4]
        IdxTableProvider provider = new();

        HashSet<DataValue> keys = [DataValue.FromScalar(0)];
        HashSet<string> requiredColumns = new(StringComparer.OrdinalIgnoreCase) { "value" };
        List<Row> rows = await ReadAllAsync(
            provider.FetchByKeysAsync(Descriptor(path), "index", keys, requiredColumns,
                CancellationToken.None));

        Assert.Single(rows);
        // Key column is always included.
        Assert.Equal(0f, rows[0]["index"].AsScalar());
        Assert.Equal(7, rows[0]["value"].AsUInt8());
    }

    /// <summary>
    /// FetchByKeysAsync on an image file returns correct image data for the
    /// requested indices.
    /// </summary>
    [Fact]
    public async Task FetchByKeys_Images_ReturnsCorrectImageData()
    {
        string path = CreateImagesFixture(); // 3 images of 2×2
        IdxTableProvider provider = new();

        HashSet<DataValue> keys = [DataValue.FromScalar(2)];
        List<Row> rows = await ReadAllAsync(
            provider.FetchByKeysAsync(Descriptor(path), "index", keys, null,
                CancellationToken.None));

        Assert.Single(rows);
        Assert.Equal(2f, rows[0]["index"].AsScalar());
        Assert.Equal(DataKind.Image, rows[0]["image"].Kind);
    }

    /// <summary>
    /// FetchByKeysAsync silently ignores key values that are out of range.
    /// </summary>
    [Fact]
    public async Task FetchByKeys_OutOfRangeKeys_IgnoresInvalidKeys()
    {
        string path = CreateLabelsFixture(); // 5 labels
        IdxTableProvider provider = new();

        HashSet<DataValue> keys =
            [DataValue.FromScalar(1), DataValue.FromScalar(99), DataValue.FromScalar(-1)];
        List<Row> rows = await ReadAllAsync(
            provider.FetchByKeysAsync(Descriptor(path), "index", keys, null,
                CancellationToken.None));

        Assert.Single(rows);
        Assert.Equal(1f, rows[0]["index"].AsScalar());
    }

    /// <summary>
    /// GetCapabilities reports the data column as expensive and index as the key
    /// column, enabling late materialization in the query planner.
    /// </summary>
    [Fact]
    public async Task GetCapabilities_Images_ReportsExpensiveDataColumn()
    {
        string path = CreateImagesFixture();
        IdxTableProvider provider = new();

        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            Descriptor(path), CancellationToken.None);

        Assert.Equal("index", capabilities.KeyColumn);
        Assert.True(capabilities.ColumnCosts.ContainsKey("image"));
        Assert.Equal(ColumnCost.Expensive, capabilities.ColumnCosts["image"]);
    }
}

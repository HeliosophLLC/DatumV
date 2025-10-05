using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Functions.Image;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads IDX binary files (the format used by MNIST, Fashion-MNIST, and similar
/// datasets). Each file becomes a table with an auto-generated <c>index</c> column
/// (Scalar, 0-based) for joining separate image and label files on a shared key.
/// </summary>
/// <remarks>
/// <para>
/// The IDX format stores a single N-dimensional array of homogeneous typed values.
/// The header encodes the data type and the number of dimensions, followed by the
/// size of each dimension as big-endian 32-bit integers. Dimension 0 is the item
/// count; dimensions 1+ define the per-item shape.
/// </para>
/// <para>
/// For <c>uint8</c> data with two or more per-item dimensions, the provider creates
/// images via <see cref="ImageHandle"/> in RGBA8888 format, enabling full integration
/// with the 30+ built-in image functions (resize, crop, blur, etc.).
/// </para>
/// </remarks>
public sealed class IdxTableProvider : ISeekableTableProvider, IKeyedTableProvider
{
    private const int MagicNumberLength = 4;

    // ───────────────────── IDX data type codes ─────────────────────

    private const byte TypeCodeUInt8 = 0x08;
    private const byte TypeCodeInt8 = 0x09;
    private const byte TypeCodeInt16 = 0x0B;
    private const byte TypeCodeInt32 = 0x0C;
    private const byte TypeCodeFloat32 = 0x0D;
    private const byte TypeCodeFloat64 = 0x0E;

    /// <summary>
    /// Number of rows to accumulate in each <see cref="RowBatch"/> before
    /// yielding to the consumer.
    /// </summary>
    private const int DefaultBatchSize = 1024;

    /// <inheritdoc />
    public Task<Schema> GetSchemaAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        using FileStream stream = OpenFile(descriptor);
        IdxHeader header = ReadHeader(stream);
        Schema schema = BuildSchema(header);
        return Task.FromResult(schema);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using FileStream stream = OpenFile(descriptor);
        IdxHeader header = ReadHeader(stream);

        bool includeIndex = requiredColumns is null ||
            requiredColumns.Contains("index");
        bool includeData = requiredColumns is null ||
            requiredColumns.Contains(DataColumnName(header));

        List<string> columnNames = new();
        if (includeIndex)
        {
            columnNames.Add("index");
        }

        if (includeData)
        {
            columnNames.Add(DataColumnName(header));
        }

        string[] names = columnNames.ToArray();
        Dictionary<string, int> nameIndex = new(names.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Length; i++)
        {
            nameIndex[names[i]] = i;
        }

        int itemByteSize = header.ItemByteSize;
        byte[] itemBuffer = new byte[itemByteSize];

        RowBatch? batch = null;

        for (int rowIndex = 0; rowIndex < header.ItemCount; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (includeData)
            {
                ReadExactly(stream, itemBuffer);
            }
            else
            {
                // Skip the data bytes when only index is requested.
                stream.Seek(itemByteSize, SeekOrigin.Current);
            }

            DataValue[] values = DatumIngest.Execution.Pooling.GlobalPool.Backing.RentDataValues(names.Length);
            int valueIndex = 0;

            if (includeIndex)
            {
                values[valueIndex++] = DataValue.FromFloat32(rowIndex);
            }

            if (includeData)
            {
                values[valueIndex] = CreateDataValue(header, itemBuffer);
            }

            batch ??= RowBatch.Rent(DefaultBatchSize);
            batch.Add(new Row(names, values, nameIndex));
            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        using FileStream stream = OpenFile(descriptor);
        IdxHeader header = ReadHeader(stream);
        string dataColumnName = DataColumnName(header);

        Dictionary<string, ColumnCost> columnCosts = new(StringComparer.OrdinalIgnoreCase)
        {
            [dataColumnName] = ColumnCost.Expensive,
        };

        return Task.FromResult(new ProviderCapabilities(
            EstimatedRowCount: header.ItemCount,
            EstimatedRowSizeBytes: header.ItemByteSize + sizeof(float),
            SupportsSeek: true,
            ColumnCosts: columnCosts,
            KeyColumn: "index"));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ReadRowRangeAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        long startRow,
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using FileStream stream = OpenFile(descriptor);
        IdxHeader header = ReadHeader(stream);

        // Clamp to available rows.
        if (startRow >= header.ItemCount)
        {
            yield break;
        }

        int rowsToRead = (int)Math.Min(count, header.ItemCount - startRow);

        bool includeIndex = requiredColumns is null ||
            requiredColumns.Contains("index");
        bool includeData = requiredColumns is null ||
            requiredColumns.Contains(DataColumnName(header));

        List<string> columnNames = new();
        if (includeIndex)
        {
            columnNames.Add("index");
        }

        if (includeData)
        {
            columnNames.Add(DataColumnName(header));
        }

        string[] names = columnNames.ToArray();
        Dictionary<string, int> nameIndex = new(names.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Length; i++)
        {
            nameIndex[names[i]] = i;
        }

        // Seek directly to the start row, skipping the header and preceding rows.
        int itemByteSize = header.ItemByteSize;
        long dataOffset = stream.Position + startRow * itemByteSize;
        stream.Seek(dataOffset, SeekOrigin.Begin);

        byte[] itemBuffer = new byte[itemByteSize];

        RowBatch? batch = null;

        for (int i = 0; i < rowsToRead; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (includeData)
            {
                ReadExactly(stream, itemBuffer);
            }
            else
            {
                stream.Seek(itemByteSize, SeekOrigin.Current);
            }

            DataValue[] values = DatumIngest.Execution.Pooling.GlobalPool.Backing.RentDataValues(names.Length);
            int valueIndex = 0;

            if (includeIndex)
            {
                values[valueIndex++] = DataValue.FromFloat32(startRow + i);
            }

            if (includeData)
            {
                values[valueIndex] = CreateDataValue(header, itemBuffer);
            }

            batch ??= RowBatch.Rent(DefaultBatchSize);
            batch.Add(new Row(names, values, nameIndex));
            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> FetchByKeysAsync(
        TableDescriptor descriptor,
        string keyColumn,
        IReadOnlySet<DataValue> keyValues,
        IReadOnlySet<string>? requiredColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using FileStream stream = OpenFile(descriptor);
        IdxHeader header = ReadHeader(stream);
        long dataStartPosition = stream.Position;

        string dataColumnName = DataColumnName(header);
        bool includeData = requiredColumns is null ||
            requiredColumns.Contains(dataColumnName);

        // The key column (index) is always included per the interface contract.
        List<string> columnNames = new() { "index" };
        if (includeData)
        {
            columnNames.Add(dataColumnName);
        }

        string[] names = columnNames.ToArray();
        Dictionary<string, int> nameIndex = new(names.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Length; i++)
        {
            nameIndex[names[i]] = i;
        }

        // Extract valid integer indices from the key set, sort for sequential I/O.
        List<int> sortedIndices = new();
        foreach (DataValue key in keyValues)
        {
            int index = (int)key.AsFloat32();
            if (index >= 0 && index < header.ItemCount)
            {
                sortedIndices.Add(index);
            }
        }

        sortedIndices.Sort();

        int itemByteSize = header.ItemByteSize;
        byte[] itemBuffer = new byte[itemByteSize];

        RowBatch? batch = null;

        foreach (int rowIndex in sortedIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long offset = dataStartPosition + (long)rowIndex * itemByteSize;
            stream.Seek(offset, SeekOrigin.Begin);

            DataValue[] values = DatumIngest.Execution.Pooling.GlobalPool.Backing.RentDataValues(names.Length);
            values[0] = DataValue.FromFloat32(rowIndex);

            if (includeData)
            {
                ReadExactly(stream, itemBuffer);
                values[1] = CreateDataValue(header, itemBuffer);
            }

            batch ??= RowBatch.Rent(DefaultBatchSize);
            batch.Add(new Row(names, values, nameIndex));
            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    // ───────────────────── Header parsing ─────────────────────

    /// <summary>
    /// Parsed IDX file header containing the data type, dimension count,
    /// dimension sizes, and derived per-item metrics.
    /// </summary>
    private sealed class IdxHeader
    {
        /// <summary>The IDX data type code (e.g. 0x08 = uint8).</summary>
        public required byte TypeCode { get; init; }

        /// <summary>Number of dimensions in the array.</summary>
        public required int DimensionCount { get; init; }

        /// <summary>Size of each dimension. Index 0 is the item count.</summary>
        public required int[] Dimensions { get; init; }

        /// <summary>Number of items (rows) — equal to Dimensions[0].</summary>
        public int ItemCount => Dimensions[0];

        /// <summary>Per-item dimensions (everything after dimension 0).</summary>
        public ReadOnlySpan<int> ItemShape => Dimensions.AsSpan(1);

        /// <summary>Number of elements per item (product of ItemShape).</summary>
        public int ItemElementCount
        {
            get
            {
                int count = 1;
                ReadOnlySpan<int> shape = ItemShape;
                for (int i = 0; i < shape.Length; i++)
                {
                    count *= shape[i];
                }

                return count;
            }
        }

        /// <summary>Byte size per element for this data type.</summary>
        public int ElementByteSize => TypeCode switch
        {
            TypeCodeUInt8 => 1,
            TypeCodeInt8 => 1,
            TypeCodeInt16 => 2,
            TypeCodeInt32 => 4,
            TypeCodeFloat32 => 4,
            TypeCodeFloat64 => 8,
            _ => throw new InvalidOperationException($"Unsupported IDX type code: 0x{TypeCode:X2}.")
        };

        /// <summary>Total byte size per item.</summary>
        public int ItemByteSize => ItemElementCount * ElementByteSize;

        /// <summary>Whether the data type is unsigned 8-bit integer.</summary>
        public bool IsUInt8 => TypeCode == TypeCodeUInt8;

        /// <summary>Number of per-item dimensions (DimensionCount - 1).</summary>
        public int ItemDimensionCount => DimensionCount - 1;
    }

    /// <summary>
    /// Reads and validates the IDX file header from the current stream position.
    /// </summary>
    /// <exception cref="InvalidDataException">Thrown when the file is not a valid IDX file.</exception>
    private static IdxHeader ReadHeader(Stream stream)
    {
        Span<byte> magic = stackalloc byte[MagicNumberLength];
        ReadExactly(stream, magic);

        if (magic[0] != 0 || magic[1] != 0)
        {
            throw new InvalidDataException(
                "Invalid IDX file: first two bytes of the magic number must be zero.");
        }

        byte typeCode = magic[2];
        int dimensionCount = magic[3];

        if (dimensionCount < 1)
        {
            throw new InvalidDataException(
                "Invalid IDX file: dimension count must be at least 1.");
        }

        ValidateTypeCode(typeCode);

        int[] dimensions = new int[dimensionCount];
        Span<byte> dimensionBuffer = stackalloc byte[sizeof(int)];

        for (int i = 0; i < dimensionCount; i++)
        {
            ReadExactly(stream, dimensionBuffer);
            dimensions[i] = BinaryPrimitives.ReadInt32BigEndian(dimensionBuffer);

            if (dimensions[i] < 0)
            {
                throw new InvalidDataException(
                    $"Invalid IDX file: dimension {i} has negative size {dimensions[i]}.");
            }
        }

        return new IdxHeader
        {
            TypeCode = typeCode,
            DimensionCount = dimensionCount,
            Dimensions = dimensions,
        };
    }

    private static void ValidateTypeCode(byte typeCode)
    {
        if (typeCode is not (TypeCodeUInt8 or TypeCodeInt8 or TypeCodeInt16
            or TypeCodeInt32 or TypeCodeFloat32 or TypeCodeFloat64))
        {
            throw new InvalidDataException(
                $"Invalid IDX file: unsupported data type code 0x{typeCode:X2}. " +
                "Supported types: uint8 (0x08), int8 (0x09), int16 (0x0B), " +
                "int32 (0x0C), float32 (0x0D), float64 (0x0E).");
        }
    }

    // ───────────────────── Schema construction ─────────────────────

    /// <summary>
    /// Builds a two-column schema from the parsed header. The first column is
    /// always <c>index</c> (Scalar); the second column's name and kind depend
    /// on the data type and per-item dimensionality.
    /// </summary>
    private static Schema BuildSchema(IdxHeader header)
    {
        DataKind dataKind = InferDataKind(header);
        string dataColumnName = DataColumnName(header);

        return new Schema(new ColumnInfo[]
        {
            new("index", DataKind.Float32, nullable: false),
            new(dataColumnName, dataKind, nullable: false),
        });
    }

    /// <summary>
    /// Returns the data column name based on the header: <c>image</c> for uint8
    /// data with 2+ per-item dimensions, <c>value</c> for scalars, and
    /// <c>data</c> for arrays/vectors/matrices/tensors.
    /// </summary>
    private static string DataColumnName(IdxHeader header)
    {
        if (header.IsUInt8 && header.ItemDimensionCount >= 2)
        {
            return "image";
        }

        return header.ItemDimensionCount == 0 ? "value" : "data";
    }

    /// <summary>
    /// Infers the <see cref="DataKind"/> from the IDX header based on the data type
    /// and per-item dimensionality.
    /// </summary>
    private static DataKind InferDataKind(IdxHeader header)
    {
        if (header.IsUInt8)
        {
            return header.ItemDimensionCount switch
            {
                0 => DataKind.UInt8,
                1 => DataKind.UInt8Array,
                _ => DataKind.Image,
            };
        }

        return header.ItemDimensionCount switch
        {
            0 => DataKind.Float32,
            1 => DataKind.Vector,
            2 => DataKind.Matrix,
            _ => DataKind.Tensor,
        };
    }

    // ───────────────────── Data value construction ─────────────────────

    /// <summary>
    /// Creates a <see cref="DataValue"/> from the raw item bytes according to the
    /// data type and dimensionality described by the header.
    /// </summary>
    private static DataValue CreateDataValue(IdxHeader header, byte[] itemBuffer)
    {
        if (header.IsUInt8)
        {
            return header.ItemDimensionCount switch
            {
                0 => DataValue.FromUInt8(itemBuffer[0]),
                1 => DataValue.FromUInt8Array(itemBuffer.ToArray()),
                _ => CreateImageFromUInt8(header, itemBuffer),
            };
        }

        return header.ItemDimensionCount switch
        {
            0 => DataValue.FromFloat32(ReadScalarElement(header.TypeCode, itemBuffer)),
            1 => DataValue.FromVector(ReadFloatArray(header, itemBuffer)),
            2 => DataValue.FromMatrix(
                ReadFloatArray(header, itemBuffer),
                header.ItemShape[0],
                header.ItemShape[1]),
            _ => DataValue.FromTensor(
                ReadFloatArray(header, itemBuffer),
                header.ItemShape.ToArray()),
        };
    }

    /// <summary>
    /// Creates an RGBA8888 <see cref="ImageHandle"/> from uint8 pixel data.
    /// Supports grayscale (2D), and multi-channel (3D+) layouts.
    /// </summary>
    private static DataValue CreateImageFromUInt8(IdxHeader header, byte[] pixelData)
    {
        ReadOnlySpan<int> shape = header.ItemShape;
        int height = shape[0];
        int width = shape[1];

        // Determine channel count: 2D = grayscale (1ch), 3D = shape[2] channels.
        int channels = shape.Length >= 3 ? shape[2] : 1;

        // For 4D+ data, flatten extra dimensions into channel count.
        for (int d = 3; d < shape.Length; d++)
        {
            channels *= shape[d];
        }

        SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        IntPtr pixelPointer = bitmap.GetPixels();

        unsafe
        {
            byte* destination = (byte*)pixelPointer;
            int pixelCount = width * height;

            switch (channels)
            {
                case 1:
                    // Grayscale: replicate to R, G, B; alpha = 255.
                    for (int i = 0; i < pixelCount; i++)
                    {
                        byte gray = pixelData[i];
                        destination[i * 4] = gray;
                        destination[i * 4 + 1] = gray;
                        destination[i * 4 + 2] = gray;
                        destination[i * 4 + 3] = 255;
                    }

                    break;

                case 3:
                    // RGB: copy channels, alpha = 255.
                    for (int i = 0; i < pixelCount; i++)
                    {
                        destination[i * 4] = pixelData[i * 3];
                        destination[i * 4 + 1] = pixelData[i * 3 + 1];
                        destination[i * 4 + 2] = pixelData[i * 3 + 2];
                        destination[i * 4 + 3] = 255;
                    }

                    break;

                case 4:
                    // RGBA: direct copy.
                    new ReadOnlySpan<byte>(pixelData, 0, pixelCount * 4)
                        .CopyTo(new Span<byte>(destination, pixelCount * 4));
                    break;

                default:
                    // Arbitrary channel count: use first 3 or pad with zero.
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int baseOffset = i * channels;
                        destination[i * 4] = pixelData[baseOffset];
                        destination[i * 4 + 1] = channels > 1 ? pixelData[baseOffset + 1] : (byte)0;
                        destination[i * 4 + 2] = channels > 2 ? pixelData[baseOffset + 2] : (byte)0;
                        destination[i * 4 + 3] = channels > 3 ? pixelData[baseOffset + 3] : (byte)255;
                    }

                    break;
            }
        }

        ImageHandle handle = new(bitmap, SKEncodedImageFormat.Png);
        return DataValue.FromImageHandle(handle);
    }

    // ───────────────────── Numeric reading helpers ─────────────────────

    /// <summary>
    /// Reads a single numeric element from the buffer and returns it as a float.
    /// All multi-byte values are big-endian per the IDX specification.
    /// </summary>
    private static float ReadScalarElement(byte typeCode, ReadOnlySpan<byte> buffer)
    {
        return typeCode switch
        {
            TypeCodeUInt8 => buffer[0],
            TypeCodeInt8 => (sbyte)buffer[0],
            TypeCodeInt16 => BinaryPrimitives.ReadInt16BigEndian(buffer),
            TypeCodeInt32 => BinaryPrimitives.ReadInt32BigEndian(buffer),
            TypeCodeFloat32 => BinaryPrimitives.ReadSingleBigEndian(buffer),
            TypeCodeFloat64 => (float)BinaryPrimitives.ReadDoubleBigEndian(buffer),
            _ => throw new InvalidOperationException($"Unsupported IDX type code: 0x{typeCode:X2}.")
        };
    }

    /// <summary>
    /// Reads all elements from the item buffer into a float array.
    /// </summary>
    private static float[] ReadFloatArray(IdxHeader header, ReadOnlySpan<byte> itemBuffer)
    {
        int elementCount = header.ItemElementCount;
        int elementSize = header.ElementByteSize;
        float[] result = new float[elementCount];

        for (int i = 0; i < elementCount; i++)
        {
            result[i] = ReadScalarElement(
                header.TypeCode,
                itemBuffer.Slice(i * elementSize, elementSize));
        }

        return result;
    }

    // ───────────────────── I/O helpers ─────────────────────

    private static FileStream OpenFile(TableDescriptor descriptor)
    {
        return new FileStream(
            descriptor.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int bytesRead = stream.Read(buffer[totalRead..]);
            if (bytesRead == 0)
            {
                throw new InvalidDataException(
                    $"Unexpected end of IDX file: expected {buffer.Length} bytes but read {totalRead}.");
            }

            totalRead += bytesRead;
        }
    }
}

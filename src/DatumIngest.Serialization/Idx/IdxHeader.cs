using System.Buffers.Binary;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Idx;

/// <summary>
/// Parsed IDX file header containing the data type, dimension count,
/// dimension sizes, and derived per-item metrics.
/// </summary>
internal sealed class IdxHeader
{
    private const byte TypeCodeUInt8 = 0x08;
    private const byte TypeCodeInt8 = 0x09;
    private const byte TypeCodeInt16 = 0x0B;
    private const byte TypeCodeInt32 = 0x0C;
    private const byte TypeCodeFloat32 = 0x0D;
    private const byte TypeCodeFloat64 = 0x0E;

    public required byte TypeCode { get; init; }
    public required int DimensionCount { get; init; }
    public required int[] Dimensions { get; init; }

    public int ItemCount => Dimensions[0];
    public ReadOnlySpan<int> ItemShape => Dimensions.AsSpan(1);
    public int ItemDimensionCount => DimensionCount - 1;
    public bool IsUInt8 => TypeCode == TypeCodeUInt8;

    public int ItemElementCount
    {
        get
        {
            int count = 1;
            ReadOnlySpan<int> shape = ItemShape;
            for (int i = 0; i < shape.Length; i++)
                count *= shape[i];
            return count;
        }
    }

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

    public int ItemByteSize => ItemElementCount * ElementByteSize;

    public string DataColumnName => IsUInt8 && ItemDimensionCount >= 2
        ? "image"
        : ItemDimensionCount == 0 ? "value" : "data";

    public (DataKind Kind, bool IsArray) InferDataKind()
    {
        if (IsUInt8)
        {
            return ItemDimensionCount switch
            {
                0 => (DataKind.UInt8, false),
                1 => (DataKind.UInt8, true),
                _ => (DataKind.Image, false),
            };
        }

        return ItemDimensionCount switch
        {
            0 => (DataKind.Float32, false),
            // Rank-1 float arrays land as Float32 + IsArray (the former Vector kind).
            1 => (DataKind.Float32, true),
            // Higher-rank float tensors are deferred. The IdxValueReader throws
            // on rank ≥ 2 too — these match.
            _ => throw new NotSupportedException(
                $"IDX rank-{ItemDimensionCount} float arrays aren't supported yet."),
        };
    }

    /// <summary>
    /// Reads and validates the IDX file header from the current stream position.
    /// </summary>
    public static IdxHeader Read(Stream stream)
    {
        Span<byte> magic = stackalloc byte[4];
        ReadExactly(stream, magic);

        if (magic[0] != 0 || magic[1] != 0)
            throw new InvalidDataException("Invalid IDX file: first two bytes must be zero.");

        byte typeCode = magic[2];
        int dimensionCount = magic[3];

        if (dimensionCount < 1)
            throw new InvalidDataException("Invalid IDX file: dimension count must be at least 1.");

        ValidateTypeCode(typeCode);

        int[] dimensions = new int[dimensionCount];
        Span<byte> dimBuf = stackalloc byte[sizeof(int)];

        for (int i = 0; i < dimensionCount; i++)
        {
            ReadExactly(stream, dimBuf);
            dimensions[i] = BinaryPrimitives.ReadInt32BigEndian(dimBuf);

            if (dimensions[i] < 0)
                throw new InvalidDataException($"Invalid IDX file: dimension {i} has negative size {dimensions[i]}.");
        }

        return new IdxHeader
        {
            TypeCode = typeCode,
            DimensionCount = dimensionCount,
            Dimensions = dimensions,
        };
    }

    internal static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int bytesRead = stream.Read(buffer[totalRead..]);
            if (bytesRead == 0)
                throw new InvalidDataException(
                    $"Unexpected end of IDX file: expected {buffer.Length} bytes but read {totalRead}.");
            totalRead += bytesRead;
        }
    }

    private static void ValidateTypeCode(byte typeCode)
    {
        if (typeCode is not (TypeCodeUInt8 or TypeCodeInt8 or TypeCodeInt16
            or TypeCodeInt32 or TypeCodeFloat32 or TypeCodeFloat64))
        {
            throw new InvalidDataException(
                $"Unsupported IDX type code 0x{typeCode:X2}. " +
                "Supported: uint8 (0x08), int8 (0x09), int16 (0x0B), int32 (0x0C), float32 (0x0D), float64 (0x0E).");
        }
    }
}

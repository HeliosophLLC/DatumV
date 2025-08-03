using System.Runtime.InteropServices;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Decodes a <see cref="DataKind.Uuid"/> column page produced by <c>UuidColumnEncoder</c>.
/// </summary>
/// <remarks>
/// Uncompressed layout: <c>nullBitmap[ceil(N/8)] | bytes[N * 16]</c>.
/// Each non-null row has 16 bytes read back into a <see cref="Guid"/> using
/// <see cref="MemoryMarshal.Read{T}"/> which matches the encoder's
/// <see cref="MemoryMarshal.TryWrite{T}"/> call.
/// </remarks>
internal sealed class UuidColumnDecoder : DatumColumnDecoder
{
    private const int GuidByteSize = 16;

    /// <inheritdoc/>
    public override DataValue[] Decode(
        byte[] payload,
        DatumEncoding encoding,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DatumColumnDescriptor descriptor,
        DatumDecoderContext context)
    {
        byte[] raw = DecompressPayload(payload, uncompressedByteLength, compression);
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        DataValue[] result = new DataValue[rowCount];
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (nullBitmap.IsNull(rowIndex))
            {
                result[rowIndex] = DataValue.Null(DataKind.Uuid);
            }
            else
            {
                int byteOffset = bitmapByteCount + rowIndex * GuidByteSize;
                Guid guid = MemoryMarshal.Read<Guid>(raw.AsSpan(byteOffset, GuidByteSize));
                result[rowIndex] = DataValue.FromUuid(guid);
            }
        }

        return result;
    }
}

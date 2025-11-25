using System.Buffers.Binary;

namespace DatumIngest.Model;

/// <summary>
/// 16-byte pointer-slot codec used by reference-type arrays
/// (<see cref="DataKind.String"/> + <see cref="DataValue.IsArray"/>,
/// <see cref="DataKind.Image"/> + <see cref="DataValue.IsArray"/>,
/// <see cref="DataKind.Struct"/> + <see cref="DataValue.IsArray"/>, and
/// nested <c>Array&lt;Array&lt;X&gt;&gt;</c>).
/// </summary>
/// <remarks>
/// <para>
/// Layout (little-endian throughout):
/// </para>
/// <list type="bullet">
///   <item><description>bytes 0–7: <see cref="long"/> offset into the slot's owning store
///     (arena offset for in-memory, sidecar absolute offset on disk).</description></item>
///   <item><description>bytes 8–12: 5-byte length (40-bit, ~1 TiB cap). Low 4 bytes are
///     a <see cref="uint"/>; byte 12 is the high 8 bits.</description></item>
///   <item><description>bytes 13–14: reserved (zero). Held for future-use without
///     a wire-format bump; do not consume without an RFC.</description></item>
///   <item><description>byte 15: codec discriminator (0 = Raw; future room for Lz4 /
///     Zstd / format-specific compressors per element).</description></item>
/// </list>
/// <para>
/// Identical on the wire and in memory — see
/// <see cref="DatumFile.V2.DatumFormatV2.VariableSlotBytes"/>. The page-level
/// pointer slot used by <c>VariableSlotPageEncoderV2</c> is the same shape; the
/// difference is interpretation of the offset field (page-level always points
/// into its own sidecar; in-memory array slots can point into either an arena
/// or a sidecar, governed by the array's own <see cref="DataValue"/> flags).
/// </para>
/// </remarks>
internal static class ArraySlot
{
    /// <summary>Size of one slot in bytes.</summary>
    public const int SizeBytes = 16;

    /// <summary>Maximum length encodable in the 40-bit length field (~1 TiB).</summary>
    public const long MaxLength = (1L << 40) - 1;

    /// <summary>
    /// Writes a slot into <paramref name="dest"/> at offset 0. The destination span
    /// must be at least <see cref="SizeBytes"/> long; the high 4 bytes (13–14
    /// reserved + 15 codec) are written explicitly so callers don't need to
    /// pre-zero the buffer.
    /// </summary>
    /// <param name="dest">Destination span. Must be ≥ <see cref="SizeBytes"/> bytes.</param>
    /// <param name="offset">
    /// Store-relative offset. Arena offsets sign-extend into the 64-bit field
    /// (the 32-bit arena offset zero-extends to 64-bit because arena offsets are
    /// non-negative).
    /// </param>
    /// <param name="length">Payload length in bytes. Must fit in 40 bits.</param>
    /// <param name="codec">Codec discriminator. Defaults to 0 (Raw).</param>
    public static void Write(Span<byte> dest, long offset, long length, byte codec = 0)
    {
        if (dest.Length < SizeBytes)
        {
            throw new ArgumentException(
                $"ArraySlot.Write requires a destination of at least {SizeBytes} bytes.",
                nameof(dest));
        }
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), offset, "Slot offset must be non-negative.");
        }
        if (length < 0 || length > MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length), length,
                $"Slot length must be in [0, {MaxLength}] (5-byte cap).");
        }

        BinaryPrimitives.WriteInt64LittleEndian(dest[..8], offset);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(8, 4), unchecked((uint)length));
        dest[12] = (byte)((length >> 32) & 0xFF);
        dest[13] = 0;
        dest[14] = 0;
        dest[15] = codec;
    }

    /// <summary>
    /// Reads a slot from <paramref name="src"/> at offset 0. The source span must be
    /// at least <see cref="SizeBytes"/> long.
    /// </summary>
    public static void Read(ReadOnlySpan<byte> src, out long offset, out long length, out byte codec)
    {
        if (src.Length < SizeBytes)
        {
            throw new ArgumentException(
                $"ArraySlot.Read requires a source of at least {SizeBytes} bytes.",
                nameof(src));
        }

        offset = BinaryPrimitives.ReadInt64LittleEndian(src[..8]);
        uint lengthLow = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(8, 4));
        long lengthHigh = (long)src[12] << 32;
        length = lengthHigh | lengthLow;
        codec = src[15];
    }
}

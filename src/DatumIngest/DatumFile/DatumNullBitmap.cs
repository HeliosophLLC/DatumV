namespace DatumIngest.DatumFile;

/// <summary>
/// A compact bit-vector marking which rows within a column page are null.
/// Bit <c>i</c> of the bitmap corresponds to row <c>i</c>: a set bit means the value is null.
/// Bit-major layout: row <c>i</c> is at byte <c>i / 8</c>, bit position <c>i % 8</c>.
/// </summary>
public sealed class DatumNullBitmap
{
    private readonly byte[] _bits;
    private int _nullCount;

    /// <summary>
    /// Creates a null bitmap for the given row count, with all bits cleared (no nulls).
    /// </summary>
    /// <param name="rowCount">Number of rows this bitmap covers.</param>
    public DatumNullBitmap(int rowCount)
    {
        RowCount = rowCount;
        _bits = new byte[ByteCount(rowCount)];
        _nullCount = 0;
    }

    /// <summary>Creates a null bitmap from an existing byte array and row count.</summary>
    private DatumNullBitmap(byte[] bits, int rowCount, int nullCount)
    {
        _bits = bits;
        RowCount = rowCount;
        _nullCount = nullCount;
    }

    /// <summary>The number of rows covered by this bitmap.</summary>
    public int RowCount { get; }

    /// <summary>The number of rows that are null.</summary>
    public int NullCount => _nullCount;

    /// <summary>Returns <c>true</c> when no rows are null. Avoids null-bitmap overhead in decoders.</summary>
    public bool AllNonNull => _nullCount == 0;

    /// <summary>Returns <c>true</c> if row <paramref name="rowIndex"/> is null.</summary>
    public bool IsNull(int rowIndex) => (_bits[rowIndex >> 3] & (1 << (rowIndex & 7))) != 0;

    /// <summary>Marks row <paramref name="rowIndex"/> as null. No-op if already marked.</summary>
    public void SetNull(int rowIndex)
    {
        int byteIndex = rowIndex >> 3;
        int bitMask = 1 << (rowIndex & 7);

        if ((_bits[byteIndex] & bitMask) == 0)
        {
            _bits[byteIndex] |= (byte)bitMask;
            _nullCount++;
        }
    }

    /// <summary>Returns the raw backing bytes. Caller must not mutate the returned array.</summary>
    public byte[] ToBytes() => _bits;

    /// <summary>
    /// Returns the number of bytes required to store a bitmap for <paramref name="rowCount"/> rows.
    /// </summary>
    public static int ByteCount(int rowCount) => (rowCount + 7) >> 3;

    /// <summary>
    /// Reconstructs a null bitmap from a raw byte array.
    /// The null count is recomputed by popcount.
    /// </summary>
    public static DatumNullBitmap FromBytes(byte[] bits, int rowCount)
    {
        int nullCount = CountNulls(bits.AsSpan(), rowCount);
        return new DatumNullBitmap(bits, rowCount, nullCount);
    }

    /// <summary>
    /// Reconstructs a null bitmap from a region of a raw byte buffer.
    /// Copies the relevant bytes into an internal array so the caller's buffer can be reused.
    /// </summary>
    public static DatumNullBitmap FromBytes(ReadOnlySpan<byte> source, int rowCount)
    {
        int byteCount = ByteCount(rowCount);
        byte[] bits = new byte[byteCount];
        source[..byteCount].CopyTo(bits);
        int nullCount = CountNulls(bits.AsSpan(), rowCount);
        return new DatumNullBitmap(bits, rowCount, nullCount);
    }

    /// <summary>
    /// Counts the number of null bits set in the bitmap bytes.
    /// </summary>
    private static int CountNulls(ReadOnlySpan<byte> bits, int rowCount)
    {
        int nullCount = 0;

        foreach (byte b in bits)
        {
            // Kernighan's popcount.
            int value = b;
            while (value != 0)
            {
                value &= value - 1;
                nullCount++;
            }
        }

        // Clamp: trailing bits beyond rowCount are not valid null markers.
        int trailingBits = (8 - (rowCount & 7)) & 7;

        if (trailingBits > 0 && bits.Length > 0)
        {
            byte lastByteMask = (byte)((1 << (rowCount & 7)) - 1);

            if (lastByteMask != 0)
            {
                int overcounted = System.Numerics.BitOperations.PopCount(
                    (uint)(bits[^1] & ~lastByteMask));
                nullCount -= overcounted;
            }
        }

        return nullCount;
    }
}

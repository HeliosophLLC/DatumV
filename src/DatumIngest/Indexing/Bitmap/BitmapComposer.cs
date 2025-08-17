using System.Numerics;
using System.Runtime.InteropServices;

namespace DatumIngest.Indexing.Bitmap;

/// <summary>
/// Provides SIMD-accelerated bitwise composition operations on bitmap byte spans.
/// These methods underpin multi-column predicate evaluation: AND (intersection),
/// OR (union), and NOT (complement) compose bitmaps from individual column predicates
/// into a combined row inclusion mask.
/// </summary>
internal static class BitmapComposer
{
    /// <summary>
    /// Computes the bitwise AND of two bitsets, writing the result into a new array.
    /// Both spans must have the same length.
    /// </summary>
    /// <param name="left">First operand bitset.</param>
    /// <param name="right">Second operand bitset.</param>
    /// <returns>A new byte array containing <c>left &amp; right</c>.</returns>
    internal static byte[] And(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        byte[] result = new byte[left.Length];
        And(left, right, result);
        return result;
    }

    /// <summary>
    /// Computes the bitwise AND of two bitsets into a pre-allocated destination.
    /// </summary>
    /// <param name="left">First operand bitset.</param>
    /// <param name="right">Second operand bitset.</param>
    /// <param name="destination">Destination buffer (must be at least <c>left.Length</c> bytes).</param>
    internal static void And(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, Span<byte> destination)
    {
        int vectorLength = Vector<byte>.Count;
        int i = 0;

        if (left.Length >= vectorLength)
        {
            ReadOnlySpan<Vector<byte>> leftVectors = MemoryMarshal.Cast<byte, Vector<byte>>(left);
            ReadOnlySpan<Vector<byte>> rightVectors = MemoryMarshal.Cast<byte, Vector<byte>>(right);
            Span<Vector<byte>> destinationVectors = MemoryMarshal.Cast<byte, Vector<byte>>(destination);

            for (int v = 0; v < leftVectors.Length; v++)
            {
                destinationVectors[v] = leftVectors[v] & rightVectors[v];
            }

            i = leftVectors.Length * vectorLength;
        }

        for (; i < left.Length; i++)
        {
            destination[i] = (byte)(left[i] & right[i]);
        }
    }

    /// <summary>
    /// Computes the bitwise OR of two bitsets, writing the result into a new array.
    /// Both spans must have the same length.
    /// </summary>
    /// <param name="left">First operand bitset.</param>
    /// <param name="right">Second operand bitset.</param>
    /// <returns>A new byte array containing <c>left | right</c>.</returns>
    internal static byte[] Or(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        byte[] result = new byte[left.Length];
        Or(left, right, result);
        return result;
    }

    /// <summary>
    /// Computes the bitwise OR of two bitsets into a pre-allocated destination.
    /// </summary>
    /// <param name="left">First operand bitset.</param>
    /// <param name="right">Second operand bitset.</param>
    /// <param name="destination">Destination buffer (must be at least <c>left.Length</c> bytes).</param>
    internal static void Or(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, Span<byte> destination)
    {
        int vectorLength = Vector<byte>.Count;
        int i = 0;

        if (left.Length >= vectorLength)
        {
            ReadOnlySpan<Vector<byte>> leftVectors = MemoryMarshal.Cast<byte, Vector<byte>>(left);
            ReadOnlySpan<Vector<byte>> rightVectors = MemoryMarshal.Cast<byte, Vector<byte>>(right);
            Span<Vector<byte>> destinationVectors = MemoryMarshal.Cast<byte, Vector<byte>>(destination);

            for (int v = 0; v < leftVectors.Length; v++)
            {
                destinationVectors[v] = leftVectors[v] | rightVectors[v];
            }

            i = leftVectors.Length * vectorLength;
        }

        for (; i < left.Length; i++)
        {
            destination[i] = (byte)(left[i] | right[i]);
        }
    }

    /// <summary>
    /// Computes the bitwise NOT of a bitset, writing the result into a new array.
    /// </summary>
    /// <param name="source">Source bitset to negate.</param>
    /// <param name="rowCount">
    /// Number of valid rows. Bits beyond <paramref name="rowCount"/> in the final byte
    /// are cleared to prevent false positives.
    /// </param>
    /// <returns>A new byte array containing <c>~source</c> (masked to <paramref name="rowCount"/>).</returns>
    internal static byte[] Not(ReadOnlySpan<byte> source, int rowCount)
    {
        byte[] result = new byte[source.Length];
        Not(source, rowCount, result);
        return result;
    }

    /// <summary>
    /// Computes the bitwise NOT of a bitset into a pre-allocated destination.
    /// </summary>
    /// <param name="source">Source bitset to negate.</param>
    /// <param name="rowCount">Number of valid rows (excess bits in the last byte are zeroed).</param>
    /// <param name="destination">Destination buffer.</param>
    internal static void Not(ReadOnlySpan<byte> source, int rowCount, Span<byte> destination)
    {
        int vectorLength = Vector<byte>.Count;
        int i = 0;

        if (source.Length >= vectorLength)
        {
            ReadOnlySpan<Vector<byte>> sourceVectors = MemoryMarshal.Cast<byte, Vector<byte>>(source);
            Span<Vector<byte>> destinationVectors = MemoryMarshal.Cast<byte, Vector<byte>>(destination);

            for (int v = 0; v < sourceVectors.Length; v++)
            {
                destinationVectors[v] = ~sourceVectors[v];
            }

            i = sourceVectors.Length * vectorLength;
        }

        for (; i < source.Length; i++)
        {
            destination[i] = (byte)~source[i];
        }

        // Mask off trailing bits beyond rowCount in the final byte.
        int trailingBits = rowCount & 7;

        if (trailingBits != 0 && destination.Length > 0)
        {
            byte mask = (byte)((1 << trailingBits) - 1);
            destination[^1] &= mask;
        }
    }

    /// <summary>
    /// Counts the total number of set bits in the bitset.
    /// </summary>
    /// <param name="bits">The bitset to count.</param>
    /// <returns>The population count.</returns>
    internal static int PopCount(ReadOnlySpan<byte> bits)
    {
        int count = 0;

        // Process 8 bytes at a time using ulong PopCount.
        ReadOnlySpan<ulong> ulongs = MemoryMarshal.Cast<byte, ulong>(bits);

        for (int i = 0; i < ulongs.Length; i++)
        {
            count += BitOperations.PopCount(ulongs[i]);
        }

        // Handle remaining bytes.
        int processed = ulongs.Length * 8;

        for (int i = processed; i < bits.Length; i++)
        {
            count += BitOperations.PopCount((uint)bits[i]);
        }

        return count;
    }

    /// <summary>
    /// Enumerates the zero-based indices of all set bits in the bitset, up to
    /// <paramref name="maxBit"/> (exclusive).
    /// </summary>
    /// <param name="bits">The bitset to scan.</param>
    /// <param name="maxBit">The upper bound on bit index (typically the row count).</param>
    /// <returns>An enumerable of set bit positions.</returns>
    internal static IEnumerable<int> EnumerateSetBits(ReadOnlySpan<byte> bits, int maxBit)
    {
        // ReadOnlySpan cannot be used in iterators, so copy to a local array.
        byte[] bitsArray = bits.ToArray();
        return EnumerateSetBitsCore(bitsArray, maxBit);
    }

    private static IEnumerable<int> EnumerateSetBitsCore(byte[] bits, int maxBit)
    {
        for (int byteIndex = 0; byteIndex < bits.Length; byteIndex++)
        {
            byte b = bits[byteIndex];

            if (b == 0)
            {
                continue;
            }

            int baseOffset = byteIndex << 3;

            for (int bitIndex = 0; bitIndex < 8; bitIndex++)
            {
                int position = baseOffset + bitIndex;

                if (position >= maxBit)
                {
                    yield break;
                }

                if ((b & (1 << bitIndex)) != 0)
                {
                    yield return position;
                }
            }
        }
    }

    /// <summary>
    /// Returns whether any bit is set in the bitset (fast non-zero check).
    /// </summary>
    /// <param name="bits">The bitset to check.</param>
    /// <returns><c>true</c> if at least one bit is set.</returns>
    internal static bool AnySet(ReadOnlySpan<byte> bits)
    {
        // Check 8 bytes at a time.
        ReadOnlySpan<ulong> ulongs = MemoryMarshal.Cast<byte, ulong>(bits);

        for (int i = 0; i < ulongs.Length; i++)
        {
            if (ulongs[i] != 0)
            {
                return true;
            }
        }

        int processed = ulongs.Length * 8;

        for (int i = processed; i < bits.Length; i++)
        {
            if (bits[i] != 0)
            {
                return true;
            }
        }

        return false;
    }
}

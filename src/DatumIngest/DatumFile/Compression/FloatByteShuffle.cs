using System.Runtime.CompilerServices;

namespace DatumIngest.DatumFile.Compression;

/// <summary>
/// Applies a BLOSC-style byte-lane shuffle to arrays of fixed-width numeric values
/// before compression. The shuffle interleaves byte lanes so that byte 0 of all
/// elements is contiguous, then byte 1, and so on. This significantly improves Zstd
/// compression ratios on correlated numeric data (embeddings, pixel values, sensor
/// readings) by creating long runs of similar byte values that the LZ77 back-reference
/// engine can exploit.
/// </summary>
public static class ByteLaneShuffle
{
    /// <summary>
    /// Shuffles <paramref name="input"/> floats into a byte-lane-interleaved layout
    /// suitable for compression. The output is four contiguous blocks of N bytes each:
    /// [byte0 of all N floats] [byte1 of all N floats] [byte2 of all N floats] [byte3 of all N floats].
    /// </summary>
    /// <param name="input">Source float array. Read as raw <c>float32</c>.</param>
    /// <param name="output">
    /// Destination byte span. Must have length equal to <c>input.Length * sizeof(float)</c>.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Shuffle(ReadOnlySpan<float> input, Span<byte> output)
    {
        Shuffle(System.Runtime.InteropServices.MemoryMarshal.AsBytes(input), output, sizeof(float));
    }

    /// <summary>
    /// Reverses the shuffle, reconstructing the original float values.
    /// <paramref name="input"/> must be in the four-block lane-interleaved format
    /// produced by <see cref="Shuffle(ReadOnlySpan{float}, Span{byte})"/>.
    /// </summary>
    /// <param name="input">Shuffled bytes. Length must be a multiple of 4.</param>
    /// <param name="output">Destination float span. Length must equal <c>input.Length / 4</c>.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Unshuffle(ReadOnlySpan<byte> input, Span<float> output)
    {
        int byteCount = output.Length * sizeof(float);
        Unshuffle(input[..byteCount], System.Runtime.InteropServices.MemoryMarshal.AsBytes(output), sizeof(float));
    }

    /// <summary>
    /// Shuffles <paramref name="input"/> bytes into a byte-lane-interleaved layout
    /// with <paramref name="elementSize"/> lanes. Input length must be a multiple of
    /// <paramref name="elementSize"/>. The output contains <paramref name="elementSize"/>
    /// contiguous blocks of <c>count</c> bytes each, where <c>count = input.Length / elementSize</c>.
    /// </summary>
    /// <param name="input">Source bytes (element data in native layout).</param>
    /// <param name="output">
    /// Destination byte span. Must have the same length as <paramref name="input"/>.
    /// </param>
    /// <param name="elementSize">Size of each element in bytes (e.g. 2, 4, or 8).</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Shuffle(ReadOnlySpan<byte> input, Span<byte> output, int elementSize)
    {
        int count = input.Length / elementSize;

        for (int i = 0; i < count; i++)
        {
            int srcBase = i * elementSize;
            for (int lane = 0; lane < elementSize; lane++)
            {
                output[lane * count + i] = input[srcBase + lane];
            }
        }
    }

    /// <summary>
    /// Reverses a byte-lane shuffle with <paramref name="elementSize"/> lanes,
    /// reconstructing the original element bytes.
    /// </summary>
    /// <param name="input">Shuffled bytes. Length must be a multiple of <paramref name="elementSize"/>.</param>
    /// <param name="output">
    /// Destination byte span. Must have the same length as <paramref name="input"/>.
    /// </param>
    /// <param name="elementSize">Size of each element in bytes (e.g. 2, 4, or 8).</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Unshuffle(ReadOnlySpan<byte> input, Span<byte> output, int elementSize)
    {
        int count = input.Length / elementSize;

        for (int i = 0; i < count; i++)
        {
            int dstBase = i * elementSize;
            for (int lane = 0; lane < elementSize; lane++)
            {
                output[dstBase + lane] = input[lane * count + i];
            }
        }
    }
}

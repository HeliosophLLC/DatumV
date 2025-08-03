using System.Runtime.CompilerServices;

namespace DatumIngest.DatumFile.Compression;

/// <summary>
/// Applies a BLOSC-style byte-lane shuffle to arrays of 32-bit float values
/// before compression. The shuffle interleaves the four byte lanes of each
/// <c>float32</c> so that byte 0 of all elements is contiguous, then byte 1,
/// and so on. This significantly improves Zstd compression ratios on correlated
/// floating-point data (embeddings, pixel values, sensor readings) by creating
/// long runs of similar byte values that the LZ77 back-reference engine can exploit.
/// </summary>
public static class FloatByteShuffle
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
        int count = input.Length;
        ReadOnlySpan<byte> inputBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(input);

        // Four byte-lane blocks: each block is count bytes long.
        Span<byte> lane0 = output[0..(count)];
        Span<byte> lane1 = output[(count)..(count * 2)];
        Span<byte> lane2 = output[(count * 2)..(count * 3)];
        Span<byte> lane3 = output[(count * 3)..(count * 4)];

        for (int i = 0; i < count; i++)
        {
            int srcBase = i * 4;
            lane0[i] = inputBytes[srcBase];
            lane1[i] = inputBytes[srcBase + 1];
            lane2[i] = inputBytes[srcBase + 2];
            lane3[i] = inputBytes[srcBase + 3];
        }
    }

    /// <summary>
    /// Reverses the shuffle, reconstructing the original float values.
    /// <paramref name="input"/> must be in the four-block lane-interleaved format
    /// produced by <see cref="Shuffle"/>.
    /// </summary>
    /// <param name="input">Shuffled bytes. Length must be a multiple of 4.</param>
    /// <param name="output">Destination float span. Length must equal <c>input.Length / 4</c>.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Unshuffle(ReadOnlySpan<byte> input, Span<float> output)
    {
        int count = output.Length;
        Span<byte> outputBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(output);

        ReadOnlySpan<byte> lane0 = input[0..(count)];
        ReadOnlySpan<byte> lane1 = input[(count)..(count * 2)];
        ReadOnlySpan<byte> lane2 = input[(count * 2)..(count * 3)];
        ReadOnlySpan<byte> lane3 = input[(count * 3)..(count * 4)];

        for (int i = 0; i < count; i++)
        {
            int dstBase = i * 4;
            outputBytes[dstBase] = lane0[i];
            outputBytes[dstBase + 1] = lane1[i];
            outputBytes[dstBase + 2] = lane2[i];
            outputBytes[dstBase + 3] = lane3[i];
        }
    }
}

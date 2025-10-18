using System.Buffers;
using System.Buffers.Binary;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes <see cref="DataKind.String"/> and <see cref="DataKind.JsonValue"/> column pages
/// using <see cref="DatumEncoding.VariableBytes"/> layout with Zstd compression.
/// </summary>
/// <remarks>
/// Layout of the uncompressed payload:
/// <c>nullBitmap[ceil(N/8)] | offsets:uint32[N+1] | pool:byte[offsets[N]]</c>.
/// <list type="bullet">
///   <item><c>offsets[0]</c> = 0 always.</item>
///   <item><c>offsets[i+1]</c> = <c>offsets[i] + UTF-8 byte length of row[i]</c>.</item>
///   <item>Null rows and empty strings both produce <c>offsets[i] == offsets[i+1]</c>;
///   the null bitmap distinguishes them.</item>
/// </list>
/// Storing <c>N+1</c> offsets rather than <c>N</c> (length, offset) pairs avoids an
/// addition per row during decoding and enables vectorised range reads.
/// </remarks>
internal sealed class StringColumnEncoder : DatumColumnEncoder
{
    /// <summary>
    /// When <c>true</c>, <see cref="BuildZoneMap"/> decodes each UTF-8 span to UTF-16
    /// on the stack and compares via <c>ReadOnlySpan&lt;char&gt;.SequenceCompareTo</c>
    /// — exact <see cref="string.CompareOrdinal(string?, string?)"/> semantics, at the
    /// cost of a UTF-8→UTF-16 decode per comparison. When <c>false</c>, compares UTF-8
    /// bytes directly via <c>SequenceCompareTo</c> — zero extra work, but produces a
    /// slightly-wider zone map for strings containing supplementary-plane code points
    /// (emoji, ancient scripts) mixed with Private Use Area characters. For ASCII/BMP
    /// data the two approaches are identical.
    /// </summary>
    private const bool UseExactOrdinalCompare = true;

    /// <summary>
    /// Per-call stack-allocated char buffer size used by <see cref="BuildZoneMapUtf16Ordinal"/>.
    /// Strings whose UTF-8 byte length exceeds this fall back to a one-shot heap array.
    /// </summary>
    private const int OrdinalCompareStackLimit = 1024;


    /// <inheritdoc/>
    public override DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;

        // Pass 1: compute total UTF-8 pool size by summing per-value byte length and
        // mark nulls in the bitmap. StringByteLength reads _p1 directly — no store
        // dispatch, no span construction, zero allocations.
        DatumNullBitmap nullBitmap = new(rowCount);
        uint nullCount = 0;
        int totalPoolBytes = 0;

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            DataValue value = values[rowIndex];
            if (value.IsNull)
            {
                nullBitmap.SetNull(rowIndex);
                nullCount++;
                continue;
            }
            totalPoolBytes += value.StringByteLength;
        }

        bool isJson = descriptor.Kind == DataKind.JsonValue;
        DatumZoneMap zoneMap = BuildZoneMap(nullCount, values, isJson, context);

        byte[] bitmapBytes = nullBitmap.ToBytes();
        int offsetsSize = (rowCount + 1) * 4;
        int rawLength = bitmapBytes.Length + offsetsSize + totalPoolBytes;
        byte[] raw = ArrayPool<byte>.Shared.Rent(rawLength);

        try
        {
            Buffer.BlockCopy(bitmapBytes, 0, raw, 0, bitmapBytes.Length);

            int offsetWrite = bitmapBytes.Length;
            int poolWrite = offsetWrite + offsetsSize;
            uint runningOffset = 0;

            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offsetWrite), runningOffset);
            offsetWrite += 4;

            // Pass 2: copy UTF-8 bytes directly from each page's store into the pooled
            // raw buffer. Page iteration is needed here because DataValue offsets are
            // relative to the originating batch's arena slice.
            foreach (PageSpan page in context.Pages)
            {
                IValueStore pageStore = page.ArenaLength > 0
                    ? context.Store.Slice(page.ArenaBase, page.ArenaLength)
                    : context.Store;

                int endRow = page.RowStart + page.RowCount;
                for (int rowIndex = page.RowStart; rowIndex < endRow; rowIndex++)
                {
                    DataValue value = values[rowIndex];

                    if (!value.IsNull)
                    {
                        ReadOnlySpan<byte> utf8 = value.AsUtf8Span(pageStore);
                        utf8.CopyTo(raw.AsSpan(poolWrite, utf8.Length));
                        poolWrite += utf8.Length;
                        runningOffset += (uint)utf8.Length;
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offsetWrite), runningOffset);
                    offsetWrite += 4;
                }
            }

            (byte[] compressed, int compressedLength) = DatumCompressor.Compress(raw.AsSpan(0, rawLength), DatumCompression.Zstd);

            return new DatumEncodedPage(compressed, compressedLength, DatumEncoding.VariableBytes, DatumCompression.Zstd, rawLength, zoneMap);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private static DatumZoneMap BuildZoneMap(
        uint nullCount,
        IReadOnlyList<DataValue> values,
        bool isJson,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;
        if (nullCount == (uint)rowCount) return new DatumZoneMap(nullCount);

        // JSON values do not have a meaningful lexicographic range for zone map pruning.
        if (isJson) return new DatumZoneMap(nullCount);

#pragma warning disable CS0162 // Unreachable code (intentional — const toggle)
        return UseExactOrdinalCompare
            ? BuildZoneMapUtf16Ordinal(nullCount, values, context)
            : BuildZoneMapUtf8Bytewise(nullCount, values, context);
#pragma warning restore CS0162
    }

    /// <summary>
    /// Zero-allocation per-row scan that tracks min/max by UTF-8 byte-span comparison.
    /// Matches <see cref="string.CompareOrdinal(string?, string?)"/> exactly for any
    /// content without supplementary-plane code points. Materializes the winning two
    /// strings only at the end.
    /// </summary>
    private static DatumZoneMap BuildZoneMapUtf8Bytewise(
        uint nullCount,
        IReadOnlyList<DataValue> values,
        DatumEncoderContext context)
    {
        int minRow = -1, maxRow = -1;
        IValueStore? minStore = null, maxStore = null;
        ReadOnlySpan<byte> minBytes = default, maxBytes = default;

        foreach (PageSpan page in context.Pages)
        {
            IValueStore pageStore = page.ArenaLength > 0
                ? context.Store.Slice(page.ArenaBase, page.ArenaLength)
                : context.Store;

            int endRow = page.RowStart + page.RowCount;
            for (int rowIndex = page.RowStart; rowIndex < endRow; rowIndex++)
            {
                DataValue value = values[rowIndex];
                if (value.IsNull) continue;

                ReadOnlySpan<byte> utf8 = value.AsUtf8Span(pageStore);

                if (minRow < 0 || utf8.SequenceCompareTo(minBytes) < 0)
                {
                    minRow = rowIndex;
                    minStore = pageStore;
                    minBytes = utf8;
                }
                if (maxRow < 0 || utf8.SequenceCompareTo(maxBytes) > 0)
                {
                    maxRow = rowIndex;
                    maxStore = pageStore;
                    maxBytes = utf8;
                }
            }
        }

        if (minRow < 0) return new DatumZoneMap(nullCount);

        string minimum = values[minRow].AsString(minStore!);
        string maximum = values[maxRow].AsString(maxStore!);
        return new DatumZoneMap(nullCount, DataKind.String, minimum, maximum);
    }

    /// <summary>
    /// Exact <see cref="string.CompareOrdinal(string?, string?)"/> semantics by
    /// decoding each UTF-8 span to UTF-16 into a pair of reused stack buffers, then
    /// comparing as <c>ReadOnlySpan&lt;char&gt;</c>. Zero heap allocation for strings
    /// whose UTF-8 byte length is &lt;= <see cref="OrdinalCompareStackLimit"/>; falls
    /// back to a one-shot <c>char[]</c> for longer strings.
    /// </summary>
    private static DatumZoneMap BuildZoneMapUtf16Ordinal(
        uint nullCount,
        IReadOnlyList<DataValue> values,
        DatumEncoderContext context)
    {
        Span<char> aStack = stackalloc char[OrdinalCompareStackLimit];
        Span<char> bStack = stackalloc char[OrdinalCompareStackLimit];

        int minRow = -1, maxRow = -1;
        IValueStore? minStore = null, maxStore = null;
        ReadOnlySpan<byte> minBytes = default, maxBytes = default;

        foreach (PageSpan page in context.Pages)
        {
            IValueStore pageStore = page.ArenaLength > 0
                ? context.Store.Slice(page.ArenaBase, page.ArenaLength)
                : context.Store;

            int endRow = page.RowStart + page.RowCount;
            for (int rowIndex = page.RowStart; rowIndex < endRow; rowIndex++)
            {
                DataValue value = values[rowIndex];
                if (value.IsNull) continue;

                ReadOnlySpan<byte> utf8 = value.AsUtf8Span(pageStore);

                if (minRow < 0 || CompareUtf16Ordinal(utf8, minBytes, aStack, bStack) < 0)
                {
                    minRow = rowIndex;
                    minStore = pageStore;
                    minBytes = utf8;
                }
                if (maxRow < 0 || CompareUtf16Ordinal(utf8, maxBytes, aStack, bStack) > 0)
                {
                    maxRow = rowIndex;
                    maxStore = pageStore;
                    maxBytes = utf8;
                }
            }
        }

        if (minRow < 0) return new DatumZoneMap(nullCount);

        string minimum = values[minRow].AsString(minStore!);
        string maximum = values[maxRow].AsString(maxStore!);
        return new DatumZoneMap(nullCount, DataKind.String, minimum, maximum);
    }

    /// <summary>
    /// Decodes both UTF-8 spans into the provided char buffers (or heap arrays if
    /// larger than the stack limit), then compares code-unit-wise. Result is
    /// equivalent to <c>string.CompareOrdinal</c> on the decoded strings.
    /// </summary>
    private static int CompareUtf16Ordinal(
        ReadOnlySpan<byte> a, ReadOnlySpan<byte> b,
        Span<char> aStack, Span<char> bStack)
    {
        // UTF-8 to UTF-16 never produces more chars than input bytes.
        Span<char> aChars = a.Length <= OrdinalCompareStackLimit ? aStack : new char[a.Length];
        Span<char> bChars = b.Length <= OrdinalCompareStackLimit ? bStack : new char[b.Length];
        int aLen = System.Text.Encoding.UTF8.GetChars(a, aChars);
        int bLen = System.Text.Encoding.UTF8.GetChars(b, bChars);
        return aChars[..aLen].SequenceCompareTo(bChars[..bLen]);
    }
}

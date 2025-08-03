using System.Buffers.Binary;
using System.Text;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;
using DatumIngest.Indexing;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes a low-cardinality column page using <see cref="DatumEncoding.DictionaryRLE"/>
/// with Zstd compression.
/// </summary>
/// <remarks>
/// This encoder is applicable to any <see cref="DataKind"/> when the number of distinct
/// non-null values in the row group is small enough that using a per-page dictionary
/// reduces the payload size compared to inline encoding.
/// <para>
/// Layout of the uncompressed payload:
/// <list type="number">
///   <item><c>nullBitmap[ceil(N/8)]</c></item>
///   <item><c>dictionaryEntryCount:uint16</c> — number of distinct values (max 65 535).</item>
///   <item>
///     For each dictionary entry: the <see cref="DataValue"/> serialized via
///     <c>IndexWriter.WriteDataValue</c>.
///   </item>
///   <item>
///     <c>codes:byte[N]</c> when <c>dictionaryEntryCount &lt;= 256</c>, or
///     <c>codes:uint16[N]</c> when <c>dictionaryEntryCount &lt;= 65 535</c>.
///     Null rows store code <c>0</c> (the null sentinel; the null bitmap is authoritative).
///   </item>
/// </list>
/// </para>
/// </remarks>
internal sealed class DictionaryColumnEncoder : DatumColumnEncoder
{
    /// <summary>Maximum number of distinct values before this encoder is no longer preferable.</summary>
    internal const int MaxDictionarySize = 65_534; // reserves one code index for null sentinel

    /// <inheritdoc/>
    public override DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;
        DatumNullBitmap nullBitmap = new(rowCount);
        uint nullCount = 0;

        // Build the dictionary in insertion order.
        Dictionary<DataValue, int> codeMap = new(rowCount);
        List<DataValue> dictionary = new();
        int[] codes = new int[rowCount];

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            DataValue value = values[rowIndex];

            if (value.IsNull)
            {
                nullBitmap.SetNull(rowIndex);
                codes[rowIndex] = 0; // null sentinel code; ignored by decoder due to null bitmap
                nullCount++;
                continue;
            }

            if (!codeMap.TryGetValue(value, out int code))
            {
                code = dictionary.Count + 1; // codes start at 1; 0 is the null sentinel
                codeMap[value] = code;
                dictionary.Add(value);
            }

            codes[rowIndex] = code;
        }

        DatumZoneMap zoneMap = new(nullCount, null, null);

        // Serialize using BinaryWriter for the DataValue entries.
        using MemoryStream dictStream = new();
        using BinaryWriter dictWriter = new(dictStream, System.Text.Encoding.UTF8, leaveOpen: true);

        // Write dictionary: entry count + each DataValue
        dictWriter.Write((ushort)dictionary.Count);
        foreach (DataValue entry in dictionary)
        {
            IndexWriter.WriteDataValue(dictWriter, entry);
        }
        dictWriter.Flush();
        byte[] dictBytes = dictStream.ToArray();

        byte[] bitmapBytes = nullBitmap.ToBytes();
        bool wideCode = dictionary.Count > 255;
        int codesSize = rowCount * (wideCode ? 2 : 1);

        byte[] raw = new byte[bitmapBytes.Length + dictBytes.Length + codesSize];
        bitmapBytes.CopyTo(raw, 0);
        dictBytes.CopyTo(raw, bitmapBytes.Length);

        int codeWrite = bitmapBytes.Length + dictBytes.Length;
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (wideCode)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(codeWrite), (ushort)codes[rowIndex]);
                codeWrite += 2;
            }
            else
            {
                raw[codeWrite++] = (byte)codes[rowIndex];
            }
        }

        byte[] compressed = DatumCompressor.Compress(raw, DatumCompression.Zstd);

        return new DatumEncodedPage(compressed, DatumEncoding.DictionaryRLE, DatumCompression.Zstd, raw.Length, zoneMap);
    }
}

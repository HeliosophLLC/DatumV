using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Heliosoph.DatumV.Model;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Serialization.Fits;

/// <summary>
/// Parsed BINTABLE column descriptor: position, declared name, TFORM
/// type/repeat, and the byte offset of the column inside one on-disk
/// row.
/// </summary>
internal sealed class FitsBinTableColumn
{
    /// <summary>Zero-based column index within the table (i.e. <c>TFORMn</c> with n = Index + 1).</summary>
    public required int Index { get; init; }

    /// <summary>Column name from <c>TTYPEn</c>, or synthesized <c>col_n</c> when the card is absent.</summary>
    public required string Name { get; init; }

    /// <summary>Parsed <c>TFORMn</c> for this column.</summary>
    public required FitsTForm Form { get; init; }

    /// <summary>Byte offset of this column's first byte within one BINTABLE row.</summary>
    public required int ByteOffset { get; init; }
}

/// <summary>
/// Decodes FITS BINTABLE rows: parses <c>TFORMn</c> / <c>TTYPEn</c> headers
/// into typed columns, then streams the data section row-by-row applying
/// per-column big-endian decoders.
/// </summary>
internal static class FitsBinTableReader
{
    private const int DefaultBatchSize = 1024;

    /// <summary>
    /// Builds the per-column descriptor list from a BINTABLE HDU's cards.
    /// Throws if <c>TFIELDS</c> or any required <c>TFORMn</c> is missing /
    /// invalid.
    /// </summary>
    internal static IReadOnlyList<FitsBinTableColumn> ParseColumns(FitsHduDescriptor hdu)
    {
        if (hdu.Kind != FitsHduKind.BinTable)
        {
            throw new InvalidOperationException(
                $"FitsBinTableReader.ParseColumns called on non-BINTABLE HDU (kind={hdu.Kind}).");
        }

        int columnCount = hdu.TFields
            ?? throw new InvalidDataException("BINTABLE HDU is missing the TFIELDS card.");

        FitsBinTableColumn[] columns = new FitsBinTableColumn[columnCount];
        int rowOffset = 0;
        for (int i = 0; i < columnCount; i++)
        {
            int n = i + 1;
            string? tform = FindCardValue(hdu.Cards, $"TFORM{n}")
                ?? throw new InvalidDataException(
                    $"BINTABLE HDU has TFIELDS={columnCount} but TFORM{n} is missing.");
            string name = FindCardValue(hdu.Cards, $"TTYPE{n}") ?? $"col_{n}";

            FitsTForm form = FitsTForm.Parse(tform);
            columns[i] = new FitsBinTableColumn
            {
                Index = i,
                Name = name,
                Form = form,
                ByteOffset = rowOffset,
            };
            rowOffset += form.RowByteSize;
        }

        if (rowOffset != hdu.RowByteSize && hdu.NAxis >= 1)
        {
            // NAXIS1 (row byte width) and the sum of TFORMs must agree.
            // A mismatch usually means the file has variable-length columns we
            // refused at TFORM-parse time, or it's malformed. Surface clearly.
            throw new InvalidDataException(
                $"BINTABLE row byte width mismatch: NAXIS1={hdu.RowByteSize} but TFORM sum = {rowOffset}.");
        }

        return columns;
    }

    /// <summary>
    /// Builds a <see cref="Schema"/> from the parsed columns. Used by the TVF's
    /// plan-time validation path so downstream column references type-check.
    /// </summary>
    internal static Schema BuildSchema(IReadOnlyList<FitsBinTableColumn> columns)
    {
        ColumnInfo[] infos = new ColumnInfo[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            FitsBinTableColumn col = columns[i];
            // All BINTABLE columns are nullable in v1 — proper TNULL / NaN
            // handling lands with the TNULL feature; until then a value
            // matching TNULL or a NaN flows through as the literal pixel
            // value, but the schema position must permit NULL so a future
            // sweep doesn't break.
            infos[i] = new ColumnInfo(col.Name, col.Form.ElementKind, nullable: true)
            {
                IsArray = col.Form.IsArray,
            };
        }
        return new Schema(infos);
    }

    /// <summary>
    /// Streams rows from the BINTABLE data section. The stream must be
    /// positioned at the HDU's <c>DataOffset</c> on entry; on successful
    /// completion it sits at <c>DataOffset + DataByteSize</c> (the caller is
    /// responsible for advancing past padding).
    /// </summary>
    internal static async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        FitsHduDescriptor hdu,
        Stream stream,
        IReadOnlyList<FitsBinTableColumn> columns,
        ColumnLookup outputLookup,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long nRows = hdu.NAxis >= 2 ? hdu.NAxisN[1] : 0;
        int rowBytes = (int)hdu.RowByteSize;
        if (rowBytes == 0)
        {
            yield break;
        }

        byte[] rowBuffer = new byte[rowBytes];
        RowBatch? batch = null;

        for (long r = 0; r < nRows; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FitsHduDescriptor.ReadExactly(stream, rowBuffer);

            batch ??= context.RentRowBatch(outputLookup);
            DataValue[] values = context.Pool.RentDataValues(columns.Count);
            for (int c = 0; c < columns.Count; c++)
            {
                values[c] = DecodeCell(columns[c], rowBuffer, batch.Arena);
            }
            batch.Add(values);

            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null && batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Decodes one row's bytes for a single column into a <see cref="DataValue"/>.
    /// All numeric reads are big-endian per the FITS spec; string fields are
    /// right-trimmed of trailing spaces.
    /// </summary>
    private static DataValue DecodeCell(
        FitsBinTableColumn column,
        ReadOnlySpan<byte> rowBytes,
        IValueStore arena)
    {
        ReadOnlySpan<byte> cellBytes = rowBytes.Slice(column.ByteOffset, column.Form.RowByteSize);
        FitsTForm form = column.Form;

        // String column (A): repeat is the field width, value is ASCII text.
        if (form.TypeChar == 'A')
        {
            string raw = Encoding.ASCII.GetString(cellBytes).TrimEnd(' ', '\0');
            return DataValue.FromString(raw, arena);
        }

        // Array column (repeat > 1, non-A).
        if (form.IsArray)
        {
            return DecodeArrayCell(form, cellBytes, arena);
        }

        // Scalar column.
        return DecodeScalarCell(form, cellBytes);
    }

    private static DataValue DecodeScalarCell(FitsTForm form, ReadOnlySpan<byte> bytes) =>
        form.TypeChar switch
        {
            'L' => DataValue.FromBoolean(bytes[0] == (byte)'T'),
            'B' => DataValue.FromUInt8(bytes[0]),
            'I' => DataValue.FromInt16(BinaryPrimitives.ReadInt16BigEndian(bytes)),
            'J' => DataValue.FromInt32(BinaryPrimitives.ReadInt32BigEndian(bytes)),
            'K' => DataValue.FromInt64(BinaryPrimitives.ReadInt64BigEndian(bytes)),
            'E' => DataValue.FromFloat32(BinaryPrimitives.ReadSingleBigEndian(bytes)),
            'D' => DataValue.FromFloat64(BinaryPrimitives.ReadDoubleBigEndian(bytes)),
            _ => throw new InvalidOperationException(
                $"Internal: unexpected TFORM type '{form.TypeChar}' in scalar decode."),
        };

    private static DataValue DecodeArrayCell(FitsTForm form, ReadOnlySpan<byte> bytes, IValueStore arena)
    {
        int n = form.Repeat;
        switch (form.TypeChar)
        {
            case 'B':
            {
                byte[] arr = bytes.ToArray();
                return DataValue.FromByteArray(arr, arena);
            }
            case 'I':
            {
                short[] arr = new short[n];
                for (int i = 0; i < n; i++) arr[i] = BinaryPrimitives.ReadInt16BigEndian(bytes.Slice(i * 2, 2));
                return DataValue.FromArenaArray<short>(arr, DataKind.Int16, arena);
            }
            case 'J':
            {
                int[] arr = new int[n];
                for (int i = 0; i < n; i++) arr[i] = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(i * 4, 4));
                return DataValue.FromArenaArray<int>(arr, DataKind.Int32, arena);
            }
            case 'K':
            {
                long[] arr = new long[n];
                for (int i = 0; i < n; i++) arr[i] = BinaryPrimitives.ReadInt64BigEndian(bytes.Slice(i * 8, 8));
                return DataValue.FromArenaArray<long>(arr, DataKind.Int64, arena);
            }
            case 'E':
            {
                float[] arr = new float[n];
                for (int i = 0; i < n; i++) arr[i] = BinaryPrimitives.ReadSingleBigEndian(bytes.Slice(i * 4, 4));
                return DataValue.FromArenaArray<float>(arr, DataKind.Float32, arena);
            }
            case 'D':
            {
                double[] arr = new double[n];
                for (int i = 0; i < n; i++) arr[i] = BinaryPrimitives.ReadDoubleBigEndian(bytes.Slice(i * 8, 8));
                return DataValue.FromArenaArray<double>(arr, DataKind.Float64, arena);
            }
            case 'L':
            {
                bool[] arr = new bool[n];
                for (int i = 0; i < n; i++) arr[i] = bytes[i] == (byte)'T';
                // No FromArenaArray<bool> path — bool is tricky over span bytes.
                // Pack as UInt8 array instead and document the convention.
                byte[] packed = new byte[n];
                for (int i = 0; i < n; i++) packed[i] = arr[i] ? (byte)1 : (byte)0;
                return DataValue.FromByteArray(packed, arena);
            }
            default:
                throw new InvalidOperationException(
                    $"Internal: unexpected TFORM type '{form.TypeChar}' in array decode.");
        }
    }

    private static string? FindCardValue(IReadOnlyList<FitsCard> cards, string keyword)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            if (string.Equals(cards[i].Keyword, keyword, StringComparison.Ordinal))
            {
                return cards[i].AsString();
            }
        }
        return null;
    }
}

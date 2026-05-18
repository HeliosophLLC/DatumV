using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Fits;

/// <summary>
/// Deserializes FITS files into <see cref="RowBatch"/> streams for the
/// generic ingest pipeline. Yields one row per HDU with the same shape
/// the <c>open_fits_hdus</c> table-valued function produces — the most
/// informative default for an unknown FITS file. Recipes that want the
/// pixel previews or binary-table rows directly should use the
/// <c>open_fits_images</c> / <c>open_fits_table</c> TVFs instead.
/// </summary>
public sealed class FitsDeserializer : IFormatDeserializer
{
    private const int DefaultBatchSize = 64;

    private static readonly ColumnLookup OutputColumnLookup = new(
    [
        "hdu_index", "kind", "extname", "extver", "bitpix",
        "naxis", "naxisn", "nrows", "ncols", "header",
    ]);

    private readonly FileFormatDescriptor _descriptor;

    /// <summary>Creates a deserializer for the given file descriptor.</summary>
    public FitsDeserializer(FileFormatDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> DeserializeAsync(
        SerializationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using Stream stream = await _descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);

        // FITS' header walk relies on Position; gzipped inputs aren't
        // seekable, so we copy through a MemoryStream for the .fits.gz case.
        Stream walkable = stream;
        MemoryStream? buffered = null;
        if (!stream.CanSeek)
        {
            buffered = new MemoryStream();
            await stream.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
            buffered.Position = 0;
            walkable = buffered;
        }

        try
        {
            RowBatch? batch = null;
            int hduIndex = 0;

            while (FitsHduDescriptor.TryReadNext(
                walkable, isPrimary: hduIndex == 0, out FitsHduDescriptor? hdu))
            {
                cancellationToken.ThrowIfCancellationRequested();

                batch ??= context.Pool.RentRowBatch(OutputColumnLookup, DefaultBatchSize);
                DataValue[] row = BuildRow(hdu, hduIndex, batch.Arena, context);
                batch.Add(row);

                hdu.SkipData(walkable);
                hduIndex++;

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
        finally
        {
            buffered?.Dispose();
        }
    }

    private static DataValue[] BuildRow(
        FitsHduDescriptor hdu,
        int hduIndex,
        IValueStore arena,
        SerializationContext context)
    {
        DataValue[] row = context.Pool.RentDataValues(OutputColumnLookup.Count);

        row[0] = DataValue.FromInt64(hduIndex);
        row[1] = DataValue.FromString(KindToString(hdu.Kind), arena);
        row[2] = hdu.ExtName is null ? DataValue.Null(DataKind.String) : DataValue.FromString(hdu.ExtName, arena);
        row[3] = hdu.ExtVer is null ? DataValue.Null(DataKind.Int32) : DataValue.FromInt32(hdu.ExtVer.Value);
        row[4] = hdu.BitPix == 0 ? DataValue.Null(DataKind.Int32) : DataValue.FromInt32(hdu.BitPix);
        row[5] = DataValue.FromInt32(hdu.NAxis);
        row[6] = DataValue.FromArenaArray<int>(
            hdu.NAxisN is int[] arr ? arr : [.. hdu.NAxisN],
            DataKind.Int32,
            arena);

        if (hdu.Kind == FitsHduKind.BinTable && hdu.NAxis >= 2)
        {
            row[7] = DataValue.FromInt64(hdu.NAxisN[1]);
            row[8] = hdu.TFields is null ? DataValue.Null(DataKind.Int32) : DataValue.FromInt32(hdu.TFields.Value);
        }
        else
        {
            row[7] = DataValue.Null(DataKind.Int64);
            row[8] = DataValue.Null(DataKind.Int32);
        }

        row[9] = DataValue.FromJson(FitsHeaderJson.Build(hdu.Cards), arena);
        return row;
    }

    private static string KindToString(FitsHduKind kind) =>
        kind switch
        {
            FitsHduKind.Primary => "primary",
            FitsHduKind.Image => "image",
            FitsHduKind.BinTable => "bintable",
            FitsHduKind.AsciiTable => "asciitable",
            _ => "unknown",
        };
}

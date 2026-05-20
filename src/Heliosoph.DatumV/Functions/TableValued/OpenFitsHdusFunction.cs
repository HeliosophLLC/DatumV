using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Fits;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>open_fits_hdus(path) → table</c>. Opens a FITS file and yields one
/// row per Header-Data Unit (HDU) with its parsed metadata, without
/// reading any pixel or table-row data. The interrogation TVF: use it to
/// see what's inside an opaque FITS file before deciding whether to pull
/// images or table rows out of it.
/// </summary>
/// <remarks>
/// <para>
/// FITS files are a concatenation of HDUs: each one is a 2880-byte-block
/// header section (36 × 80-char ASCII cards per block) terminated by an
/// <c>END</c> card, followed by an optional data section padded to the
/// next 2880-byte boundary. Real-world files (JWST L2, SDSS, DESI, JWST
/// NIRCam, …) typically have 5–12 HDUs that mix image and binary-table
/// extensions in a single file.
/// </para>
/// <para>
/// Each row's <c>header</c> column carries the full card list as a JSON
/// array of <c>{key, value, comment}</c> objects, so callers can inspect
/// any keyword the typed columns don't surface (WCS keywords, instrument
/// metadata, observation IDs, etc.) with PG-style JSON operators.
/// </para>
/// <para>
/// Transparent <c>.gz</c> support: the path is wrapped in a
/// <see cref="FileFormatDescriptor"/>, so callers pass the raw archive
/// filename (e.g. <c>jw01234.fits.gz</c>) and the descriptor materialises
/// to a temp file on first open.
/// </para>
/// </remarks>
public sealed class OpenFitsHdusFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup OutputColumnLookup = new(
    [
        "hdu_index", "kind", "extname", "extver", "bitpix",
        "naxis", "naxisn", "nrows", "ncols", "header",
    ]);

    private const int DefaultBatchSize = 64;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_fits_hdus";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens a FITS file and yields one row per HDU with its parsed metadata: " +
        "open_fits_hdus(path). Columns: (hdu_index INT64, kind STRING, extname STRING?, " +
        "extver INT32?, bitpix INT32?, naxis INT32, naxisn INT32[], nrows INT64?, " +
        "ncols INT32?, header JSON). Read this first to see what's inside an unfamiliar FITS file.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters: [new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String))],
            FixedOutputSchema: new Schema(
            [
                new ColumnInfo("hdu_index", DataKind.Int64, nullable: false),
                new ColumnInfo("kind", DataKind.String, nullable: false),
                new ColumnInfo("extname", DataKind.String, nullable: true),
                new ColumnInfo("extver", DataKind.Int32, nullable: true),
                new ColumnInfo("bitpix", DataKind.Int32, nullable: true),
                new ColumnInfo("naxis", DataKind.Int32, nullable: false),
                new ColumnInfo("naxisn", DataKind.Int32, nullable: false) { IsArray = true },
                new ColumnInfo("nrows", DataKind.Int64, nullable: true),
                new ColumnInfo("ncols", DataKind.Int32, nullable: true),
                new ColumnInfo("header", DataKind.Json, nullable: false),
            ])),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        IValueStore constantStore,
        CancellationToken cancellationToken)
    {
        if (argumentKinds.Length != 1)
        {
            throw new FunctionArgumentException(Name,
                "requires 1 argument: open_fits_hdus(path).");
        }
        else if (argumentKinds[0] != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (path) must be STRING.");
        }
        return Signatures[0].FixedOutputSchema!;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        ValueRef[] arguments,
        ExecutionContext context)
    {
        if (arguments.Length != 1)
        {
            throw new ArgumentException(
                "open_fits_hdus requires 1 argument: (path).");
        }

        string path = arguments[0].AsString();

        using FileFormatDescriptor descriptor = new(path);
        await foreach (RowBatch batch in StreamRowsAsync(descriptor, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        FileFormatDescriptor descriptor,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken = context.CancellationToken;
        await using Stream stream = await descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);

        // FITS' header walk relies on Position to capture / restore offsets.
        // FileFormatDescriptor may hand back a buffering stream over a gzipped
        // input; that stream isn't seekable, so we copy through a MemoryStream
        // for the rare-but-real ".fits.gz" case. Uncompressed files stream
        // directly from disk.
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

                batch ??= context.RentRowBatch(OutputColumnLookup);
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
        ExecutionContext context)
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
            row[7] = DataValue.FromInt64(hdu.NAxisN[1]); // nrows = NAXIS2
            row[8] = hdu.TFields is null
                ? DataValue.Null(DataKind.Int32)
                : DataValue.FromInt32(hdu.TFields.Value);
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

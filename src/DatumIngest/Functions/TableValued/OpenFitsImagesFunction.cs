using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Fits;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// <c>open_fits_images(path) → table</c>. Opens a FITS file and yields one
/// row per image HDU (primary image + every <c>XTENSION='IMAGE'</c>
/// extension that carries pixel data), surfacing both a displayable
/// preview PNG and the scientific Float32 pixel array. Binary-table and
/// header-only HDUs are skipped — they have no image to project.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Output columns.</strong> Pixel data appears in two forms:
/// <c>image</c> (a PNG-encoded grayscale preview, min/max-stretched to
/// 8-bit, populated when NAXIS == 2) and <c>sci</c> (the per-pixel
/// scientific value in Float32 with BSCALE/BZERO applied, populated
/// whenever the HDU has pixel data). Recipes that need pixel statistics,
/// NaN masks, or per-pixel SQL math read <c>sci</c>; recipes that just
/// want a thumbnail for browse or chat read <c>image</c>.
/// </para>
/// <para>
/// <strong>Shape.</strong> The Float32 array in <c>sci</c> is flat — the
/// per-row <c>header</c> column carries NAXIS / NAXISn so callers can
/// reshape it. Once typed multi-dim arrays land everywhere the kind will
/// upgrade to a shape-bearing Float32 tensor without changing the
/// TVF surface.
/// </para>
/// </remarks>
public sealed class OpenFitsImagesFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup OutputColumnLookup = new(
    [
        "hdu_index", "extname", "image", "sci", "header",
    ]);

    private const int DefaultBatchSize = 32;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_fits_images";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens a FITS file and yields one row per image HDU: open_fits_images(path). " +
        "Columns: (hdu_index INT64, extname STRING?, image IMAGE?, sci FLOAT32[]?, " +
        "header JSON). Skips binary-table and header-only HDUs. image is a min/max-stretched " +
        "PNG preview (NAXIS==2 only); sci is the scientific Float32 array with BSCALE/BZERO applied.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters: [new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String))],
            FixedOutputSchema: new Schema(
            [
                new ColumnInfo("hdu_index", DataKind.Int64, nullable: false),
                new ColumnInfo("extname", DataKind.String, nullable: true),
                new ColumnInfo("image", DataKind.Image, nullable: true),
                new ColumnInfo("sci", DataKind.Float32, nullable: true) { IsArray = true },
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
                "requires 1 argument: open_fits_images(path).");
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
            throw new ArgumentException("open_fits_images requires 1 argument: (path).");
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

        // Mirror open_fits_hdus: buffer non-seekable (gzipped) inputs so the
        // descriptor walker can jump to data offsets and skip past padding.
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

                if (IsImageRow(hdu))
                {
                    batch ??= context.RentRowBatch(OutputColumnLookup);

                    // Read pixel data BEFORE SkipData — the reader consumes
                    // exactly DataByteSize bytes; SkipData handles the
                    // 2880-byte padding tail.
                    walkable.Position = hdu.DataOffset;
                    (DataValue sci, DataValue image) = FitsImageReader.ReadImage(hdu, walkable, batch.Arena);

                    DataValue[] row = BuildRow(hdu, hduIndex, sci, image, batch.Arena, context);
                    batch.Add(row);

                    if (batch.IsFull)
                    {
                        yield return batch;
                        batch = null;
                    }
                }

                hdu.SkipData(walkable);
                hduIndex++;
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

    /// <summary>
    /// True when this HDU is an image we should surface: explicit IMAGE
    /// extension, or a primary HDU that actually carries pixel data
    /// (NAXIS > 0). Bintables, ASCII tables, header-only primaries, and
    /// unknown XTENSIONs are skipped.
    /// </summary>
    private static bool IsImageRow(FitsHduDescriptor hdu)
    {
        if (!hdu.HasData) return false;
        return hdu.Kind == FitsHduKind.Image
            || (hdu.Kind == FitsHduKind.Primary && hdu.NAxis > 0);
    }

    private static DataValue[] BuildRow(
        FitsHduDescriptor hdu,
        int hduIndex,
        DataValue sci,
        DataValue image,
        IValueStore arena,
        ExecutionContext context)
    {
        DataValue[] row = context.Pool.RentDataValues(OutputColumnLookup.Count);
        row[0] = DataValue.FromInt64(hduIndex);
        row[1] = hdu.ExtName is null ? DataValue.Null(DataKind.String) : DataValue.FromString(hdu.ExtName, arena);
        row[2] = image;
        row[3] = sci;
        row[4] = DataValue.FromJson(FitsHeaderJson.Build(hdu.Cards), arena);
        return row;
    }
}

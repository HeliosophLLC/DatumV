using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Functions.Image;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Ingestion.Sampling;

/// <summary>
/// Collects a representative sample of rows during ingestion using reservoir sampling
/// (Algorithm R). After all rows have been considered, call <see cref="Build"/> to
/// produce a <see cref="SamplePreview"/> with JSON-friendly values.
/// </summary>
/// <remarks>
/// <para>
/// Image values are resized to fit within <see cref="MaxThumbnailDimension"/>×<see cref="MaxThumbnailDimension"/>
/// (preserving aspect ratio), re-encoded as PNG, and stored as <c>"base64://…"</c> strings.
/// Byte-array columns (<see cref="DataKind.UInt8"/> + <c>IsArray</c>) are represented
/// as the sentinel string <c>"[binary data]"</c>. Vectors, matrices, and tensors are
/// represented as nested numeric arrays.
/// </para>
/// </remarks>
internal sealed class SamplePreviewCollector
{
    /// <summary>Maximum width or height for image thumbnails in the preview.</summary>
    internal const int MaxThumbnailDimension = 64;

    private const string BinarySentinel = "[binary data]";

    private readonly int _sampleSize;
    private readonly object?[][] _reservoir;
    private readonly Random _random = new();
    private long _rowsConsidered;
    private int _reservoirCount;

    /// <summary>
    /// Initialises a new <see cref="SamplePreviewCollector"/> with the specified reservoir size.
    /// </summary>
    /// <param name="sampleSize">The maximum number of sample rows to retain.</param>
    public SamplePreviewCollector(int sampleSize = 25)
    {
        _sampleSize = sampleSize;
        _reservoir = new object?[sampleSize][];
    }

    /// <summary>
    /// Considers a row for inclusion in the sample. Uses reservoir sampling so that
    /// every row in the source has an equal probability of being selected, regardless
    /// of stream length.
    /// </summary>
    /// <param name="row">The row to consider.</param>
    /// <param name="store">
    /// The <see cref="IValueStore"/> backing the row's reference-type payloads (strings,
    /// vectors, images, arrays, structs). Reference values are materialized eagerly
    /// into CLR primitives here so the reservoir survives the batch returning to the
    /// pool and its arena being reset.
    /// </param>
    public void Consider(Row row, IValueStore store)
    {
        if (_reservoirCount < _sampleSize)
        {
            _reservoir[_reservoirCount] = ConvertRow(row, store);
            _reservoirCount++;
        }
        else
        {
            long j = _random.NextInt64(0, _rowsConsidered + 1);

            if (j < _sampleSize)
            {
                _reservoir[j] = ConvertRow(row, store);
            }
        }

        _rowsConsidered++;
    }

    /// <summary>
    /// Considers every row in <paramref name="batch"/> for the reservoir, resolving
    /// reference-type payloads through <paramref name="store"/>. Keeps the ingest
    /// loop batch-oriented instead of leaking the per-row iteration into the caller.
    /// </summary>
    public void Consider(RowBatch batch, IValueStore store)
    {
        for (int rowIndex = 0; rowIndex < batch.Count; rowIndex++)
        {
            Consider(batch[rowIndex], store);
        }
    }

    /// <summary>
    /// Builds the <see cref="SamplePreview"/> from the collected reservoir and the
    /// table schema.
    /// </summary>
    /// <param name="schema">The schema of the ingested table.</param>
    /// <param name="registry">
    /// Optional sidecar registry used to resolve any deferred sidecar-backed image
    /// cells in the reservoir. Required iff the reservoir contains
    /// <see cref="SidecarImageRef"/> placeholders (deferred from
    /// <see cref="Consider(Row, IValueStore)"/> because the bytes weren't available
    /// until the writer flushed). Each placeholder's <c>storeId</c> is looked up in
    /// the registry to find the right <see cref="IBlobSource"/>; bytes are read and
    /// rendered to <c>base64://...</c> thumbnails.
    /// </param>
    /// <returns>A preview containing feature descriptors and the sampled rows.</returns>
    public SamplePreview Build(Schema schema, SidecarRegistry? registry = null)
    {
        List<SampleFeature> features = new(schema.Columns.Count);
        foreach (ColumnInfo column in schema.Columns)
        {
            features.Add(new SampleFeature(column.Name, column.Kind.ToString().ToLowerInvariant()));
        }

        ResolveSidecarThumbnails(registry);

        object?[][] samples = new object?[_reservoirCount][];
        Array.Copy(_reservoir, samples, _reservoirCount);

        return new SamplePreview
        {
            Features = features,
            Samples = samples,
        };
    }

    /// <summary>
    /// Walks the reservoir and replaces every <see cref="SidecarImageRef"/> placeholder
    /// with the rendered <c>base64://...</c> thumbnail string. Throws when a placeholder
    /// is found but no registry was provided or the storeId isn't registered.
    /// </summary>
    private void ResolveSidecarThumbnails(SidecarRegistry? registry)
    {
        for (int row = 0; row < _reservoirCount; row++)
        {
            object?[]? cells = _reservoir[row];
            if (cells is null) continue;

            for (int col = 0; col < cells.Length; col++)
            {
                if (cells[col] is not SidecarImageRef refValue) continue;

                if (registry is null)
                {
                    throw new InvalidOperationException(
                        "SamplePreviewCollector reservoir contains a sidecar-backed image " +
                        "but Build was called without a SidecarRegistry. Wrap the just-finalised " +
                        "SidecarReadStore in a registry and pass it.");
                }

                IBlobSource source = registry.Resolve(refValue.StoreId)
                    ?? throw new InvalidOperationException(
                        $"Sidecar storeId {refValue.StoreId} from a sample reservoir entry is " +
                        "not registered in the supplied SidecarRegistry.");

                ReadOnlySpan<byte> bytes = source.Read(refValue.Offset, refValue.Length);
                byte[] thumbnailBytes = CreateThumbnail(bytes.ToArray());
                cells[col] = "base64://" + Convert.ToBase64String(thumbnailBytes);
            }
        }
    }

    /// <summary>
    /// Converts a <see cref="Row"/> into an array of JSON-friendly objects.
    /// </summary>
    private static object?[] ConvertRow(Row row, IValueStore store)
    {
        object?[] values = new object?[row.FieldCount];
        for (int i = 0; i < row.FieldCount; i++)
        {
            values[i] = ConvertValue(row[i], store);
        }

        return values;
    }

    /// <summary>
    /// Converts a single <see cref="DataValue"/> to a JSON-serialisable representation,
    /// resolving reference-type payloads through <paramref name="store"/>.
    /// </summary>
    internal static object? ConvertValue(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            return null;
        }

        // Byte arrays (UInt8 + IsArray) take the binary sentinel before the kind
        // switch so they don't fall into the scalar UInt8 default arm.
        if (value.IsByteArrayKind)
        {
            return BinarySentinel;
        }

        return value.Kind switch
        {
            // Composite types need recursive conversion for JSON nesting.
            DataKind.Vector => ConvertVector(value.AsVector(store)),
            DataKind.Image => ConvertImage(value, store),
            DataKind.Array => ConvertArray(value, store),
            DataKind.Struct => ConvertStruct(value, store),
            // Strings need the store to resolve arena- or handle-backed content.
            DataKind.String => value.AsString(store),
            // Date/time types: ISO string for JSON (DateOnly/DateTimeOffset don't serialize well).
            DataKind.Date => value.AsDate().ToString("O"),
            DataKind.DateTime => value.AsDateTime().ToString("O"),
            DataKind.Time => value.AsTime().ToString("O"),
            DataKind.Duration => value.AsDuration().ToString(),
            DataKind.Uuid => value.AsUuid().ToString(),
            // Everything else: scalar boxed CLR types (float, int, bool, etc.).
            _ => value.ToObject(),
        };
    }

    /// <summary>Converts a float vector to a boxed float array for JSON.</summary>
    private static object ConvertVector(float[] vector)
    {
        object[] result = new object[vector.Length];
        for (int i = 0; i < vector.Length; i++)
        {
            result[i] = vector[i];
        }

        return result;
    }

    /// <summary>
    /// Converts an image to a base64 string prefixed with <c>base64://</c>.
    /// The image is resized to fit within 64×64 pixels, preserving aspect ratio.
    /// For sidecar-backed values the bytes aren't yet readable (writer is mid-stream),
    /// so a <see cref="SidecarImageRef"/> placeholder is returned and resolved later
    /// in <see cref="Build(Schema, SidecarRegistry?)"/> against the finalised sidecar.
    /// </summary>
    private static object ConvertImage(DataValue value, IValueStore store)
    {
        if (value.IsInSidecar)
        {
            return new SidecarImageRef(value.SidecarStoreId, value.SidecarOffset, value.SidecarLength);
        }

        byte[] imageBytes = value.AsImage(store);
        byte[] thumbnailBytes = CreateThumbnail(imageBytes);
        return "base64://" + Convert.ToBase64String(thumbnailBytes);
    }

    /// <summary>
    /// Reservoir placeholder for sidecar-backed image cells. Holds the
    /// <c>storeId</c> identifying which sidecar in the registry backs the bytes,
    /// plus absolute <c>(offset, length)</c> coordinates within that sidecar.
    /// Resolved by <see cref="Build(Schema, SidecarRegistry?)"/> once an
    /// <see cref="IBlobSource"/> is available.
    /// </summary>
    private sealed record SidecarImageRef(byte StoreId, long Offset, long Length);

    /// <summary>
    /// Decodes an image, resizes it to fit within <see cref="MaxThumbnailDimension"/>×<see cref="MaxThumbnailDimension"/>
    /// preserving aspect ratio, and re-encodes as PNG.
    /// </summary>
    internal static byte[] CreateThumbnail(byte[] imageBytes)
    {
        using SKBitmap original = SKBitmap.Decode(imageBytes);

        if (original is null)
        {
            // Undecipherable image — return original bytes rather than crashing.
            return imageBytes;
        }

        int targetWidth = original.Width;
        int targetHeight = original.Height;

        if (original.Width > MaxThumbnailDimension || original.Height > MaxThumbnailDimension)
        {
            float scale = Math.Min(
                (float)MaxThumbnailDimension / original.Width,
                (float)MaxThumbnailDimension / original.Height);

            targetWidth = Math.Max(1, (int)(original.Width * scale));
            targetHeight = Math.Max(1, (int)(original.Height * scale));
        }

        if (targetWidth == original.Width && targetHeight == original.Height)
        {
            // Already small enough — re-encode as PNG without resizing.
            return ImageEncoder.Encode(original, SKEncodedImageFormat.Png);
        }

        using SKBitmap resized = original.Resize(
            new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"Failed to resize image thumbnail to {targetWidth}×{targetHeight}.");

        return ImageEncoder.Encode(resized, SKEncodedImageFormat.Png);
    }

    /// <summary>Converts a struct into a JSON-serialisable array of field values.</summary>
    private static object?[] ConvertStruct(DataValue value, IValueStore store)
    {
        DataValue[] fields = value.AsStruct(store);
        object?[] result = new object?[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            result[i] = ConvertValue(fields[i], store);
        }

        return result;
    }

    /// <summary>Converts a typed array into a JSON-serialisable array.</summary>
    private static object?[] ConvertArray(DataValue value, IValueStore store)
    {
        DataValue[] elements = value.AsArray(store);
        object?[] result = new object?[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            result[i] = ConvertValue(elements[i], store);
        }

        return result;
    }
}

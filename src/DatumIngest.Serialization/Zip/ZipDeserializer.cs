using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Functions.Image;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Zip;

/// <summary>
/// Deserializes ZIP archives containing image datasets into <see cref="RowBatch"/>
/// streams. Yields one row per entry with the following columns:
/// <list type="bullet">
///   <item><description><c>file_name</c> (String) — the entry's full path within the archive.</description></item>
///   <item><description><c>file</c> (<see cref="DataKind.Image"/>) — the raw image bytes.</description></item>
///   <item><description><c>file_width</c> (Int32) — image pixel width, null when the header could not be parsed.</description></item>
///   <item><description><c>file_height</c> (Int32) — image pixel height, null when the header could not be parsed.</description></item>
///   <item><description><c>file_channels</c> (UInt8) — channel count (1/3/4 typical), null when the header could not be parsed.</description></item>
///   <item><description><c>file_byte_length</c> (Int64) — uncompressed entry length in bytes; always populated.</description></item>
///   <item><description><c>file_orientation</c> (String) — <c>"landscape"</c> / <c>"portrait"</c> / <c>"square"</c>, null when the header could not be parsed.</description></item>
/// </list>
/// OS/editor metadata entries (<c>__MACOSX/</c>, <c>.DS_Store</c>, <c>thumbs.db</c>,
/// <c>desktop.ini</c>) are skipped silently; any other entry whose first bytes do
/// not match a supported image magic (JPEG / PNG / WebP) aborts ingestion with a
/// descriptive error.
/// </summary>
/// <remarks>
/// <para>
/// Opinionated contract: ZIP is treated as a bag-of-images format. Mixed-content
/// archives (e.g. images + annotations + READMEs) need to be split at the source
/// or ingested via a different pipeline. Column names are deliberately prefixed
/// <c>file_*</c> rather than <c>image_*</c> so a future <c>AnyFile</c> mode can
/// keep the same schema shape (with the dimension columns null for non-images).
/// </para>
/// <para>
/// Entry bytes are decompressed <em>directly into the row batch's arena</em> via
/// <see cref="Arena.AppendFromStream"/>, avoiding the per-entry managed
/// <c>byte[]</c> allocation that would otherwise hit the Large Object Heap for
/// typical image sizes (&gt;85 KB). This keeps ingestion of large image archives
/// like COCO2017 out of Gen2 GC territory. When <see cref="SerializationContext.LboStore"/>
/// is set, bytes are routed to the <c>.datum-blob</c> sidecar instead and the row's
/// image cell holds the absolute (offset, length) coordinates rather than an arena slice.
/// </para>
/// <para>
/// The image header is parsed inline via <see cref="ImageHeaderParser.TryParseHeader"/>,
/// populating the derived dimension columns at zero extra I/O cost since the bytes
/// are already in hand. This makes width/height/channels/orientation queryable as
/// regular columns, eligible for zone-map pruning, and removes the need for stats
/// accumulators to re-read image bytes for these summaries.
/// </para>
/// </remarks>
public sealed class ZipDeserializer : IFormatDeserializer
{
    private readonly FileFormatDescriptor _descriptor;

    /// <summary>Creates a deserializer for the given file descriptor.</summary>
    public ZipDeserializer(FileFormatDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> DeserializeAsync(
        SerializationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using Stream stream = await _descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);

        ColumnLookup columnLookup = new([
            "file_name",
            "file",
            "file_width",
            "file_height",
            "file_channels",
            "file_byte_length",
            "file_orientation",
        ]);

        int batchSize = ComputeBatchSize(archive, context.BatchByteTarget);

        RowBatch? batch = null;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip directory entries and OS/editor metadata pollution that commonly
            // appears in user-uploaded archives.
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/')) continue;
            if (IsIgnorableMetadata(entry.FullName)) continue;

            batch ??= context.Pool.RentRowBatch(columnLookup, batchSize);
            DataValue[] values = context.Pool.RentDataValues(7);

            values[0] = DataValue.FromString(entry.FullName, batch.Arena);
            (DataValue imageValue, ImageDimensions? dimensions, long byteLength) = context.LboStore is null
                ? StoreEntryIntoArena(entry, batch.Arena)
                : StoreEntryIntoSidecar(entry, context.LboStore);

            values[1] = imageValue;
            PopulateDerivedColumns(values, dimensions, byteLength, batch.Arena);

            batch.Add(values);

            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Computes a batch size targeting <paramref name="targetBatchBytes"/> based on mean
    /// entry size. Scans the central directory (metadata only, no decompression).
    /// </summary>
    private static int ComputeBatchSize(ZipArchive archive, int targetBatchBytes)
    {
        int fileEntryCount = 0;
        long totalUncompressedSize = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                continue;
            fileEntryCount++;
            totalUncompressedSize += entry.Length;
        }

        return fileEntryCount > 0
            ? Math.Max(1, (int)((long)targetBatchBytes / Math.Max(1, totalUncompressedSize / fileEntryCount)))
            : 64;
    }

    /// <summary>
    /// Streams the entry's decompressed bytes directly into the batch arena (no
    /// managed <c>byte[]</c> allocation) and validates that the payload is a
    /// supported image format. Throws <see cref="InvalidDataException"/> when the
    /// entry is not JPEG / PNG / WebP. Returns the image cell, parsed header
    /// dimensions (null when only the magic check passed but the deeper header
    /// parse failed), and the byte length.
    /// </summary>
    private static (DataValue Image, ImageDimensions? Dimensions, long ByteLength) StoreEntryIntoArena(
        ZipArchiveEntry entry, Arena arena)
    {
        long length64 = entry.Length;
        if (length64 <= 0 || length64 > int.MaxValue)
        {
            throw new InvalidDataException(
                $"ZIP entry '{entry.FullName}' has an invalid uncompressed length " +
                $"({length64}). DatumIngest expects well-formed image archives.");
        }

        int length = (int)length64;
        using Stream entryStream = entry.Open();
        (long offset, int actualLength) = arena.AppendFromStream(entryStream, length);

        ReadOnlySpan<byte> bytes = arena.GetBytes(offset, actualLength);
        if (!DetectImageKind(bytes))
        {
            throw new InvalidDataException(
                $"ZIP entry '{entry.FullName}' is not a recognised image format " +
                $"(JPEG / PNG / WebP). DatumIngest treats ZIP sources as image " +
                $"datasets; mixed-content archives are not supported. Remove " +
                $"non-image files from the archive or ingest them via a different " +
                $"source format.");
        }

        ImageDimensions? dimensions = ImageHeaderParser.TryParseHeader(bytes);
        DataValue image = DataValueHelpers.FromArenaSlice(DataKind.Image, offset, actualLength, dimensions);
        return (image, dimensions, actualLength);
    }

    /// <summary>
    /// Reads the entry into a pooled buffer and appends it to the
    /// <paramref name="sidecar"/>. The pooled rent avoids LOH pressure for typical
    /// image sizes (&gt;85 KB) while keeping the path streaming-free at the
    /// <see cref="IBlobSink"/> boundary. Returns a sidecar-flagged
    /// <see cref="DataValue"/> with the absolute (offset, length) the sidecar
    /// assigned, plus parsed header dimensions and byte length so the deserializer
    /// can populate the derived dimension columns without re-reading the bytes.
    /// </summary>
    private static (DataValue Image, ImageDimensions? Dimensions, long ByteLength) StoreEntryIntoSidecar(
        ZipArchiveEntry entry, IBlobSink sidecar)
    {
        long length64 = entry.Length;
        if (length64 <= 0 || length64 > int.MaxValue)
        {
            throw new InvalidDataException(
                $"ZIP entry '{entry.FullName}' has an invalid uncompressed length " +
                $"({length64}). DatumIngest expects well-formed image archives.");
        }

        int length = (int)length64;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            using Stream entryStream = entry.Open();
            entryStream.ReadExactly(buffer, 0, length);

            ReadOnlySpan<byte> bytes = buffer.AsSpan(0, length);
            if (!DetectImageKind(bytes))
            {
                throw new InvalidDataException(
                    $"ZIP entry '{entry.FullName}' is not a recognised image format " +
                    $"(JPEG / PNG / WebP). DatumIngest treats ZIP sources as image " +
                    $"datasets; mixed-content archives are not supported. Remove " +
                    $"non-image files from the archive or ingest them via a different " +
                    $"source format.");
            }

            ImageDimensions? dimensions = ImageHeaderParser.TryParseHeader(bytes);
            (long offset, long appended) = sidecar.Append(bytes);
            DataValue image = DataValue.FromImageInSidecar(offset, appended);
            return (image, dimensions, appended);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Fills the five derived columns at slots 2-6 of <paramref name="values"/>:
    /// <c>file_width</c>, <c>file_height</c>, <c>file_channels</c>,
    /// <c>file_byte_length</c>, <c>file_orientation</c>. Width/height/channels and
    /// orientation are emitted as nulls when <paramref name="dimensions"/> is
    /// <see langword="null"/> (header parse failed despite a valid magic check);
    /// byte length is always populated from the entry's uncompressed size.
    /// </summary>
    private static void PopulateDerivedColumns(
        DataValue[] values, ImageDimensions? dimensions, long byteLength, Arena arena)
    {
        values[5] = DataValue.FromInt64(byteLength);

        if (dimensions is null)
        {
            values[2] = DataValue.Null(DataKind.Int32);
            values[3] = DataValue.Null(DataKind.Int32);
            values[4] = DataValue.Null(DataKind.UInt8);
            values[6] = DataValue.Null(DataKind.String);
            return;
        }

        values[2] = DataValue.FromInt32(dimensions.Width);
        values[3] = DataValue.FromInt32(dimensions.Height);
        values[4] = DataValue.FromUInt8((byte)dimensions.Channels);

        string orientation = dimensions.Width > dimensions.Height ? "landscape"
                           : dimensions.Height > dimensions.Width ? "portrait"
                           : "square";
        values[6] = DataValue.FromString(orientation, arena);
    }

    /// <summary>
    /// Returns <c>true</c> for OS/editor metadata paths that commonly appear in
    /// user-uploaded archives and should not be treated as data entries.
    /// </summary>
    private static bool IsIgnorableMetadata(string fullName)
    {
        // macOS archive metadata folder — contains ._ resource forks, not actual content.
        if (fullName.StartsWith("__MACOSX/", StringComparison.Ordinal)) return true;

        string name = Path.GetFileName(fullName);
        if (name.Length == 0) return false;

        // Leading-dot files: .DS_Store, .gitignore, .gitkeep, editor config, etc.
        if (name[0] == '.') return true;

        // Windows Explorer thumbnail cache + desktop metadata.
        if (name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// Peek-only magic detection for the three image formats the project currently
    /// decodes via Skia: JPEG (FF D8 FF), PNG (89 50 4E 47 0D 0A 1A 0A), and WebP
    /// (RIFF????WEBP). Cheap and allocation-free.
    /// </summary>
    private static bool DetectImageKind(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return true; // JPEG

        if (header.Length >= 8
            && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return true; // PNG
        }

        if (header.Length >= 12
            && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
        {
            return true; // WebP
        }

        return false;
    }
}

/// <summary>
/// Internal helper that constructs reference-type <see cref="DataValue"/>s from
/// a pre-populated <c>(offset, length)</c> in an arena, without re-appending the
/// bytes. Parallel to <see cref="DataValue.FromStringSlice"/> but for binary
/// payloads.
/// </summary>
internal static class DataValueHelpers
{
    /// <summary>
    /// Builds a <see cref="DataValue"/> with <paramref name="kind"/> referencing
    /// bytes already stored in an arena at <paramref name="offset"/> /
    /// <paramref name="length"/>. Caller is responsible for ensuring the arena
    /// contains the bytes.
    /// </summary>
    public static DataValue FromArenaSlice(DataKind kind, long offset, int length, ImageDimensions? imageDimensions = null)
        => kind switch
        {
            DataKind.Image => imageDimensions is { Width: > 0 and <= ushort.MaxValue, Height: > 0 and <= ushort.MaxValue } d
                ? DataValue.FromImageAtOffset(offset, length, (ushort)d.Width, (ushort)d.Height, ClampChannels(d.Channels))
                : DataValue.FromImageAtOffset(offset, length),
            // Byte-array slices use FromByteArrayAtOffset; callers can call that
            // factory directly since byte arrays don't have a single DataKind here.
            _ => throw new NotSupportedException($"FromArenaSlice does not support DataKind.{kind}."),
        };

    private static byte ClampChannels(int channels) =>
        channels is >= 0 and <= 255 ? (byte)channels : (byte)0;
}

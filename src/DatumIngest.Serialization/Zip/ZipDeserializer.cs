using System.IO.Compression;
using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Zip;

/// <summary>
/// Deserializes ZIP archives containing image datasets into <see cref="RowBatch"/>
/// streams. Yields one row per entry with <c>file_name</c> (string) and
/// <c>file_bytes</c> (<see cref="DataKind.Image"/>). OS/editor metadata entries
/// (<c>__MACOSX/</c>, <c>.DS_Store</c>, <c>thumbs.db</c>, <c>desktop.ini</c>) are
/// skipped silently; any other entry whose first bytes do not match a supported
/// image magic (JPEG / PNG / WebP) aborts ingestion with a descriptive error.
/// </summary>
/// <remarks>
/// <para>
/// Opinionated contract: ZIP is treated as a bag-of-images format. Mixed-content
/// archives (e.g. images + annotations + READMEs) need to be split at the source
/// or ingested via a different pipeline. Future work could extend magic-byte
/// detection to video (MP4, WebM) or audio (WAV, FLAC, OGG) formats, but those
/// require substantial additional engine work on the compute side.
/// </para>
/// <para>
/// Entry bytes are decompressed <em>directly into the row batch's arena</em> via
/// <see cref="Arena.AppendFromStream"/>, avoiding the per-entry managed
/// <c>byte[]</c> allocation that would otherwise hit the Large Object Heap for
/// typical image sizes (&gt;85 KB). This keeps ingestion of large image archives
/// like COCO2017 out of Gen2 GC territory.
/// </para>
/// <para>
/// Image magic detection is peek-only (first 4 – 12 bytes after the stream write)
/// and does not parse headers; dimension parsing happens later in
/// <c>ImageStatsAccumulator</c>.
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

        ColumnLookup columnLookup = new(["file_name", "file_bytes"]);

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
            DataValue[] values = context.Pool.RentDataValues(2);

            values[0] = DataValue.FromString(entry.FullName, batch.Arena);
            values[1] = StoreEntryIntoArena(entry, batch.Arena);

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
    /// entry is not JPEG / PNG / WebP.
    /// </summary>
    private static DataValue StoreEntryIntoArena(ZipArchiveEntry entry, Arena arena)
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
        (int offset, int actualLength) = arena.AppendFromStream(entryStream, length);

        if (!DetectImageKind(arena.GetBytes(offset, actualLength)))
        {
            throw new InvalidDataException(
                $"ZIP entry '{entry.FullName}' is not a recognised image format " +
                $"(JPEG / PNG / WebP). DatumIngest treats ZIP sources as image " +
                $"datasets; mixed-content archives are not supported. Remove " +
                $"non-image files from the archive or ingest them via a different " +
                $"source format.");
        }

        return DataValueHelpers.FromArenaSlice(DataKind.Image, offset, actualLength);
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
    public static DataValue FromArenaSlice(DataKind kind, int offset, int length)
        => kind switch
        {
            DataKind.Image => DataValue.FromImageAtOffset(offset, length),
            DataKind.UInt8Array => DataValue.FromUInt8ArrayAtOffset(offset, length),
            _ => throw new NotSupportedException($"FromArenaSlice does not support DataKind.{kind}."),
        };
}

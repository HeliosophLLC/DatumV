using System.IO.Compression;
using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Zip;

/// <summary>
/// Deserializes ZIP archives into <see cref="RowBatch"/> streams. Yields one row
/// per entry with <c>file_name</c> (string) and <c>file_bytes</c> (byte array).
/// </summary>
public sealed class ZipDeserializer : IFormatDeserializer
{
    /// <summary>Target bytes per batch. Slightly exceeding is fine.</summary>
    private const long TargetBatchBytes = 16 * 1024 * 1024;

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

        IReadOnlyList<string> names = ["file_name", "file_bytes"];
        Dictionary<string, int> nameIndex = new(2, StringComparer.OrdinalIgnoreCase)
        {
            ["file_name"] = 0,
            ["file_bytes"] = 1,
        };

        int batchSize = ComputeBatchSize(archive);

        RowBatch? batch = null;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                continue;

            batch ??= context.Pool.RentRowBatch(batchSize);
            DataValue[] values = context.Pool.RentDataValues(2);
            values[0] = DataValue.FromString(entry.FullName, batch.Arena);
            values[1] = DataValue.FromUInt8Array(ReadEntryBytes(entry), batch.Arena);

            batch.Add(new Row(names, values, nameIndex));

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
    /// Computes a batch size targeting ~16MB per batch based on mean entry size.
    /// Scans the central directory (metadata only, no decompression).
    /// </summary>
    private static int ComputeBatchSize(ZipArchive archive)
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
            ? Math.Max(1, (int)(TargetBatchBytes / Math.Max(1, totalUncompressedSize / fileEntryCount)))
            : 64;
    }

    /// <summary>
    /// Reads all bytes from a ZIP entry. Pre-sizes the buffer using
    /// <see cref="ZipArchiveEntry.Length"/> to avoid array doubling.
    /// </summary>
    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        long length = entry.Length;
        using Stream stream = entry.Open();

        if (length > 0)
        {
            byte[] buffer = new byte[length];
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0) break;
                offset += read;
            }
            return buffer;
        }

        using MemoryStream ms = new((int)Math.Max(entry.CompressedLength, 256));
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}

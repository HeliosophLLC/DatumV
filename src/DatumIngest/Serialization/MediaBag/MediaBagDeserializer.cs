using System.Buffers;
using System.Runtime.CompilerServices;
using DatumIngest.Ingestion;
using DatumIngest.Model;

namespace DatumIngest.Serialization.MediaBag;

/// <summary>
/// Container-agnostic deserializer for archives that are conceptually a
/// homogeneous bag of media files (one media kind per archive: all images, all
/// audio, …). The container layer is delegated to <see cref="IMediaBagReader"/>
/// — ZIP via <see cref="Zip.ZipBagReader"/>, TAR / TAR.GZ via
/// <see cref="Tar.TarBagReader"/>. The schema and per-row column population are
/// delegated to a <see cref="MediaKindHandler"/> probed from the first non-metadata
/// entry's magic bytes; every subsequent entry is validated against the locked
/// handler, and a mismatch aborts ingestion with a descriptive error.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No pre-scan.</strong> Sequential containers (TAR over GZip) cannot
/// cheaply produce a mean-entry-size estimate without reading the entire archive
/// twice, so batch sizing is driven by an arena-bytes watermark rather than a
/// row count derived from mean size. A batch flushes when either its row capacity
/// is reached or <see cref="Arena.BytesWritten"/> crosses
/// <see cref="SerializationContext.BatchByteTarget"/>. This matches the byte
/// target the row-count heuristic was aiming at and removes the only place where
/// the ZIP / TAR paths diverged.
/// </para>
/// <para>
/// <strong>Storage routing.</strong> When <see cref="SerializationContext.LboStore"/>
/// is null, entry bytes are streamed directly into the batch arena via
/// <see cref="Arena.AppendFromStream"/> (no managed <c>byte[]</c> per entry, so
/// large-image archives stay out of Gen2 GC). When set, bytes are routed to the
/// <c>.datum-blob</c> sidecar through a pooled buffer (avoids LOH for the
/// per-entry round trip).
/// </para>
/// <para>
/// <strong>Bootstrap.</strong> The first entry's bytes are read into a pooled
/// buffer because we cannot rent a batch until we know the schema. After probe
/// the bytes are written to either the batch arena or the sidecar; subsequent
/// entries skip the buffer hop and stream straight through.
/// </para>
/// </remarks>
public sealed class MediaBagDeserializer : IFormatDeserializer
{
    private const int DefaultBatchCapacity = 1024;

    private readonly IMediaBagReader _reader;

    /// <summary>Wraps the given reader; the deserializer drives one enumeration pass.</summary>
    public MediaBagDeserializer(IMediaBagReader reader)
    {
        _reader = reader;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> DeserializeAsync(
        SerializationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        MediaKindHandler? handler = null;
        ColumnLookup? columnLookup = null;
        RowBatch? batch = null;

        await foreach (MediaBagEntry entry in _reader.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Bag readers return every regular-file entry; the media-bag contract
            // additionally drops OS/editor metadata (__MACOSX/, .DS_Store, …) so
            // the homogeneity probe doesn't fail on a Finder-injected resource fork.
            // Raw consumers like the open_archive TVF skip this filter entirely.
            if (MediaBagFilter.IsIgnorableMetadata(entry.FullName)) continue;

            long byteLength = entry.Length;
            if (byteLength <= 0 || byteLength > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"Archive '{_reader.Source}' entry '{entry.FullName}' has an invalid " +
                    $"uncompressed length ({byteLength}). DatumIngest expects well-formed media archives.");
            }
            int length = (int)byteLength;

            if (handler is null)
            {
                // Bootstrap: probe the first entry to lock the kind, then rent a batch
                // with the correct schema. Read into a pooled buffer because we cannot
                // call AppendFromStream into the batch's arena before the batch exists.
                byte[] firstBuffer = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    entry.Body.ReadExactly(firstBuffer, 0, length);
                    ReadOnlySpan<byte> firstBytes = firstBuffer.AsSpan(0, length);

                    handler = MediaKindHandler.Detect(firstBytes);
                    if (handler is null)
                    {
                        throw new InvalidDataException(BuildUnknownMediaError(_reader.Source, entry.FullName));
                    }

                    columnLookup = new ColumnLookup(handler.ColumnNames);
                    batch = context.Pool.RentRowBatch(columnLookup, DefaultBatchCapacity);

                    EmitRowFromBuffer(firstBytes, entry.FullName, batch, handler, context);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(firstBuffer);
                }
            }
            else
            {
                batch ??= context.Pool.RentRowBatch(columnLookup!, DefaultBatchCapacity);
                EmitRowFromStream(entry, length, batch, handler, context);
            }

            if (batch.IsFull || batch.Arena.BytesWritten >= context.BatchByteTarget)
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
    /// First-entry path: bytes are already in a pooled buffer. Persist to arena or
    /// sidecar, then hand off to the handler.
    /// </summary>
    private static void EmitRowFromBuffer(
        ReadOnlySpan<byte> entryBytes, string fullName, RowBatch batch,
        MediaKindHandler handler, SerializationContext context)
    {
        DataValue[] values = context.Pool.RentDataValues(handler.ColumnNames.Length);

        if (context.LboStore is null)
        {
            var (offset, actualLength) = batch.Arena.AppendBytes(entryBytes);
            ReadOnlySpan<byte> stored = batch.Arena.GetBytes(offset, actualLength);
            handler.PopulateRowFromArena(values, fullName, offset, actualLength, stored, batch.Arena);
        }
        else
        {
            var (sidecarOffset, sidecarLength) = context.LboStore.Append(entryBytes);
            handler.PopulateRowFromSidecar(values, fullName, sidecarOffset, sidecarLength, entryBytes, batch.Arena);
        }

        batch.Add(values);
    }

    /// <summary>
    /// Steady-state path: stream entry bytes into the destination (arena or sidecar),
    /// validate the magic against the locked handler, then hand off.
    /// </summary>
    private static void EmitRowFromStream(
        MediaBagEntry entry, int length, RowBatch batch,
        MediaKindHandler handler, SerializationContext context)
    {
        DataValue[] values = context.Pool.RentDataValues(handler.ColumnNames.Length);

        if (context.LboStore is null)
        {
            (long offset, int actualLength) = batch.Arena.AppendFromStream(entry.Body, length);
            ReadOnlySpan<byte> bytes = batch.Arena.GetBytes(offset, actualLength);
            EnsureHomogeneous(handler, bytes, entry.FullName);
            handler.PopulateRowFromArena(values, entry.FullName, offset, actualLength, bytes, batch.Arena);
        }
        else
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                entry.Body.ReadExactly(buffer, 0, length);
                ReadOnlySpan<byte> bytes = buffer.AsSpan(0, length);
                EnsureHomogeneous(handler, bytes, entry.FullName);
                var (sidecarOffset, sidecarLength) = context.LboStore.Append(bytes);
                handler.PopulateRowFromSidecar(values, entry.FullName, sidecarOffset, sidecarLength, bytes, batch.Arena);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        batch.Add(values);
    }

    private static void EnsureHomogeneous(MediaKindHandler handler, ReadOnlySpan<byte> bytes, string fullName)
    {
        if (handler.MatchesMagic(bytes)) return;
        throw new InvalidDataException(
            $"Archive entry '{fullName}' is not a recognised {handler.Kind} payload. " +
            $"DatumIngest treats archive sources as homogeneous media bags — every entry " +
            $"must match the kind detected from the first entry ({handler.Kind}). Remove " +
            $"the off-kind file from the archive or split it into one archive per media kind.");
    }

    private static string BuildUnknownMediaError(string archive, string entryName) =>
        $"Archive '{archive}' first entry '{entryName}' does not match any supported media " +
        $"magic (image: JPEG / PNG / WebP / GIF; audio: FLAC / WAV / OGG / MP3). DatumIngest " +
        $"treats archive sources as homogeneous media bags; mixed-content archives are not " +
        $"supported. Remove non-media files or ingest via a different source format.";
}

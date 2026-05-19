using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace Heliosoph.DatumV.DatumFile.Sidecar;

/// <summary>
/// Writes Large Binary Objects (images, byte arrays, future video, etc.) to a
/// <c>.datum-blob</c> sidecar file. Created lazily — the file does not exist on
/// disk until the first <see cref="Append"/> call. <c>.datum</c> files containing
/// no LBO data leave no orphan <c>.datum-blob</c> behind.
/// </summary>
/// <remarks>
/// <para>
/// Concurrent <see cref="Append"/> calls are serialised internally; callers can
/// hand the same instance to multiple producers and trust that returned
/// <c>(Offset, Length)</c> pairs are unique and non-overlapping.
/// </para>
/// <para>
/// The xxHash3-64 over the payload region is computed on a dedicated background
/// thread that consumes a bounded queue of just-written buffers. This keeps the
/// hash compute (~1.9 GB/s on .NET's <see cref="XxHash3"/>) off the deserializer's
/// hot path so it overlaps with ZIP read / decompression I/O instead of serialising
/// behind every <see cref="Append"/>. The producer is the synchronous page-encoder
/// hot path, so the queue is a <see cref="BlockingCollection{T}"/> drained by a
/// real <see cref="Thread"/> — neither side needs to await.
/// </para>
/// <para>
/// On <see cref="Dispose"/> the queue is completed, the hash worker drains, and
/// the final hash is patched into the header before the file is flushed and closed.
/// The containing <c>DatumFileWriter</c> embeds <see cref="Fingerprint"/> in the
/// <c>.datum</c> footer only when <see cref="WasMaterialized"/> is true, so the
/// presence of the field in the footer is the canonical signal that a sidecar
/// must accompany the <c>.datum</c> file at read time.
/// </para>
/// </remarks>
public sealed class SidecarWriteStore : IBlobSink, IDisposable
{
    /// <summary>
    /// Bound on in-flight hash buffers. With ~150 KB average ZIP entry size, 32
    /// slots cap memory at ~5 MB — small relative to a row-group's arena while
    /// giving the deserializer plenty of slack before back-pressure kicks in.
    /// </summary>
    private const int HashQueueCapacity = 32;

    private readonly string _path;
    private readonly Lock _lock = new();
    private FileStream? _stream;
    private long _writeOffset;
    private bool _disposed;

    /// <summary>
    /// True when this store was created via <see cref="OpenForAppend"/>
    /// against an existing sidecar file. Append mode preserves the
    /// existing fingerprint, opens the stream eagerly at EOF, and
    /// invalidates the payload hash on Dispose (writes zero, which the
    /// reader treats as "unhashed" — same back-compat path as
    /// pre-Phase-9b sidecars). Streaming hash recompute over the full
    /// extended payload is a future enhancement.
    /// </summary>
    private readonly bool _appendMode;

    /// <summary>
    /// Bounded queue of just-written buffers (rented from <see cref="ArrayPool{T}.Shared"/>).
    /// The deserializer thread enqueues; a single background thread drains and feeds
    /// <see cref="_hasher"/>. Buffers are returned to the pool by the worker after
    /// hashing. Created lazily on first <see cref="Append"/> alongside the
    /// <see cref="FileStream"/>. <see cref="BlockingCollection{T}"/> gives the
    /// producer back-pressure (blocks <c>Add</c> when full) without any async
    /// machinery — the page-encoder caller is synchronous.
    /// </summary>
    private BlockingCollection<HashJob>? _hashQueue;

    /// <summary>The single background thread draining <see cref="_hashQueue"/>.</summary>
    private Thread? _hashThread;

    /// <summary>
    /// xxHash3-64 accumulator. Written exclusively on the hash worker thread, so
    /// no synchronisation is required around <c>Append</c> / <c>GetCurrentHashAsUInt64</c>.
    /// The worker is started before the first <c>Append</c> returns and joined
    /// before <c>GetCurrentHashAsUInt64</c> is read in <see cref="Dispose"/>.
    /// </summary>
    private XxHash3? _hasher;

    /// <summary>
    /// Creates a sidecar writer targeting <paramref name="path"/>. The file is not
    /// created until the first <see cref="Append"/> call; constructing the writer
    /// has no on-disk side effect.
    /// </summary>
    /// <param name="path">Full path to the sidecar file (typically the companion
    /// <c>.datum</c> path with extension swapped to <see cref="SidecarConstants.FileExtension"/>).</param>
    public SidecarWriteStore(string path)
    {
        _path = path;
        Fingerprint = GenerateFingerprint();
    }

    /// <summary>
    /// Private constructor for <see cref="OpenForAppend"/>. Eagerly
    /// opens the existing file at EOF, preserves the fingerprint read
    /// from its header, and switches into append mode (no streaming
    /// hash; payload hash zeroed on Dispose).
    /// </summary>
    private SidecarWriteStore(string path, ulong existingFingerprint, FileStream openedStream)
    {
        _path = path;
        Fingerprint = existingFingerprint;
        _stream = openedStream;
        _writeOffset = openedStream.Length;
        _appendMode = true;
    }

    /// <summary>
    /// Opens an existing sidecar file in append mode. The existing
    /// header (magic, version, fingerprint) is preserved; new payloads
    /// land at current EOF. If <paramref name="path"/> does not exist
    /// (the source <c>.datum</c> previously had no non-inline values),
    /// returns a fresh writer that lazy-materialises on first
    /// <see cref="Append"/> — matches initial-write semantics.
    /// </summary>
    /// <remarks>
    /// In append mode the streaming xxHash3 worker is not started — the
    /// existing payload hash on disk is left in place during writes and
    /// zeroed on <see cref="Dispose"/>, marking the file as "unhashed."
    /// <see cref="SidecarReadStore"/> already skips verification when
    /// the stored hash is zero (back-compat with pre-Phase-9b sidecars),
    /// so opening such a file for read still works. Recomputing the hash
    /// over the full extended payload after append is a future
    /// enhancement.
    /// </remarks>
    public static SidecarWriteStore OpenForAppend(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            // Source file had no non-inline payloads; behave like a
            // fresh writer (lazy on first Append).
            return new SidecarWriteStore(path);
        }

        // Read existing fingerprint from the header.
        ulong fingerprint;
        using (FileStream readHeader = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (readHeader.Length < SidecarConstants.HeaderSize)
            {
                throw new InvalidDataException(
                    $"Sidecar '{path}' is shorter than the header size; cannot open for append.");
            }
            Span<byte> header = stackalloc byte[SidecarConstants.HeaderSize];
            readHeader.ReadExactly(header);
            ulong magic = BinaryPrimitives.ReadUInt64LittleEndian(header[0..8]);
            if (magic != SidecarConstants.Magic)
            {
                throw new InvalidDataException(
                    $"Sidecar '{path}' does not start with the expected DATUMBLB magic.");
            }
            fingerprint = BinaryPrimitives.ReadUInt64LittleEndian(header[16..24]);
        }

        // Re-open RW at EOF for append.
        FileStream rwStream = new(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 1 << 20,
            options: FileOptions.SequentialScan);
        try
        {
            rwStream.Position = rwStream.Length;
            return new SidecarWriteStore(path, fingerprint, rwStream);
        }
        catch
        {
            rwStream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 64-bit random value generated at construction. The companion <c>.datum</c>
    /// file's footer must record this value; readers compare against the sidecar's
    /// header to detect stale or swapped files.
    /// </summary>
    public ulong Fingerprint { get; }

    /// <summary>
    /// True once <see cref="Append"/> has been called at least once and the sidecar
    /// file exists on disk. Writers that finalise without any LBO data will see
    /// <c>false</c> and skip both the file flush and the .datum footer's sidecar
    /// reference, keeping pure-tabular ingest free of sidecar artefacts.
    /// </summary>
    public bool WasMaterialized => _stream is not null;

    /// <inheritdoc />
    public (long Offset, long Length) Append(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Snapshot a copy for the background hasher. Done before taking the lock so
        // the lock hold time stays minimal — and so memory pressure from the rent +
        // copy doesn't inflate critical-section time. Cost is one extra memcpy per
        // Append (~150 KB at ~25 GB/s = ~6 µs); the win is moving xxHash3 compute
        // off this thread so it parallelises with ZIP read I/O.
        byte[] copy = ArrayPool<byte>.Shared.Rent(bytes.Length);
        bytes.CopyTo(copy);

        long offset;
        lock (_lock)
        {
            if (_stream is null)
            {
                // FileMode.Create matches DatumFileWriter's behavior on the companion
                // .datum file: overwrite if a stale sidecar exists from a prior run.
                // Stream is opened ReadWrite (not Write) only so Dispose can seek
                // back and patch the 8-byte hash into the header. Payload writes
                // remain pure forward streaming.
                _stream = new FileStream(
                    _path,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    bufferSize: 1 << 20,
                    options: FileOptions.SequentialScan);
                WriteHeader(_stream, Fingerprint);
                _writeOffset = SidecarConstants.HeaderSize;
                _hasher = new XxHash3();
                _hashQueue = new BlockingCollection<HashJob>(boundedCapacity: HashQueueCapacity);
                _hashThread = new Thread(HashWorker)
                {
                    IsBackground = true,
                    Name = "SidecarHashWorker",
                };
                _hashThread.Start();
            }

            offset = _writeOffset;
            _stream.Write(bytes);
            _writeOffset += bytes.Length;

            if (_appendMode)
            {
                // Append mode skips streaming hash — the buffer copy is
                // unused and goes back to the pool immediately. Dispose
                // zeros the on-disk hash to mark the file unhashed; a
                // future PR can recompute over the full extended
                // payload if integrity validation becomes load-bearing.
                ArrayPool<byte>.Shared.Return(copy);
            }
            else
            {
                // Enqueue under the lock so write order matches enqueue order matches
                // hash order. xxHash3 is order-dependent — Append("ab") ≠ Append("ba")
                // — and the IBlobSink contract permits concurrent Appends, so we can't
                // rely on the call site to serialise. Add blocks when the queue is full
                // (~32 buffers behind), which is rare given xxHash3 throughput beats
                // disk write.
                _hashQueue!.Add(new HashJob(copy, bytes.Length));
            }
        }

        return (offset, bytes.Length);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_stream is not null)
        {
            ulong hash;
            if (_appendMode)
            {
                // Append mode invalidates the previous hash — write 0
                // (the "unhashed" sentinel). SidecarReadStore skips
                // verification when the stored hash is zero.
                hash = 0;
            }
            else
            {
                // Signal the hash worker to drain remaining jobs and exit. Then join
                // it so the hasher state reflects every byte ever appended. Only then
                // is it safe to read the final hash and patch it into the header.
                _hashQueue!.CompleteAdding();
                _hashThread!.Join();
                _hashQueue.Dispose();
                hash = _hasher!.GetCurrentHashAsUInt64();
            }

            _stream.Seek(SidecarConstants.PayloadHashOffset, SeekOrigin.Begin);
            Span<byte> hashBytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(hashBytes, hash);
            _stream.Write(hashBytes);

            _stream.Flush();
            _stream.Dispose();
            _stream = null;
        }
    }

    /// <summary>
    /// Drains <see cref="_hashQueue"/>, feeding each buffer's payload into the
    /// xxHash3 accumulator and returning the buffer to <see cref="ArrayPool{T}.Shared"/>.
    /// Runs as a single dedicated background thread — the hasher state is therefore
    /// touched by exactly one thread. Append order matches enqueue order matches
    /// dequeue order, so the final hash is identical to what an inline implementation
    /// would produce.
    /// </summary>
    private void HashWorker()
    {
        foreach (HashJob job in _hashQueue!.GetConsumingEnumerable())
        {
            _hasher!.Append(job.Buffer.AsSpan(0, job.Length));
            ArrayPool<byte>.Shared.Return(job.Buffer);
        }
    }

    private static ulong GenerateFingerprint()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void WriteHeader(Stream stream, ulong fingerprint)
    {
        Span<byte> header = stackalloc byte[SidecarConstants.HeaderSize];
        BinaryPrimitives.WriteUInt64LittleEndian(header[0..8], SidecarConstants.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], SidecarConstants.Version);
        // header[12..16] reserved (zero)
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..24], fingerprint);
        // header[24..32] zero — payloadHash is patched in by Dispose.
        stream.Write(header);
    }

    /// <summary>
    /// One unit of hashing work: a pooled buffer holding payload bytes and the
    /// active length. The worker hashes <c>Buffer.AsSpan(0, Length)</c> and returns
    /// the buffer to <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    private readonly record struct HashJob(byte[] Buffer, int Length);
}

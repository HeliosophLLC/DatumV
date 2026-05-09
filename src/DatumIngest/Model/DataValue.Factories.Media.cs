using System.IO.Hashing;
using System.Runtime.InteropServices;
using DatumIngest.DatumFile.Sidecar;

namespace DatumIngest.Model;

public readonly partial struct DataValue
{
    /// <summary>Creates a value from encoded image bytes.</summary>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="FromImage(byte[], IValueStore)"/> instead.</remarks>
    public static DataValue FromImage(byte[] value) =>
        throw new InvalidOperationException("Use FromImage(value, store). ReferenceStore is no longer available.");

    /// <summary>Creates a value from encoded image bytes using an explicit <see cref="IValueStore"/>.</summary>
    /// <remarks>
    /// Stamps the low 32 bits of <c>XxHash64(value)</c> into <c>_p6</c> so cross-arena
    /// Equals / GetHashCode short-circuits without re-reading the bytes — same shape
    /// as the arena-array hash slot.
    /// </remarks>
    public static DataValue FromImage(byte[] value, IValueStore store)
    {
        uint hash32 = unchecked((uint)XxHash64.HashToUInt64(value));
        var (p0, p1) = store.StoreBytes(value);
        return new(DataKind.Image, flags: DataValueFlags.InArena,
            offset: p0.Value, length: p1.Value,
            width: 0, height: 0, channels: 0, hash32: hash32);
    }

    /// <summary>
    /// Creates a value from encoded image bytes plus inline dimensions metadata.
    /// Width/height fit in <see cref="ushort"/> per the design — codec specs cap at
    /// 65535×65535 and gigapixel imagery is tiled in practice. When dimensions are
    /// available at ingest time (e.g. from <c>ImageHeaderParser</c>), this overload
    /// lets accessors like <see cref="ImageWidth"/> and
    /// <c>image_width()</c> read W/H without a full SkiaSharp decode.
    /// </summary>
    /// <param name="value">Encoded image bytes (PNG/JPEG/WebP/etc.).</param>
    /// <param name="store">Backing store for the encoded bytes.</param>
    /// <param name="width">Pixel width; pass 0 when unknown.</param>
    /// <param name="height">Pixel height; pass 0 when unknown.</param>
    /// <param name="channels">Channel count (1=grayscale, 3=RGB, 4=RGBA); 0 when unknown.</param>
    public static DataValue FromImage(byte[] value, IValueStore store, ushort width, ushort height, byte channels = 0)
    {
        uint hash32 = unchecked((uint)XxHash64.HashToUInt64(value));
        var (p0, p1) = store.StoreBytes(value);
        return new(
            DataKind.Image,
            flags: DataValueFlags.InArena,
            offset: p0.Value, length: p1.Value,
            width: width, height: height, channels: channels,
            hash32: hash32);
    }

    /// <summary>
    /// Creates a value from a fully formed PointCloud blob (40-byte header
    /// followed by interleaved per-point payload) using an explicit
    /// <see cref="IValueStore"/>. Callers are responsible for building the blob;
    /// see <c>DatumIngest.Model.Spatial.PointCloudHeader</c> for the layout.
    /// </summary>
    public static DataValue FromPointCloud(byte[] blob, IValueStore store)
    {
        var (p0, p1) = store.StoreBytes(blob);
        return new(DataKind.PointCloud, flags: DataValueFlags.InArena, offset: p0.Value, length: p1.Value);
    }

    /// <summary>
    /// Variant of <see cref="FromPointCloud(byte[], IValueStore)"/> that stamps inline
    /// metadata (<paramref name="pointCount"/> + <paramref name="attributeFlags"/>) so
    /// accessors like <see cref="PointCloudCount"/> and the <c>point_cloud_count()</c>
    /// SQL function can read the count without dereferencing the blob.
    /// </summary>
    public static DataValue FromPointCloud(byte[] blob, IValueStore store, uint pointCount, byte attributeFlags = 0)
    {
        var (p0, p1) = store.StoreBytes(blob);
        return new(DataKind.PointCloud, flags: DataValueFlags.InArena,
            offset: p0.Value, length: p1.Value,
            p4: unchecked((int)pointCount), p5: attributeFlags, p6: 0);
    }

    /// <summary>
    /// Creates a value from a fully formed Mesh blob (48-byte header followed
    /// by interleaved per-vertex payload, triangle indices, and optional
    /// embedded texture) using an explicit <see cref="IValueStore"/>. Callers
    /// are responsible for building the blob; see
    /// <c>DatumIngest.Model.Spatial.MeshHeader</c> for the layout.
    /// </summary>
    public static DataValue FromMesh(byte[] blob, IValueStore store)
    {
        var (p0, p1) = store.StoreBytes(blob);
        return new(DataKind.Mesh, flags: DataValueFlags.InArena, offset: p0.Value, length: p1.Value);
    }

    /// <summary>
    /// Variant of <see cref="FromMesh(byte[], IValueStore)"/> that stamps inline metadata
    /// (vertex/triangle counts + attribute flags). Read via <see cref="MeshVertexCount"/>,
    /// <see cref="MeshTriangleCount"/>, <see cref="MeshAttributes"/>.
    /// </summary>
    public static DataValue FromMesh(byte[] blob, IValueStore store, uint vertexCount, uint triangleCount, byte attributeFlags = 0)
    {
        var (p0, p1) = store.StoreBytes(blob);
        return new(DataKind.Mesh, flags: DataValueFlags.InArena,
            offset: p0.Value, length: p1.Value,
            p4: unchecked((int)vertexCount), p5: unchecked((int)triangleCount), p6: attributeFlags);
    }

    /// <summary>Creates a value from encoded audio bytes.</summary>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="FromAudio(byte[], IValueStore)"/> instead.</remarks>
    public static DataValue FromAudio(byte[] value) =>
        throw new InvalidOperationException("Use FromAudio(value, store). ReferenceStore is no longer available.");

    /// <summary>Creates a value from encoded audio bytes using an explicit <see cref="IValueStore"/>.</summary>
    /// <remarks>
    /// Stamps the low 32 bits of <c>XxHash64(value)</c> into <c>_p6</c> so cross-arena
    /// / cross-sidecar Audio Equals short-circuits without re-reading the bytes.
    /// </remarks>
    public static DataValue FromAudio(byte[] value, IValueStore store)
    {
        uint hash32 = unchecked((uint)XxHash64.HashToUInt64(value));
        var (p0, p1) = store.StoreBytes(value);
        return new(DataKind.Audio, flags: DataValueFlags.InArena,
            offset: p0.Value, length: p1.Value,
            p4: 0, p5: 0, p6: unchecked((int)hash32));
    }

    /// <summary>
    /// Variant of <see cref="FromAudio(byte[], IValueStore)"/> that stamps inline metadata
    /// (sample-rate, channels, bit-depth, frame-count). Substrate for the
    /// <c>audio_sample_rate()</c> SQL function and the planner-time inline-accessor
    /// elision path; the per-field accessors (<see cref="AudioSampleRate"/>, etc.)
    /// read the same bits back without decoding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout (post-Round-2 repack to free <c>_p6</c> for the content hash):
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>_p4</c>: <c>sampleRate(24 b)</c> | <c>channels(4 b)</c> | <c>bitDepthCode(4 b)</c></description></item>
    /// <item><description><c>_p5</c>: <c>frameCount(32 b)</c></description></item>
    /// <item><description><c>_p6</c>: low 32 bits of <c>XxHash64(value)</c></description></item>
    /// </list>
    /// <para>
    /// Caps: <paramref name="sampleRate"/> ≤ 16,777,215 Hz (16.7 MHz; ~20× headroom
    /// over the 768 kHz pro-audio ceiling); <paramref name="channels"/> ≤ 15
    /// (covers Atmos 7.1.4 = 12); <paramref name="bitDepth"/> must be 0 (unknown),
    /// 8, 16, 24, 32, or 64. Out-of-range inputs throw
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// </para>
    /// </remarks>
    public static DataValue FromAudio(byte[] value, IValueStore store, uint sampleRate, byte channels = 0, byte bitDepth = 0, uint frameCount = 0)
    {
        int packedP4 = PackAudioP4(sampleRate, channels, bitDepth);
        uint hash32 = unchecked((uint)XxHash64.HashToUInt64(value));
        var (p0, p1) = store.StoreBytes(value);
        return new(DataKind.Audio, flags: DataValueFlags.InArena,
            offset: p0.Value, length: p1.Value,
            p4: packedP4,
            p5: unchecked((int)frameCount),
            p6: unchecked((int)hash32));
    }

    private const uint AudioMaxSampleRate = 0x00FF_FFFFu; // 24-bit cap
    private const byte AudioMaxChannels = 0x0F;            // 4-bit cap

    /// <summary>
    /// Packs the Audio metadata triple into <c>_p4</c> per the post-Round-2 layout.
    /// Throws when any input exceeds its bit budget — these are caller-side errors
    /// (e.g. a future codec returning a wildly out-of-range sample-rate) that
    /// need fixing upstream rather than silent truncation.
    /// </summary>
    private static int PackAudioP4(uint sampleRate, byte channels, byte bitDepth)
    {
        if (sampleRate > AudioMaxSampleRate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate), sampleRate,
                $"Audio sample rate exceeds the {AudioMaxSampleRate}-Hz inline cap.");
        }
        if (channels > AudioMaxChannels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channels), channels,
                $"Audio channel count exceeds the {AudioMaxChannels}-channel inline cap.");
        }

        byte bitDepthCode = EncodeAudioBitDepth(bitDepth);

        return unchecked((int)(
            sampleRate
            | ((uint)(channels & AudioMaxChannels) << 24)
            | ((uint)bitDepthCode << 28)));
    }

    /// <summary>
    /// Maps a literal bit-depth value (0 / 8 / 16 / 24 / 32 / 64) to its 4-bit
    /// storage code. Any other value throws — the inline metadata slot does not
    /// model arbitrary bit-depths.
    /// </summary>
    private static byte EncodeAudioBitDepth(byte bitDepth) => bitDepth switch
    {
        0 => 0,
        8 => 1,
        16 => 2,
        24 => 3,
        32 => 4,
        64 => 5,
        _ => throw new ArgumentOutOfRangeException(
            nameof(bitDepth), bitDepth,
            "Audio bit-depth must be 0 (unknown), 8, 16, 24, 32, or 64."),
    };

    /// <summary>Inverse of <see cref="EncodeAudioBitDepth"/>; returns 0 for unrecognised codes.</summary>
    private static byte DecodeAudioBitDepth(byte code) => code switch
    {
        1 => 8,
        2 => 16,
        3 => 24,
        4 => 32,
        5 => 64,
        _ => 0,
    };

    /// <summary>Creates a value from encoded video bytes.</summary>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="FromVideo(byte[], IValueStore)"/> instead.</remarks>
    public static DataValue FromVideo(byte[] value) =>
        throw new InvalidOperationException("Use FromVideo(value, store). ReferenceStore is no longer available.");

    /// <summary>Creates a value from encoded video bytes using an explicit <see cref="IValueStore"/>.</summary>
    public static DataValue FromVideo(byte[] value, IValueStore store)
    {
        var (p0, p1) = store.StoreBytes(value);
        return new(DataKind.Video, flags: DataValueFlags.InArena, offset: p0.Value, length: p1.Value);
    }

    /// <summary>
    /// Variant of <see cref="FromVideo(byte[], IValueStore)"/> that stamps inline metadata
    /// (W/H + FPS as 8.8 fixed-point + codec + frame_count). Substrate for future
    /// <c>video_width()</c> / <c>video_codec()</c> SQL functions; no consumer exists yet.
    /// </summary>
    public static DataValue FromVideo(byte[] value, IValueStore store, ushort width, ushort height, ushort fpsX256 = 0, byte codec = 0, uint frameCount = 0)
    {
        var (p0, p1) = store.StoreBytes(value);
        return new(DataKind.Video, flags: DataValueFlags.InArena,
            offset: p0.Value, length: p1.Value,
            p4: unchecked((int)((uint)width | ((uint)height << 16))),
            p5: fpsX256 | (codec << 16),
            p6: unchecked((int)frameCount));
    }

    /// <summary>Creates a JSON value from canonical CBOR bytes.</summary>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="FromJson(byte[], IValueStore)"/> instead.</remarks>
    public static DataValue FromJson(byte[] value) =>
        throw new InvalidOperationException("Use FromJson(value, store). ReferenceStore is no longer available.");

    /// <summary>
    /// Creates a JSON value from canonical CBOR bytes using an explicit
    /// <see cref="IValueStore"/>. The input bytes must already be CBOR-
    /// encoded — see <c>CborJsonCodec.EncodeFromJsonText</c> for the
    /// JSON-text → CBOR boundary.
    /// </summary>
    public static DataValue FromJson(byte[] value, IValueStore store)
    {
        var (p0, p1) = store.StoreBytes(value);
        return new(DataKind.Json, flags: DataValueFlags.InArena, offset: p0.Value, length: p1.Value);
    }

    /// <summary>
    /// Creates a JSON value from a span of canonical CBOR bytes using an explicit
    /// <see cref="IValueStore"/>. Used by <c>json_query</c> when materialising a
    /// subdocument span into the target arena without an intermediate
    /// <c>byte[]</c> allocation.
    /// </summary>
    public static DataValue FromJson(ReadOnlySpan<byte> value, IValueStore store)
    {
        var (p0, p1) = store.StoreBytes(value);
        return new(DataKind.Json, flags: DataValueFlags.InArena, offset: p0.Value, length: p1.Value);
    }

    /// <summary>
    /// Creates a <see cref="DataKind.Json"/> value whose canonical CBOR bytes
    /// live in a <c>.datum-blob</c> sidecar. Mirrors <see cref="FromImageInSidecar(long, long, byte)"/>.
    /// </summary>
    public static DataValue FromJsonInSidecar(long offset, long length, byte storeId = 0) =>
        BuildSidecar(DataKind.Json, offset, length, storeId);

    /// <summary>
    /// Creates a <see cref="DataKind.Image"/> value whose encoded bytes live in a
    /// <c>.datum-blob</c> sidecar. The DataValue carries 64-bit absolute offset,
    /// 40-bit length, and the <c>storeId</c> byte that resolves to the right
    /// <see cref="IBlobSource"/> in the per-query
    /// <see cref="DatumFile.Sidecar.SidecarRegistry"/>.
    /// </summary>
    /// <param name="offset">Absolute byte offset into the sidecar file (includes header).</param>
    /// <param name="length">Number of bytes; 0 ≤ length ≤ <c>2^40 − 1</c> (~1 TiB).</param>
    /// <param name="storeId">Registry storeId byte (defaults to 0 for single-sidecar / first-registered).</param>
    public static DataValue FromImageInSidecar(long offset, long length, byte storeId = 0) =>
        BuildSidecar(DataKind.Image, offset, length, storeId);

    /// <summary>
    /// Variant of <see cref="FromImageInSidecar(long, long, byte)"/> that also stamps
    /// inline width/height/channels onto the DataValue. The dimensions live in
    /// <c>_p4</c>+<c>_p5</c> alongside the sidecar pointer in <c>_p0</c>+<c>_p1</c>+
    /// <c>_p2</c>+<c>_p3</c> — read via <see cref="ImageWidth"/> / <see cref="ImageHeight"/>
    /// / <see cref="ImageChannels"/> without needing to dereference the sidecar.
    /// </summary>
    public static DataValue FromImageInSidecar(long offset, long length, byte storeId, ushort width, ushort height, byte channels)
        => FromImageInSidecar(offset, length, storeId, width, height, channels, hash32: 0);

    /// <summary>
    /// Variant of <see cref="FromImageInSidecar(long, long, byte, ushort, ushort, byte)"/>
    /// that also stamps a pre-computed 32-bit content hash into <c>_p6</c>. Use from
    /// ingest paths that have the encoded bytes in hand so cross-sidecar Equals
    /// short-circuits without fetching the payload. Pass the low 32 bits of
    /// <c>XxHash64</c> over the encoded image bytes.
    /// </summary>
    public static DataValue FromImageInSidecar(long offset, long length, byte storeId, ushort width, ushort height, byte channels, uint hash32)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), offset, "Sidecar offset must be non-negative.");
        }
        if (length < 0 || length > SidecarLengthMax)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length), length,
                $"Sidecar length must be in [0, {SidecarLengthMax}] (40-bit cap).");
        }
        return new(
            DataKind.Image,
            flags: DataValueFlags.InSidecar,
            offset: offset, length: length,
            width: width, height: height, channels: channels,
            charCount: storeId,
            hash32: hash32);
    }

    /// <summary>
    /// Creates a <see cref="DataKind.Audio"/> value whose encoded bytes live in a
    /// <c>.datum-blob</c> sidecar. Mirrors <see cref="FromImageInSidecar(long, long, byte)"/>.
    /// </summary>
    public static DataValue FromAudioInSidecar(long offset, long length, byte storeId = 0) =>
        BuildSidecar(DataKind.Audio, offset, length, storeId);

    /// <summary>
    /// Creates a <see cref="DataKind.Video"/> value whose encoded bytes live in a
    /// <c>.datum-blob</c> sidecar. Mirrors <see cref="FromImageInSidecar(long, long, byte)"/>.
    /// </summary>
    public static DataValue FromVideoInSidecar(long offset, long length, byte storeId = 0) =>
        BuildSidecar(DataKind.Video, offset, length, storeId);

    /// <summary>
    /// Creates a <see cref="DataKind.PointCloud"/> value whose blob lives in a
    /// <c>.datum-blob</c> sidecar. Mirrors <see cref="FromImageInSidecar(long, long, byte)"/>.
    /// </summary>
    public static DataValue FromPointCloudInSidecar(long offset, long length, byte storeId = 0) =>
        BuildSidecar(DataKind.PointCloud, offset, length, storeId);

    /// <summary>
    /// Creates a <see cref="DataKind.Mesh"/> value whose blob lives in a
    /// <c>.datum-blob</c> sidecar. Mirrors <see cref="FromImageInSidecar(long, long, byte)"/>.
    /// </summary>
    public static DataValue FromMeshInSidecar(long offset, long length, byte storeId = 0) =>
        BuildSidecar(DataKind.Mesh, offset, length, storeId);

    /// <summary>
    /// Creates an <c>Array&lt;Audio&gt;</c> value. Mirrors
    /// <see cref="FromImageArray"/> for the audio kind — each element's encoded
    /// bytes are written to <paramref name="store"/>; for N≥2 a slot block is
    /// also written. Used by the cross-arena <c>INSERT … SELECT</c> path for
    /// audio-array columns and by any caller that builds an audio array from
    /// a managed <c>byte[][]</c>.
    /// </summary>
    public static DataValue FromAudioArray(ReadOnlySpan<byte[]> elements, IValueStore store) =>
        BuildBlobArray(elements, DataKind.Audio, store);

    /// <summary>Reads an <c>Array&lt;Audio&gt;</c> value as a <see cref="byte"/>[][].</summary>
    public byte[][] AsAudioArray(IValueStore store, SidecarRegistry? registry = null) =>
        ReadBlobArray(DataKind.Audio, store, registry);

    /// <summary>
    /// Creates an <c>Array&lt;Video&gt;</c> value. Mirrors <see cref="FromImageArray"/>.
    /// </summary>
    public static DataValue FromVideoArray(ReadOnlySpan<byte[]> elements, IValueStore store) =>
        BuildBlobArray(elements, DataKind.Video, store);

    /// <summary>Reads an <c>Array&lt;Video&gt;</c> value as a <see cref="byte"/>[][].</summary>
    public byte[][] AsVideoArray(IValueStore store, SidecarRegistry? registry = null) =>
        ReadBlobArray(DataKind.Video, store, registry);

    /// <summary>
    /// Creates an <c>Array&lt;Json&gt;</c> value. Mirrors <see cref="FromImageArray"/>.
    /// </summary>
    public static DataValue FromJsonArray(ReadOnlySpan<byte[]> elements, IValueStore store) =>
        BuildBlobArray(elements, DataKind.Json, store);

    /// <summary>Reads an <c>Array&lt;Json&gt;</c> value as a <see cref="byte"/>[][].</summary>
    public byte[][] AsJsonArray(IValueStore store, SidecarRegistry? registry = null) =>
        ReadBlobArray(DataKind.Json, store, registry);

    /// <summary>
    /// Creates an <c>Array&lt;PointCloud&gt;</c> value. Mirrors <see cref="FromImageArray"/>.
    /// </summary>
    public static DataValue FromPointCloudArray(ReadOnlySpan<byte[]> elements, IValueStore store) =>
        BuildBlobArray(elements, DataKind.PointCloud, store);

    /// <summary>Reads an <c>Array&lt;PointCloud&gt;</c> value as a <see cref="byte"/>[][].</summary>
    public byte[][] AsPointCloudArray(IValueStore store, SidecarRegistry? registry = null) =>
        ReadBlobArray(DataKind.PointCloud, store, registry);

    /// <summary>
    /// Shared builder for blob-element typed arrays — Audio / Video / Json /
    /// PointCloud. The layout is identical to <see cref="FromImageArray"/>:
    /// N=0 → empty inline value; N=1 → single slot inline; N≥2 → per-element
    /// blob writes + a slot block at the end. The element kind goes on
    /// <see cref="Kind"/>; <see cref="DataValueFlags.IsArray"/> is set.
    /// </summary>
    private static DataValue BuildBlobArray(ReadOnlySpan<byte[]> elements, DataKind kind, IValueStore store)
    {
        if (elements.Length == 0)
        {
            return new(
                kind,
                flags: DataValueFlags.IsArray,
                p0: 0,
                charCount: 0);
        }

        if (elements.Length == 1)
        {
            var (elementP0, elementP1) = store.StoreBytes(elements[0]);
            Span<byte> slotBytes = stackalloc byte[ArraySlot.SizeBytes];
            ArraySlot.Write(slotBytes, elementP0.Value, elementP1.Value);
            int p0 = MemoryMarshal.Read<int>(slotBytes[..4]);
            int p1 = MemoryMarshal.Read<int>(slotBytes[4..8]);
            int p2 = MemoryMarshal.Read<int>(slotBytes[8..12]);
            int p3 = MemoryMarshal.Read<int>(slotBytes[12..16]);
            return new(
                kind,
                flags: DataValueFlags.IsArray,
                p0: p0, p1: p1, p2: p2, p3: p3,
                charCount: 1);
        }

        byte[] slotBlock = new byte[elements.Length * ArraySlot.SizeBytes];
        for (int i = 0; i < elements.Length; i++)
        {
            var (elementP0, elementP1) = store.StoreBytes(elements[i]);
            ArraySlot.Write(
                slotBlock.AsSpan(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                elementP0.Value,
                elementP1.Value);
        }
        var (blockP0, blockP1) = store.StoreBytes(slotBlock);
        return new(
            kind,
            flags: DataValueFlags.IsArray | DataValueFlags.InArena,
            offset: blockP0.Value,
            length: blockP1.Value);
    }

    /// <summary>
    /// Shared reader for blob-element typed arrays — Audio / Video / Json /
    /// PointCloud. Mirrors <see cref="AsImageArray"/>'s storage-tier dispatch
    /// (sidecar / inline / arena) but with the element-kind discriminator
    /// passed in.
    /// </summary>
    private byte[][] ReadBlobArray(DataKind kind, IValueStore store, SidecarRegistry? registry)
    {
        ThrowIfNotReferenceArray(kind);

        if (IsInSidecar)
        {
            IBlobSource src = ResolveSidecarSource(registry);
            ReadOnlySpan<byte> blockBytes = ReadSidecarBytes(registry);
            int elementCount = blockBytes.Length / ArraySlot.SizeBytes;
            byte[][] result = new byte[elementCount][];
            for (int i = 0; i < elementCount; i++)
            {
                ArraySlot.Read(
                    blockBytes.Slice(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                    out long elementOffset,
                    out long elementLength,
                    out _,
                    out _);
                result[i] = src.Read(elementOffset, elementLength).ToArray();
            }
            return result;
        }

        if (IsInline)
        {
            if (_charCount == 0) return [];

            Span<byte> slotBytes = stackalloc byte[ArraySlot.SizeBytes];
            MemoryMarshal.Write(slotBytes[..4], _p0);
            MemoryMarshal.Write(slotBytes[4..8], _p1);
            MemoryMarshal.Write(slotBytes[8..12], _p2);
            MemoryMarshal.Write(slotBytes[12..16], _p3);
            ArraySlot.Read(slotBytes, out long elementOffset, out long elementLength, out _, out _);
            return [store.RetrieveBytes(new ArenaOffset((int)elementOffset), new ArenaLength((int)elementLength))];
        }

        ReadOnlySpan<byte> arenaBlock = store.RetrieveUtf8Span(new ArenaOffset(BackedOffset), new ArenaLength(BackedLength));
        int n = arenaBlock.Length / ArraySlot.SizeBytes;
        byte[][] arenaResult = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            ArraySlot.Read(
                arenaBlock.Slice(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                out long elementOffset,
                out long elementLength,
                out _,
                out _);
            arenaResult[i] = store.RetrieveBytes(new ArenaOffset((int)elementOffset), new ArenaLength((int)elementLength));
        }
        return arenaResult;
    }

    /// <summary>
    /// Creates an <c>Array&lt;Image&gt;</c> value. Each element's encoded bytes are
    /// written to <paramref name="store"/>; for N≥2 a slot block is also written.
    /// Layout matches <see cref="FromStringArray"/> — see
    /// <c>project_reference_type_arrays.md</c>.
    /// </summary>
    public static DataValue FromImageArray(ReadOnlySpan<byte[]> elements, IValueStore store)
    {
        if (elements.Length == 0)
        {
            return new(
                DataKind.Image,
                flags: DataValueFlags.IsArray,
                p0: 0,
                charCount: 0);
        }

        if (elements.Length == 1)
        {
            var (elementP0, elementP1) = store.StoreBytes(elements[0]);
            Span<byte> slotBytes = stackalloc byte[ArraySlot.SizeBytes];
            ArraySlot.Write(slotBytes, elementP0.Value, elementP1.Value);
            int p0 = MemoryMarshal.Read<int>(slotBytes[..4]);
            int p1 = MemoryMarshal.Read<int>(slotBytes[4..8]);
            int p2 = MemoryMarshal.Read<int>(slotBytes[8..12]);
            int p3 = MemoryMarshal.Read<int>(slotBytes[12..16]);
            return new(
                DataKind.Image,
                flags: DataValueFlags.IsArray,
                p0: p0, p1: p1, p2: p2, p3: p3,
                charCount: 1);
        }

        byte[] slotBlock = new byte[elements.Length * ArraySlot.SizeBytes];
        for (int i = 0; i < elements.Length; i++)
        {
            var (elementP0, elementP1) = store.StoreBytes(elements[i]);
            ArraySlot.Write(
                slotBlock.AsSpan(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                elementP0.Value,
                elementP1.Value);
        }
        var (blockP0, blockP1) = store.StoreBytes(slotBlock);
        return new(
            DataKind.Image,
            flags: DataValueFlags.IsArray | DataValueFlags.InArena,
            offset: blockP0.Value,
            length: blockP1.Value);
    }

    /// <summary>
    /// Reads an <c>Array&lt;Image&gt;</c> value as a <see cref="byte"/>[][].
    /// </summary>
    public byte[][] AsImageArray(IValueStore store, SidecarRegistry? registry = null)
    {
        ThrowIfNotReferenceArray(DataKind.Image);

        if (IsInSidecar)
        {
            IBlobSource src = ResolveSidecarSource(registry);
            ReadOnlySpan<byte> blockBytes = ReadSidecarBytes(registry);
            int elementCount = blockBytes.Length / ArraySlot.SizeBytes;
            byte[][] result = new byte[elementCount][];
            for (int i = 0; i < elementCount; i++)
            {
                ArraySlot.Read(
                    blockBytes.Slice(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                    out long elementOffset,
                    out long elementLength,
                    out _,
                    out _);
                result[i] = src.Read(elementOffset, elementLength).ToArray();
            }
            return result;
        }

        if (IsInline)
        {
            if (_charCount == 0) return [];

            Span<byte> slotBytes = stackalloc byte[ArraySlot.SizeBytes];
            MemoryMarshal.Write(slotBytes[..4], _p0);
            MemoryMarshal.Write(slotBytes[4..8], _p1);
            MemoryMarshal.Write(slotBytes[8..12], _p2);
            MemoryMarshal.Write(slotBytes[12..16], _p3);
            ArraySlot.Read(slotBytes, out long elementOffset, out long elementLength, out _, out _);
            return [store.RetrieveBytes(new ArenaOffset((int)elementOffset), new ArenaLength((int)elementLength))];
        }

        ReadOnlySpan<byte> arenaBlock = store.RetrieveUtf8Span(new ArenaOffset(BackedOffset), new ArenaLength(BackedLength));
        int n = arenaBlock.Length / ArraySlot.SizeBytes;
        byte[][] arenaResult = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            ArraySlot.Read(
                arenaBlock.Slice(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                out long elementOffset,
                out long elementLength,
                out _,
                out _);
            arenaResult[i] = store.RetrieveBytes(new ArenaOffset((int)elementOffset), new ArenaLength((int)elementLength));
        }
        return arenaResult;
    }

    /// <summary>
    /// Creates an <see cref="DataKind.Image"/> value that references bytes already
    /// written to an <see cref="IValueStore"/> at the given <paramref name="offset"/>
    /// and <paramref name="length"/>. Use when the bytes were streamed directly into
    /// an arena (e.g. via <see cref="Arena.AppendFromStream"/>) to avoid the managed
    /// <c>byte[]</c> allocation that <see cref="FromImage(byte[], IValueStore)"/>
    /// would otherwise force.
    /// </summary>
    public static DataValue FromImageAtOffset(long offset, long length) =>
        new(DataKind.Image, flags: DataValueFlags.InArena, offset: offset, length: length);

    /// <summary>
    /// Variant of the basic image-at-offset factory that also stamps inline dimensions
    /// metadata. Pass dimensions parsed from the image header so accessors like
    /// <see cref="ImageWidth"/>/<see cref="ImageHeight"/> work without decoding.
    /// </summary>
    public static DataValue FromImageAtOffset(long offset, long length, ushort width, ushort height, byte channels = 0) =>
        new(DataKind.Image, flags: DataValueFlags.InArena,
            offset: offset, length: length,
            width: width, height: height, channels: channels);

    /// <summary>
    /// Variant of <see cref="FromImageAtOffset(long, long, ushort, ushort, byte)"/> that
    /// also stamps a pre-computed 32-bit content hash into <c>_p6</c>. Use from ingest
    /// paths that have the encoded bytes in hand (e.g. the media-bag deserializer) so
    /// downstream Equals / GetHashCode can short-circuit without bytes; pass the low
    /// 32 bits of <c>XxHash64</c> over the encoded image payload.
    /// </summary>
    public static DataValue FromImageAtOffset(long offset, long length, ushort width, ushort height, byte channels, uint hash32) =>
        new(DataKind.Image, flags: DataValueFlags.InArena,
            offset: offset, length: length,
            width: width, height: height, channels: channels, hash32: hash32);

    /// <summary>
    /// Creates a <see cref="DataKind.Audio"/> value that references bytes already
    /// written to an <see cref="IValueStore"/> at the given <paramref name="offset"/>
    /// and <paramref name="length"/>. Mirrors <see cref="FromImageAtOffset(long, long)"/>
    /// for audio — use when the encoded audio bytes were streamed directly into an
    /// arena to avoid a managed <c>byte[]</c> allocation.
    /// </summary>
    public static DataValue FromAudioAtOffset(long offset, long length) =>
        new(DataKind.Audio, flags: DataValueFlags.InArena, offset: offset, length: length);

    /// <summary>
    /// Variant of <see cref="FromAudioAtOffset(long, long)"/> that stamps inline
    /// container metadata (sample-rate, channels, bit-depth, frame-count) plus a
    /// pre-computed 32-bit content hash. Use from ingest paths that have parsed
    /// the audio header in hand so accessors like <see cref="AudioSampleRate"/>
    /// skip a full decode and cross-arena Equals short-circuits without re-reading
    /// the bytes. Caps mirror <see cref="FromAudio(byte[], IValueStore, uint, byte, byte, uint)"/>.
    /// </summary>
    public static DataValue FromAudioAtOffset(
        long offset, long length,
        uint sampleRate, byte channels, byte bitDepth, uint frameCount,
        uint hash32)
    {
        int packedP4 = PackAudioP4(sampleRate, channels, bitDepth);
        return new(DataKind.Audio, flags: DataValueFlags.InArena,
            offset: offset, length: length,
            p4: packedP4,
            p5: unchecked((int)frameCount),
            p6: unchecked((int)hash32));
    }

    /// <summary>
    /// Creates a byte-array value that references bytes already written to an
    /// <see cref="IValueStore"/> at the given offset and length. Parallel to
    /// <see cref="FromImageAtOffset(long, long)"/> for generic binary payloads where the
    /// bytes are already arena-resident. Produces <see cref="DataKind.UInt8"/>
    /// with the <see cref="DataValueFlags.IsArray"/> flag.
    /// </summary>
    public static DataValue FromByteArrayAtOffset(long offset, long length) =>
        new(
            DataKind.UInt8,
            flags: DataValueFlags.InArena | DataValueFlags.IsArray,
            offset: offset, length: length);
}

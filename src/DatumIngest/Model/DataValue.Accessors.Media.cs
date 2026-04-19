using DatumIngest.DatumFile.Sidecar;

namespace DatumIngest.Model;

public readonly partial struct DataValue
{
    /// <summary>
    /// Returns the encoded image byte array. For arena-backed values, reads from
    /// <paramref name="store"/>; for sidecar-backed values, looks up the value's
    /// <c>storeId</c> in <paramref name="registry"/> to find its
    /// <see cref="IBlobSource"/>. The flag on the DataValue determines which path runs.
    /// </summary>
    public byte[] AsImage(IValueStore store, SidecarRegistry? registry = null)
    {
        ThrowIfNullOrWrongKind(DataKind.Image);

        if (IsInSidecar)
        {
            return ReadSidecarBytes(registry).ToArray();
        }
        return store.RetrieveBytes(new ArenaOffset(BackedOffset), new ArenaLength(BackedLength));
    }

    // ───────────────────────── Image inline metadata ─────────────────────────

    /// <summary>
    /// Inline pixel width for <see cref="DataKind.Image"/> values, populated at ingest
    /// time when the image header was parsed (e.g. <c>ImageHeaderParser</c>). Returns
    /// 0 when not populated (legacy values or sources that didn't parse the header) —
    /// callers should fall back to a full decode if they need authoritative dimensions.
    /// Lives in the low 16 bits of <c>_p4</c>.
    /// </summary>
    public ushort ImageWidth => _kind == DataKind.Image ? unchecked((ushort)_p4) : (ushort)0;

    /// <summary>
    /// Inline pixel height for <see cref="DataKind.Image"/> values; companion to
    /// <see cref="ImageWidth"/>. Lives in the high 16 bits of <c>_p4</c>.
    /// </summary>
    public ushort ImageHeight => _kind == DataKind.Image ? unchecked((ushort)(_p4 >> 16)) : (ushort)0;

    /// <summary>
    /// Inline channel count for <see cref="DataKind.Image"/> values (1=grayscale,
    /// 3=RGB, 4=RGBA, …). Returns 0 when not populated. Lives in the low byte of <c>_p5</c>.
    /// </summary>
    public byte ImageChannels => _kind == DataKind.Image ? unchecked((byte)_p5) : (byte)0;

    // ───────────────────────── Audio inline metadata ─────────────────────────
    // Post-Round-2 layout:
    //   _p4 (32 b): sampleRate(24) | channels(4) | bitDepthCode(4)
    //   _p5 (32 b): frameCount
    //   _p6 (32 b): low 32 bits of XxHash64(bytes)

    /// <summary>Inline sample rate (Hz) for Audio values; 0 when not stamped. Low 24 bits of <c>_p4</c>.</summary>
    public uint AudioSampleRate => _kind == DataKind.Audio ? unchecked((uint)_p4 & AudioMaxSampleRate) : 0u;

    /// <summary>Inline channel count for Audio values; 0 when not stamped. Bits 24-27 of <c>_p4</c>.</summary>
    public byte AudioChannels => _kind == DataKind.Audio ? unchecked((byte)((_p4 >> 24) & AudioMaxChannels)) : (byte)0;

    /// <summary>Inline bit depth for Audio values (8/16/24/32/64); 0 when unknown or not stamped. Bits 28-31 of <c>_p4</c> via <see cref="DecodeAudioBitDepth"/>.</summary>
    public byte AudioBitDepth => _kind == DataKind.Audio ? DecodeAudioBitDepth(unchecked((byte)((_p4 >> 28) & 0xF))) : (byte)0;

    /// <summary>Inline frame count (samples per channel) for Audio values; 0 when not stamped. <c>_p5</c>.</summary>
    public uint AudioFrameCount => _kind == DataKind.Audio ? unchecked((uint)_p5) : 0u;

    // ───────────────────────── Video inline metadata ─────────────────────────

    /// <summary>Inline pixel width for Video values; 0 when not stamped. Low 16 bits of <c>_p4</c>.</summary>
    public ushort VideoWidth => _kind == DataKind.Video ? unchecked((ushort)_p4) : (ushort)0;

    /// <summary>Inline pixel height for Video values; 0 when not stamped. High 16 bits of <c>_p4</c>.</summary>
    public ushort VideoHeight => _kind == DataKind.Video ? unchecked((ushort)(_p4 >> 16)) : (ushort)0;

    /// <summary>
    /// Inline frame rate as 8.8 fixed-point — multiply by <c>1/256.0</c> to recover the FPS
    /// as a float (e.g. raw 7680 = 30.0 fps; raw 6133 ≈ 23.967 fps). 0 when not stamped.
    /// Low 16 bits of <c>_p5</c>.
    /// </summary>
    public ushort VideoFpsX256 => _kind == DataKind.Video ? unchecked((ushort)_p5) : (ushort)0;

    /// <summary>Inline codec identifier (enum byte: H264/H265/AV1/VP9/…). Byte 2 of <c>_p5</c>.</summary>
    public byte VideoCodec => _kind == DataKind.Video ? unchecked((byte)(_p5 >> 16)) : (byte)0;

    /// <summary>Inline frame count for Video values; 0 when not stamped. <c>_p6</c>.</summary>
    public uint VideoFrameCount => _kind == DataKind.Video ? unchecked((uint)_p6) : 0u;

    // ───────────────────────── PointCloud inline metadata ─────────────────────────

    /// <summary>Inline point count for PointCloud values; 0 when not stamped. <c>_p4</c>.</summary>
    public uint PointCloudCount => _kind == DataKind.PointCloud ? unchecked((uint)_p4) : 0u;

    /// <summary>
    /// Inline attribute flag byte for PointCloud values. Bits: 0=has_color, 1=has_normal,
    /// 2=has_intensity, 3=organized. 0 when not stamped. Low byte of <c>_p5</c>.
    /// </summary>
    public byte PointCloudAttributes => _kind == DataKind.PointCloud ? unchecked((byte)_p5) : (byte)0;

    // ───────────────────────── Mesh inline metadata ─────────────────────────

    /// <summary>Inline vertex count for Mesh values; 0 when not stamped. <c>_p4</c>.</summary>
    public uint MeshVertexCount => _kind == DataKind.Mesh ? unchecked((uint)_p4) : 0u;

    /// <summary>Inline triangle count for Mesh values; 0 when not stamped. <c>_p5</c>.</summary>
    public uint MeshTriangleCount => _kind == DataKind.Mesh ? unchecked((uint)_p5) : 0u;

    /// <summary>
    /// Inline attribute flag byte for Mesh values. Bits: 0=has_color, 1=has_normals,
    /// 2=has_uvs, 3=has_texture. 0 when not stamped. Low byte of <c>_p6</c>.
    /// </summary>
    public byte MeshAttributes => _kind == DataKind.Mesh ? unchecked((byte)_p6) : (byte)0;

    /// <summary>
    /// Returns the raw PointCloud blob (40-byte header followed by interleaved
    /// per-point payload). For arena-backed values, reads from <paramref name="store"/>;
    /// for sidecar-backed values, looks up the value's <c>storeId</c> in
    /// <paramref name="registry"/>. Callers parse the header via
    /// <c>DatumIngest.Model.Spatial.PointCloudHeader.Read</c>.
    /// </summary>
    public byte[] AsPointCloud(IValueStore store, SidecarRegistry? registry = null)
    {
        ThrowIfNullOrWrongKind(DataKind.PointCloud);

        if (IsInSidecar)
        {
            return ReadSidecarBytes(registry).ToArray();
        }
        return store.RetrieveBytes(new ArenaOffset(BackedOffset), new ArenaLength(BackedLength));
    }

    /// <summary>
    /// Returns the raw Mesh blob (48-byte header followed by interleaved
    /// per-vertex payload, triangle indices, and optional embedded texture).
    /// For arena-backed values, reads from <paramref name="store"/>; for
    /// sidecar-backed values, looks up the value's <c>storeId</c> in
    /// <paramref name="registry"/>. Callers parse the header via
    /// <c>DatumIngest.Model.Spatial.MeshHeader.Read</c>.
    /// </summary>
    public byte[] AsMesh(IValueStore store, SidecarRegistry? registry = null)
    {
        ThrowIfNullOrWrongKind(DataKind.Mesh);

        if (IsInSidecar)
        {
            return ReadSidecarBytes(registry).ToArray();
        }
        return store.RetrieveBytes(new ArenaOffset(BackedOffset), new ArenaLength(BackedLength));
    }
}

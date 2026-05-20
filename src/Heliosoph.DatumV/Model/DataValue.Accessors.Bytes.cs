using System.IO.Hashing;
using Heliosoph.DatumV.DatumFile.Sidecar;

namespace Heliosoph.DatumV.Model;

public readonly partial struct DataValue
{
    // ─────────────────────── Reference-type accessors ─────────────────────────

    /// <summary>
    /// Returns the byte array payload. For arena-backed values, reads from
    /// <paramref name="store"/>; for sidecar-backed values, looks up the source in
    /// <paramref name="registry"/> by the value's <c>storeId</c> and reads from there.
    /// The flag on the DataValue determines which path runs.
    /// </summary>
    public byte[] AsUInt8Array(IValueStore store, SidecarRegistry? registry = null)
    {
        if (!IsByteArrayKind)
        {
            throw new InvalidOperationException(
                $"AsUInt8Array is only valid for byte arrays (UInt8 + IsArray); got {_kind}.");
        }
        if (IsNull)
        {
            throw new InvalidOperationException("Value is null.");
        }

        if (IsInSidecar)
        {
            return ReadSidecarBytes(registry).ToArray();
        }
        return store.RetrieveBytes(new ArenaOffset(BackedOffset), new ArenaLength(BackedLength));
    }

    /// <summary>
    /// True when this value carries a byte-array payload — i.e.
    /// <see cref="DataKind.UInt8"/> with the <see cref="DataValueFlags.IsArray"/>
    /// flag set. Used to dispatch byte-content paths
    /// (<see cref="AsByteSpan"/>, <see cref="AsUInt8Array(IValueStore, SidecarRegistry?)"/>)
    /// without enumerating the kind in every call site.
    /// </summary>
    public bool IsByteArrayKind =>
        _kind == DataKind.UInt8 && (_flags & DataValueFlags.IsArray) != 0;

    /// <summary>
    /// True when this value carries an encoded-blob payload — <see cref="DataKind.Image"/>,
    /// <see cref="DataKind.Audio"/>, <see cref="DataKind.Video"/>, <see cref="DataKind.Json"/>,
    /// or <see cref="DataKind.PointCloud"/>. All share the same byte-content storage shape
    /// (inline / arena / sidecar at <c>(_p0, _p1)</c>) and the same accessors
    /// (<see cref="AsByteSpan"/>, etc.); only the kind discriminator distinguishes them.
    /// JSON's bytes are canonical CBOR (RFC 7049 §3.9); PointCloud's bytes are a 40-byte
    /// header plus interleaved per-point payload (see
    /// <c>Heliosoph.DatumV.Model.Spatial.PointCloudHeader</c>); the other three are codec-specific.
    /// </summary>
    public bool IsBlobKind =>
        _kind is DataKind.Image or DataKind.Audio or DataKind.Video or DataKind.Json or DataKind.PointCloud or DataKind.Mesh;

    /// <summary>
    /// Returns the byte payload for a byte-array (UInt8 + IsArray) or
    /// <see cref="DataKind.Image"/> value as a <see cref="ReadOnlySpan{T}"/>, without
    /// materializing a managed <c>byte[]</c>. Zero-allocation hot-path reader.
    /// </summary>
    /// <remarks>
    /// For arena-backed values the span is valid only while <paramref name="store"/>'s
    /// backing arena is alive; for sidecar-backed values the span lives as long as the
    /// resolved <see cref="IBlobSource"/>'s mmap view does. Callers must consume the
    /// span before whichever store backs it goes away.
    /// </remarks>
    public ReadOnlySpan<byte> AsByteSpan(IValueStore store, SidecarRegistry? registry = null)
    {
        // Image/Audio/Video and (UInt8 + IsArray) all carry byte content at (_p0, _p1) — read path is identical.
        if (!IsBlobKind && !IsByteArrayKind)
        {
            throw new InvalidOperationException(
                $"AsByteSpan is only valid for byte-content kinds (Image/Audio/Video/Json/PointCloud/Mesh or UInt8 + IsArray); got {_kind}.");
        }

        if (IsInSidecar)
        {
            return ReadSidecarBytes(registry);
        }
        return store.RetrieveUtf8Span(new ArenaOffset(BackedOffset), new ArenaLength(BackedLength));
    }

    /// <summary>
    /// Resolves a sidecar-backed byte payload by looking up the value's
    /// <see cref="SidecarStoreId"/> in <paramref name="registry"/>. Throws when no
    /// registry is supplied or the storeId isn't registered — sidecar-backed
    /// DataValues cannot be read against an arena alone.
    /// </summary>
    private ReadOnlySpan<byte> ReadSidecarBytes(SidecarRegistry? registry)
    {
        if (registry is null)
        {
            throw new InvalidOperationException(
                "DataValue is sidecar-backed (DataValueFlags.InSidecar) but no SidecarRegistry was provided. " +
                "Pass the ExecutionContext's registry (or the frame's) so the storeId can resolve.");
        }

        IBlobSource? source = registry.Resolve(SidecarStoreId)
            ?? throw new InvalidOperationException(
                $"Sidecar storeId {SidecarStoreId} is not registered in the supplied " +
                "SidecarRegistry. The DataValue references a sidecar that wasn't opened by " +
                "this query — likely the table provider didn't register its IBlobSource.");

        Heliosoph.DatumV.Diagnostics.HashGateStats.RecordSidecarFetch();
        return source.Read(SidecarOffset, SidecarLength);
    }

    /// <summary>Returns the text string payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    /// <remarks>
    /// Works for inline strings (whose bytes are self-contained in the struct) without a store.
    /// For non-inline strings (reference-store or arena-backed), use
    /// <see cref="AsString(IValueStore, SidecarRegistry?)"/> or <see cref="AsString(Arena)"/> —
    /// those require an explicit store to resolve the payload.
    /// </remarks>
    public string AsString()
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        if (IsInline) return System.Text.Encoding.UTF8.GetString(InlineUtf8Span);
        throw new InvalidOperationException(
            "AsString() without a store only supports inline strings. For non-inline strings, " +
            "use AsString(IValueStore, SidecarRegistry?) or AsString(Arena).");
    }

    /// <summary>
    /// Returns the text string payload, resolving inline / arena / sidecar
    /// storage tiers uniformly. Inline values come from the struct itself,
    /// arena-backed values come from <paramref name="store"/>, and
    /// sidecar-backed values come from <paramref name="registry"/> via the
    /// value's recorded <c>storeId</c>.
    /// </summary>
    /// <param name="store">Used for arena-backed values; ignored for inline / sidecar.</param>
    /// <param name="registry">
    /// Required for sidecar-backed values; ignored for inline / arena. Callers
    /// that work only against arena-backed batches may omit this — a
    /// sidecar-backed value will then throw with a clear "no SidecarRegistry
    /// was provided" message via <see cref="ReadSidecarBytes"/>.
    /// </param>
    public string AsString(IValueStore store, SidecarRegistry? registry = null)
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        if (IsInline) return System.Text.Encoding.UTF8.GetString(InlineUtf8Span);
        if (IsInSidecar) return System.Text.Encoding.UTF8.GetString(ReadSidecarBytes(registry));
        return store.RetrieveString(new ArenaOffset(BackedOffset), new ArenaLength(BackedLength));
    }

    /// <summary>
    /// Unicode code-point count of a String value — matches PG <c>length(text)</c>
    /// semantics (surrogate-pair characters count as 1, in contrast to
    /// <see cref="string.Length"/> / <see cref="System.Text.Encoding.GetCharCount(System.ReadOnlySpan{byte})"/>
    /// which count UTF-16 code units). Cached in <c>_charCount</c> at construction;
    /// for arena-slice values where the cache reads as 0 (no count stamped) or 65535
    /// (overflow sentinel for very long strings), falls back to a single
    /// non-continuation-byte walk via <paramref name="store"/>.
    /// </summary>
    /// <param name="store">Fallback store for arena-slice values where the count was not cached.</param>
    /// <returns>The number of Unicode code points in the string.</returns>
    public int StringCharCount(IValueStore store)
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        // Inline: code-point count cached in the high byte of _charCount.
        if (IsInline) return InlineCharCount;
        // _charCount holds the full code-point count (ushort). 65535 = overflow
        // sentinel → walk the bytes. 0 = unknown (e.g. FromStringSlice) → walk.
        return _charCount is not 0 and not ushort.MaxValue
            ? _charCount
            : CountUtf8CodePoints(store.RetrieveUtf8Span(new ArenaOffset(BackedOffset), new ArenaLength(BackedLength)));
    }

    /// <summary>
    /// Returns the cached code-point count as-is, without falling back to a byte walk.
    /// Returns <c>0</c> for values that never cached the count (e.g. <see cref="FromStringSlice"/>),
    /// and <c>65535</c> for strings that overflowed the 16-bit cache. Same PG-compatible
    /// code-point semantics as <see cref="StringCharCount(IValueStore)"/>.
    /// </summary>
    /// <remarks>
    /// Zero-allocation hot-path reader for callers that only need a coarse "how big is this string"
    /// signal (e.g. the auto-indexing size threshold). Callers that need exact lengths for strings
    /// near or above 65535 code points should use <see cref="StringCharCount(IValueStore)"/> instead.
    /// </remarks>
    public int RawCharCount => IsInline ? InlineCharCount : _charCount;

    /// <summary>
    /// Returns the byte length of this value's content as stored in process memory
    /// (inline payload or arena-backed). Returns <c>0</c> for null, sidecar-backed
    /// values (whose bytes live on disk — see <see cref="SidecarByteLength"/>), and
    /// inline scalars whose bytes are entirely inside the <see cref="DataValue"/>
    /// struct itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zero-allocation hot-path reader: every kind reads its byte count from the
    /// inline payload words (<c>_p1</c>/<c>_p2</c>) or from <c>InlineByteLength</c>,
    /// without materializing the value through an <see cref="IValueStore"/>. Use
    /// this for two-pass encoders sizing pooled buffers, memory-budget estimation,
    /// and any other "how big is this payload" query.
    /// </para>
    /// <para>
    /// Per-kind layout:
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="DataKind.String"/>:
    ///     UTF-8 byte length (<see cref="InlineByteLength"/> when inline; <c>_p1</c> when arena-backed).</item>
    ///   <item>Any typed array (Kind + IsArray) and <see cref="DataKind.Image"/>:
    ///     byte length (<see cref="InlineByteLength"/> when inline; <c>_p1</c> when arena-backed).</item>
    ///   <item>Inline arrays (<see cref="IsInlineArray"/>): element count × element size.</item>
    ///   <item>All other kinds (inline scalars, null, sidecar): <c>0</c>.</item>
    /// </list>
    /// </remarks>
    public int ContentByteLength
    {
        get
        {
            if (IsNull || IsInSidecar) return 0;

            if (IsInline)
            {
                if (IsInlineArray) return InlineArrayElementCount * ScalarByteSize(_kind) + ShapePrefixByteCount;
                if (_kind == DataKind.String) return InlineByteLength;
                return 0;
            }

            // Arena-backed. BackedLength already covers the shape prefix when IsMultiDim is set.
            // BackedLength is a 40-bit value (~1 TiB cap); a value > int.MaxValue would mean a
            // single arena-backed payload exceeded 2 GB, which int-returning callers can't
            // represent — surface that as an explicit overflow instead of silent truncation.
            if (IsArray || IsBlobKind) return checked((int)BackedLength);
            return _kind switch
            {
                DataKind.String => checked((int)BackedLength),
                _ => 0,
            };
        }
    }

    /// <summary>
    /// Returns the byte length of this value's payload in a <c>.datum-blob</c>
    /// sidecar, or <c>null</c> when the value is not sidecar-backed. The length
    /// is a 40-bit value (max ~1 TiB) packed across <c>_p2</c> and the low byte
    /// of <c>_p3</c>; see <see cref="BuildSidecar"/> for the layout.
    /// </summary>
    /// <remarks>
    /// Pairs with <see cref="ContentByteLength"/>: that returns in-memory bytes
    /// (zero for sidecar values), this returns on-disk bytes (null for non-sidecar
    /// values). Encoders that need total content size compute
    /// <c>ContentByteLength + (SidecarByteLength ?? 0)</c>.
    /// </remarks>
    public long? SidecarByteLength => IsInSidecar ? SidecarLength : null;

    /// <summary>
    /// Returns the cached XxHash64 of the string's UTF-8 bytes as a single ulong,
    /// or <c>0</c> for values with no cached hash (e.g. arena-slice strings built
    /// via <see cref="FromStringSlice"/>).
    /// </summary>
    /// <remarks>
    /// Zero-allocation hot-path reader for frequency-sketch accumulators that need
    /// a content-addressed hash key without materializing the string. When the
    /// cached hash is absent, callers must fall back to hashing the UTF-8 bytes
    /// through an <see cref="IValueStore"/>.
    /// </remarks>
    public ulong RawContentHash
    {
        get
        {
            // Inline strings don't cache a hash — the inline payload bytes don't have room
            // for a hash slot beyond the UTF-8 content. XxHash64 over a <=16-byte span is
            // a single stripe, effectively free.
            if (IsInline) return XxHash64.HashToUInt64(InlineUtf8Span);
            // Arena/sidecar-backed: cache lives in _p4+_p5 (payload bytes 16-23). For values
            // constructed without a hash (e.g. sidecar-backed reads or arena-slice paths),
            // the slot is zero — the "no cached hash" sentinel honored by CompareStrings.
            return (uint)_p4 | ((ulong)(uint)_p5 << 32);
        }
    }

    /// <summary>
    /// Returns the element count for arena-backed typed-array values without
    /// allocating. Returns the byte count for byte arrays (1 element = 1 byte) and
    /// derives element count from byte count for wider element kinds via
    /// <see cref="ScalarByteSize"/>.
    /// </summary>
    /// <returns>The element count, or -1 when not derivable inline (non-array, sidecar, etc.).</returns>
    public int ElementCount
    {
        get
        {
            // Byte arrays are not allowed to carry IsMultiDim (asserted at construction);
            // BackedLength is the raw byte count which equals the element count.
            if (IsByteArrayKind) return checked((int)BackedLength);
            if (IsInlineArray) return InlineArrayElementCount;
            if (!IsArray) return -1;
            if ((_flags & DataValueFlags.InArena) != 0)
            {
                int elementSize = ScalarByteSize(_kind);
                if (elementSize <= 0) return -1;
                long elementBytes = BackedLength - ShapePrefixByteCount;
                return checked((int)(elementBytes / elementSize));
            }
            if (IsInSidecar)
            {
                int elementSize = ScalarByteSize(_kind);
                if (elementSize <= 0) return -1;
                long elementBytes = SidecarLength - ShapePrefixByteCount;
                return (int)(elementBytes / elementSize);
            }
            return -1;
        }
    }

    /// <summary>
    /// Returns the UTF-8 byte span, resolving inline / arena / sidecar
    /// storage tiers uniformly. Inline values come from the struct,
    /// arena-backed values come from <paramref name="store"/>, and
    /// sidecar-backed values come from <paramref name="registry"/>.
    /// </summary>
    /// <param name="store">Used for arena-backed values; ignored for inline / sidecar.</param>
    /// <param name="registry">
    /// Required for sidecar-backed values; ignored for inline / arena. Callers
    /// that work only against arena-backed batches may omit this — a
    /// sidecar-backed value will then throw with a clear "no SidecarRegistry
    /// was provided" message via <see cref="ReadSidecarBytes"/>.
    /// </param>
    public ReadOnlySpan<byte> AsUtf8Span(IValueStore store, SidecarRegistry? registry = null)
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        if (IsInline) return InlineUtf8Span;
        if (IsInSidecar) return ReadSidecarBytes(registry);
        return store.RetrieveUtf8Span(new ArenaOffset(BackedOffset), new ArenaLength(BackedLength));
    }

    /// <summary>
    /// Returns the text string payload, resolving arena-backed values from the
    /// given <see cref="Arena"/>.
    /// </summary>
    /// <param name="arena">The arena that owns the UTF-8 bytes.</param>
    /// <returns>The decoded string.</returns>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public string AsString(Arena arena)
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        if (IsInline) return System.Text.Encoding.UTF8.GetString(InlineUtf8Span);
        return arena.GetString(BackedOffset, checked((int)BackedLength));
    }

}

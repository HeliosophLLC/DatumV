namespace DatumIngest.Model;

public readonly partial struct DataValue
{
    // ───────────────────────── Equality ─────────────────────────

    /// <inheritdoc/>
    public override bool Equals(object? other) => other is DataValue dv && Equals(dv);

    /// <inheritdoc/>
    public bool Equals(DataValue other)
    {
        if (_kind != other._kind) return false;
        if (IsNull && other.IsNull) return true;
        if (IsNull != other.IsNull) return false;

        // Array values (IsArray) must short-circuit before the scalar kind switch
        // — that switch only compares the payload words a scalar of that kind uses
        // (e.g. Float32 only checks _p0), which misses the rest of an inline
        // array's bytes and the length of an arena-backed array. Inline arrays
        // pack bytes across _p0–_p3 with the element count in the low byte of
        // _charCount; arena-backed arrays store (offset, length) in _p0/_p1
        // with _p2/_p3 zero. A unified payload + flags + _charCount comparison
        // handles both: inline gets full-byte equality, arena gets
        // offset-equality (same store ⇒ identical content) plus length, and
        // any Array<Struct> typeId carried in _charCount is included.
        if (IsArray)
        {
            if (!other.IsArray
                || _flags != other._flags
                || _charCount != other._charCount)
            {
                return false;
            }

            // Same offset+length+_p2/_p3: same store, identical bytes — fast path.
            if (_p0 == other._p0 && _p1 == other._p1 && _p2 == other._p2 && _p3 == other._p3)
            {
                return true;
            }

            // Cross-arena: consult the cached content hash stamped at construction
            // (FromArenaArray / FromArenaArrayBytes write XxHash64 into _p4+_p5).
            // XxHash64 collision rate is ~1/2^64, so equal hashes are treated as
            // equal content — matches the String cached-hash branch.
            ulong leftHash = (uint)_p4 | ((ulong)(uint)_p5 << 32);
            ulong rightHash = (uint)other._p4 | ((ulong)(uint)other._p5 << 32);
            if (leftHash != 0 && rightHash != 0)
            {
                if (leftHash != rightHash)
                {
                    DatumIngest.Diagnostics.HashGateStats.RecordHashShortCircuit();
                    return false;
                }
                return true;
            }

            // Legacy path: at least one side has no cached hash (e.g. inline array
            // or pre-hash-stamping construction). Fall back to byte-identity via
            // offset comparison, which is conservative-false across arenas.
            return false;
        }

        return _kind switch
        {
            // Unknown sentinel: no payload, so non-null Unknown values are always equal.
            DataKind.Unknown => true,

            // Small integer types: compare _p0 directly (no -0 ambiguity for integers).
            DataKind.UInt8 or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Boolean or DataKind.Date
            or DataKind.Type
                => _p0 == other._p0,

            // 64-bit integer/temporal types: compare _p0 + _p1.
            DataKind.Int64 or DataKind.UInt64 or DataKind.Time or DataKind.Duration
                => _p0 == other._p0 && _p1 == other._p1,

            // Float types: recover the actual float so IEEE semantics (NaN != NaN, -0 == 0) are preserved.
            DataKind.Float16
                => BitConverter.UInt16BitsToHalf((ushort)_p0) == BitConverter.UInt16BitsToHalf((ushort)other._p0),
            DataKind.Float32
                => BitConverter.Int32BitsToSingle(_p0) == BitConverter.Int32BitsToSingle(other._p0),
            DataKind.Float64
                => BitConverter.Int64BitsToDouble(ReadLong()) == BitConverter.Int64BitsToDouble(other.ReadLong()),

            // Decimal: 16 bytes inline; bit-equality is value-equality (decimal has no -0/NaN).
            DataKind.Decimal
                => _p0 == other._p0 && _p1 == other._p1 && _p2 == other._p2 && _p3 == other._p3,

            // 128-bit integers: 16 bytes inline, bit-equality = value-equality.
            DataKind.UInt128 or DataKind.Int128
                => _p0 == other._p0 && _p1 == other._p1 && _p2 == other._p2 && _p3 == other._p3,

            // Points: per-component IEEE float compare so NaN != NaN and -0 == 0.
            DataKind.Point2D
                => BitConverter.Int32BitsToSingle(_p0) == BitConverter.Int32BitsToSingle(other._p0)
                && BitConverter.Int32BitsToSingle(_p1) == BitConverter.Int32BitsToSingle(other._p1),
            DataKind.Point3D
                => BitConverter.Int32BitsToSingle(_p0) == BitConverter.Int32BitsToSingle(other._p0)
                && BitConverter.Int32BitsToSingle(_p1) == BitConverter.Int32BitsToSingle(other._p1)
                && BitConverter.Int32BitsToSingle(_p2) == BitConverter.Int32BitsToSingle(other._p2),

            // Reference types:
            DataKind.String
                => CompareStrings(in this, in other),
            DataKind.Uuid
                => _p0 == other._p0 && _p1 == other._p1 && _p2 == other._p2 && _p3 == other._p3,
            // Timestamp / TimestampTz are 8-byte ticks; _p2 is unused.
            DataKind.Timestamp or DataKind.TimestampTz
                => _p0 == other._p0 && _p1 == other._p1,
            // Image: prefer the cached 32-bit content hash in _p6 (stamped at
            // ingest time when the bytes were in hand) so cross-arena / cross-sidecar
            // images compare equal without a payload fetch. Falls back to
            // offset-equality when either side lacks the cached hash (legacy
            // FromImageAtOffset / FromImageInSidecar callers that didn't plumb it).
            DataKind.Image
                => CompareImage(in this, in other),
            // Audio: same shape as Image — cached 32-bit content hash in _p6 with
            // offset-equality as the legacy fallback. The repacked _p4/_p5 metadata
            // is content-identifying (sampleRate/channels/bitDepth/frameCount), so
            // equal hashes imply equal metadata too.
            DataKind.Audio
                => CompareAudio(in this, in other),
            // For reference types without a store, use offset-equality: same (_p0,_p1) in the
            // same store means identical content. Different offsets → unknown, return false.
            DataKind.Video or DataKind.Json or DataKind.PointCloud or DataKind.Mesh
                => _p0 == other._p0 && _p1 == other._p1,
            // VideoFrame is inline: (videoId, frameIndex) value-equality.
            DataKind.VideoFrame
                => _p0 == other._p0 && _p1 == other._p1,
            DataKind.Struct
                => _meta == other._meta && _p0 == other._p0 && _p1 == other._p1,
            _ => false,
        };
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        if (IsNull) return HashCode.Combine(_kind, true);

        // Array values: mirror Equals' unified IsArray branch. The scalar arms
        // below only hash the payload words their kind uses (Float32 hashes
        // _p0), which would collide for inline arrays sharing a first element.
        if (IsArray)
        {
            // Prefer the cached content hash when present (arena arrays stamp
            // XxHash64 across _p4+_p5 at FromArenaArray time) so equal-content
            // arrays stored in different arenas hash to the same bucket — the
            // dictionary deduplicates correctly across batches.
            ulong cachedHash = (uint)_p4 | ((ulong)(uint)_p5 << 32);
            if (cachedHash != 0)
            {
                return HashCode.Combine(_kind, _flags, _charCount, cachedHash);
            }

            // Legacy path (inline arrays, pre-hash-stamping arena arrays): hash
            // the layout words. Inline arrays pack bytes across _p0–_p3 directly.
            HashCode hash = new();
            hash.Add(_kind);
            hash.Add(_flags);
            hash.Add(_charCount);
            hash.Add(_p0);
            hash.Add(_p1);
            hash.Add(_p2);
            hash.Add(_p3);
            return hash.ToHashCode();
        }

        return _kind switch
        {
            // Small integer types: hash _p0 directly.
            DataKind.UInt8 or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Boolean or DataKind.Date
            or DataKind.Type
                => HashCode.Combine(_kind, _p0),

            // 64-bit integer/temporal types: hash _p0 + _p1.
            DataKind.Int64 or DataKind.UInt64 or DataKind.Time or DataKind.Duration
                => HashCode.Combine(_kind, _p0, _p1),

            // Float types: delegate to float/double GetHashCode so -0 == 0 hashing is preserved.
            DataKind.Float16
                => HashCode.Combine(_kind, BitConverter.UInt16BitsToHalf((ushort)_p0)),
            DataKind.Float32
                => HashCode.Combine(_kind, BitConverter.Int32BitsToSingle(_p0)),
            DataKind.Float64
                => HashCode.Combine(_kind, BitConverter.Int64BitsToDouble(ReadLong())),

            // Decimal: 16 bytes inline; combine all four ints.
            DataKind.Decimal
                => HashCode.Combine(_kind, _p0, _p1, _p2, _p3),

            // 128-bit integers: same — four-int hash.
            DataKind.UInt128 or DataKind.Int128
                => HashCode.Combine(_kind, _p0, _p1, _p2, _p3),

            // Points: hash via float to mirror IEEE equality (-0 == 0).
            DataKind.Point2D
                => HashCode.Combine(_kind,
                    BitConverter.Int32BitsToSingle(_p0),
                    BitConverter.Int32BitsToSingle(_p1)),
            DataKind.Point3D
                => HashCode.Combine(_kind,
                    BitConverter.Int32BitsToSingle(_p0),
                    BitConverter.Int32BitsToSingle(_p1),
                    BitConverter.Int32BitsToSingle(_p2)),

            // Reference types:
            // RawContentHash returns XxHash64-over-UTF-8 for both inline and cached-hash
            // values, so equal-content strings across the two modes hash to the same code —
            // matching CompareStrings' mixed-mode hash match.
            DataKind.String
                => RawContentHash != 0
                    ? HashCode.Combine(_kind, RawContentHash)
                    : ComputeStringHashCode(),
            DataKind.Timestamp or DataKind.TimestampTz
                => HashCode.Combine(_kind, _p0, _p1),
            DataKind.Uuid
                => HashCode.Combine(_kind, _p0, _p1, _p2, _p3),
            // Image / Audio: use the cached 32-bit content hash in _p6 when stamped
            // (the ingest path provided the bytes); fall back to offset-based hashing
            // when absent. Cross-arena dedup requires the cached hash — offsets drift
            // per scan so without it the dictionary fragments.
            DataKind.Image or DataKind.Audio
                => _p6 != 0
                    ? HashCode.Combine(_kind, (uint)_p6)
                    : HashCode.Combine(_kind, _p0, _p1),
            // Offset-based hashing: consistent with offset-equality in Equals.
            DataKind.Video or DataKind.Json or DataKind.PointCloud or DataKind.Mesh
                => HashCode.Combine(_kind, _p0, _p1),
            // VideoFrame: inline value, hash the (videoId, frameIndex) payload.
            DataKind.VideoFrame
                => HashCode.Combine(_kind, _p0, _p1),
            DataKind.Struct
                => HashCode.Combine(_kind, _p0, _p1, _meta),
            _ => HashCode.Combine(_kind),
        };
    }

    /// <inheritdoc/>
    public static bool operator ==(DataValue left, DataValue right) => left.Equals(right);

    /// <inheritdoc/>
    public static bool operator !=(DataValue left, DataValue right) => !left.Equals(right);

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// Computes GetHashCode for a string value that has no cached hash (e.g. legacy arena-backed values).
    /// Falls back to offset/length-based hash since we have no store to resolve content.
    /// </summary>
    private int ComputeStringHashCode()
    {
        return HashCode.Combine(_kind, _p0, _p1);
    }

    /// <summary>
    /// Compares two <see cref="DataKind.Image"/> values. Same offset+length in the
    /// same store ⇒ identical content (fast path). Otherwise, when both sides
    /// carry the cached 32-bit content hash in <c>_p6</c>, compare those;
    /// mismatch ⇒ definitely unequal (records a hash short-circuit). Match ⇒
    /// treated as equal (32-bit collision rate ~1 in 4B is acceptable for a
    /// fast-negative gate). Legacy path with no cached hash returns false
    /// conservatively — same shape as the non-string reference kinds.
    /// </summary>
    private static bool CompareImage(in DataValue left, in DataValue right)
    {
        if (left._p0 == right._p0 && left._p1 == right._p1)
        {
            return true;
        }

        uint leftHash = (uint)left._p6;
        uint rightHash = (uint)right._p6;
        if (leftHash != 0 && rightHash != 0)
        {
            if (leftHash != rightHash)
            {
                DatumIngest.Diagnostics.HashGateStats.RecordHashShortCircuit();
                return false;
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Audio counterpart to <see cref="CompareImage"/>. Same shape: fast path on
    /// same offset+length, cached 32-bit content hash in <c>_p6</c> otherwise,
    /// legacy fallback returns false. Inline metadata in <c>_p4</c>/<c>_p5</c>
    /// (sampleRate/channels/bitDepth/frameCount) is content-derived, so equal
    /// content hashes imply equal metadata.
    /// </summary>
    private static bool CompareAudio(in DataValue left, in DataValue right)
    {
        if (left._p0 == right._p0 && left._p1 == right._p1)
        {
            return true;
        }

        uint leftHash = (uint)left._p6;
        uint rightHash = (uint)right._p6;
        if (leftHash != 0 && rightHash != 0)
        {
            if (leftHash != rightHash)
            {
                DatumIngest.Diagnostics.HashGateStats.RecordHashShortCircuit();
                return false;
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Compares two string values, handling the case where one or both
    /// are arena-backed (no reference in the store). Arena-backed values from the
    /// same batch share offset/length identity; cross-arena comparison requires
    /// materialisation before calling <see cref="Equals(DataValue)"/>.
    /// </summary>
    private static bool CompareStrings(in DataValue left, in DataValue right)
    {
        bool leftInline = left.IsInline;
        bool rightInline = right.IsInline;

        // Both inline: bitwise compare payload + byte length (unused bytes are zeroed at construction).
        if (leftInline && rightInline)
        {
            return left._charCount == right._charCount
                && left._p0 == right._p0 && left._p1 == right._p1
                && left._p2 == right._p2 && left._p3 == right._p3;
        }

        // Mixed inline/non-inline: compare via RawContentHash. Inline computes XxHash64 over
        // its bytes; reference-store values return their cached XxHash64. Both use the same
        // hash function over UTF-8 bytes, so equal content hashes equal. Arena-slice values
        // return 0 for RawContentHash and cannot be compared here without a store.
        if (leftInline || rightInline)
        {
            ulong leftHash = left.RawContentHash;
            ulong rightHash = right.RawContentHash;
            return leftHash != 0 && rightHash != 0 && leftHash == rightHash;
        }

        // Both non-inline: existing offset + cached-hash comparison.
        // Fast path: same offset + same byte length → identical bytes in the same store.
        if (left._p0 == right._p0 && left._p1 == right._p1)
            return true;

        bool leftHasHash = (left._p4 | left._p5) != 0;
        bool rightHasHash = (right._p4 | right._p5) != 0;

        // Both have hashes: compare directly.
        if (leftHasHash && rightHasHash)
            return left._p4 == right._p4 && left._p5 == right._p5;

        // Mixed or neither has hash: without a store we cannot resolve content, return false.
        // Callers should use the store-based Equals overloads for cross-origin comparison.
        return false;
    }
}

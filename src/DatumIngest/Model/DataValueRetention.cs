namespace DatumIngest.Model;

/// <summary>
/// Helpers for safely retaining <see cref="DataValue"/> instances beyond the lifetime
/// of their originating store. Non-inline reference-type payloads (strings, binary)
/// are copied into a caller-provided retention store; self-contained values (inline
/// strings, fixed-size scalars) pass through unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="Stabilize"/> before storing a <see cref="DataValue"/> in a field
/// whose lifetime outlasts the source store (for example, an accumulator's min/max,
/// a dictionary key, or an index reader's materialised key). Skipping stabilisation
/// is a silent correctness bug — the value appears valid until the source store is
/// pooled or disposed, then reads return garbage bytes.
/// </para>
/// <para>
/// Inline strings and fixed-size scalars are self-contained in the <see cref="DataValue"/>
/// struct, so stabilisation is a no-op for them. Only non-inline reference payloads
/// trigger a copy into the retention store.
/// </para>
/// </remarks>
public static class DataValueRetention
{
    /// <summary>
    /// Returns a <see cref="DataValue"/> safe to retain past <paramref name="sourceStore"/>'s
    /// lifetime. Reference-type payloads are copied into <paramref name="retentionStore"/>.
    /// </summary>
    /// <param name="value">The value to stabilise.</param>
    /// <param name="sourceStore">The store currently backing <paramref name="value"/>'s payload.</param>
    /// <param name="retentionStore">The store that should own the stabilised value's payload.</param>
    /// <exception cref="NotSupportedException">
    /// Thrown for kinds whose retention path hasn't been implemented yet. Add a case here
    /// when a new retention site introduces one.
    /// </exception>
    public static DataValue Stabilize(DataValue value, IValueStore sourceStore, IValueStore retentionStore)
    {
        // Null values carry no payload — nothing to copy.
        if (value.IsNull) return value;

        // Sidecar-backed values reference absolute (offset, length) coordinates in a
        // long-lived .datum-blob mmap. The payload's lifetime is the IBlobSource, not
        // the source arena, so stabilisation across arena boundaries is a no-op.
        // Reading the bytes here would also fail — Stabilize doesn't carry the
        // IBlobSource, and the destination arena's coordinate space is 32-bit while
        // the sidecar's is 64-bit.
        if (value.IsInSidecar) return value;

        // Inline arrays (IsInlineArray flag) carry their elements packed in the
        // struct's _p0-_p3 region — fully self-contained, pass through.
        if (value.IsInlineArray) return value;

        // Same-store fast path. With the one-arena-per-query model
        // (`project_one_arena_per_query.md`) most Stabilize calls have
        // source == target — the bytes are already in the right place. Apply this
        // before the inline check so an inline N=1 reference array (whose slot
        // points into sourceStore) also short-circuits when no cross-store copy
        // is needed.
        if (ReferenceEquals(sourceStore, retentionStore)) return value;

        // Reference-type arrays (Kind ∈ {String, Image, Struct} + IsArray) need
        // per-element deep copy: each slot's (offset, length) references payload
        // bytes in sourceStore. A naïve byte-block copy of the slot table alone
        // would leave slots dangling against the old store. Handle this BEFORE
        // the generic inline pass-through so inline N=1 reference arrays (whose
        // _p0–_p3 hold a slot pointing into sourceStore) get deep-copied too.
        if (value.IsArray && IsReferenceElementKind(value.Kind))
        {
            return StabilizeReferenceArray(value, sourceStore, retentionStore);
        }

        // Other inline values (scalars, inline strings, inline N=0 reference arrays)
        // are self-contained — no external bytes to copy.
        if (value.IsInline) return value;

        // Any arena-backed typed array (fixed-width Kind + IsArray) — bytes live
        // contiguously at (_p0, _p1) in sourceStore, regardless of element kind.
        // Copy bytes verbatim into retentionStore and rebuild the DataValue with
        // the same kind tag. Must come before the scalar arms below so e.g.
        // UInt8 + IsArray matches first.
        if (value.IsArray)
        {
            ReadOnlySpan<byte> bytes = value.AsArraySpan<byte>(sourceStore);
            return DataValue.FromArenaArray<byte>(bytes, value.Kind, retentionStore);
        }

        return value.Kind switch
        {
            // Fixed-size scalars: self-contained in the struct's inline payload bytes.
            DataKind.Unknown
                or DataKind.Boolean
                or DataKind.Int8 or DataKind.UInt8
                or DataKind.Int16 or DataKind.UInt16
                or DataKind.Int32 or DataKind.UInt32
                or DataKind.Int64 or DataKind.UInt64
                or DataKind.Float32 or DataKind.Float64
                or DataKind.Date or DataKind.Time or DataKind.Duration or DataKind.DateTime
                or DataKind.Uuid or DataKind.Type
                => value,

            // Reference-type payloads: copy into the retention store.
            DataKind.String => DataValue.FromUtf8Span(
                value.AsUtf8Span(sourceStore),
                value.StringCharCount(sourceStore),
                retentionStore),

            // Image is just encoded bytes now — the legacy ImageHandle-in-object-slot
            // path is gone, since image functions are lowered to fused pipelines that
            // emit raw bytes at the boundaries. So retention is the same as UInt8Array.
            DataKind.Image => DataValue.FromImage(
                value.AsImage(sourceStore),
                retentionStore),

            // Recursively stabilise each element into the retention store, then store the
            // result array there. Element kinds that themselves throw from Stabilize bubble
            // up — e.g. an array of vectors would still be unsupported.
            DataKind.Array => StabilizeArray(value, sourceStore, retentionStore),

            // Struct retention path isn't implemented yet because no current retention
            // site uses it as a key. Add a case when needed.
            _ => throw new NotSupportedException(
                $"Retention of {value.Kind} is not implemented. Add a case to " +
                "DataValueRetention.Stabilize when a retention site needs it."),
        };
    }

    private static DataValue StabilizeArray(DataValue value, IValueStore sourceStore, IValueStore retentionStore)
    {
        DataValue[] elements = value.AsArray(sourceStore);
        if (elements.Length == 0)
        {
            return DataValue.FromArray(value.ArrayElementKind, elements, retentionStore);
        }

        DataValue[] stabilized = new DataValue[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            stabilized[i] = Stabilize(elements[i], sourceStore, retentionStore);
        }

        return DataValue.FromArray(value.ArrayElementKind, stabilized, retentionStore);
    }

    /// <summary>
    /// Returns whether <paramref name="kind"/> is a reference-type element kind
    /// — i.e. one whose <c>Array&lt;Kind&gt;</c> uses the slot-block layout where
    /// each slot points to per-element payload bytes (vs. fixed-width arrays
    /// where slot bytes ARE the element bytes).
    /// </summary>
    private static bool IsReferenceElementKind(DataKind kind) =>
        kind is DataKind.String or DataKind.Image or DataKind.Struct;

    /// <summary>
    /// Deep-copies a reference-type array (Array&lt;String&gt;, Array&lt;Image&gt;,
    /// Array&lt;Struct&gt;) from <paramref name="sourceStore"/> into
    /// <paramref name="retentionStore"/>. Walks each element via the kind-typed
    /// accessor, then re-builds the array via the kind-typed factory targeting
    /// the retention store. Cost is linear in total payload bytes — see
    /// <c>project_reference_type_arrays.md</c> for the architectural rationale.
    /// </summary>
    private static DataValue StabilizeReferenceArray(
        DataValue value,
        IValueStore sourceStore,
        IValueStore retentionStore)
    {
        return value.Kind switch
        {
            DataKind.String => DataValue.FromStringArray(value.AsStringArray(sourceStore), retentionStore),
            DataKind.Image => DataValue.FromImageArray(value.AsImageArray(sourceStore), retentionStore),
            DataKind.Struct => StabilizeStructArray(value, sourceStore, retentionStore),
            _ => throw new NotSupportedException(
                $"Stabilize does not yet handle Array<{value.Kind}>. Add a case " +
                "to DataValueRetention.StabilizeReferenceArray when needed."),
        };
    }

    /// <summary>
    /// Deep-copies an Array&lt;Struct&gt;: each element's field <see cref="DataValue"/>[]
    /// is itself recursively stabilised (since fields can be reference-type) before
    /// the result array is rebuilt against <paramref name="retentionStore"/>.
    /// </summary>
    private static DataValue StabilizeStructArray(
        DataValue value,
        IValueStore sourceStore,
        IValueStore retentionStore)
    {
        DataValue[][] elements = value.AsStructArray(sourceStore);
        DataValue[][] stabilizedElements = new DataValue[elements.Length][];
        for (int i = 0; i < elements.Length; i++)
        {
            DataValue[] sourceFields = elements[i];
            DataValue[] retentionFields = new DataValue[sourceFields.Length];
            for (int j = 0; j < sourceFields.Length; j++)
            {
                retentionFields[j] = Stabilize(sourceFields[j], sourceStore, retentionStore);
            }
            stabilizedElements[i] = retentionFields;
        }
        return DataValue.FromStructArray(stabilizedElements, retentionStore);
    }
}

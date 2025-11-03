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
        // Null and inline values carry no external payload — nothing to copy.
        if (value.IsNull) return value;
        if (value.IsInline) return value;

        // Sidecar-backed values reference absolute (offset, length) coordinates in a
        // long-lived .datum-blob mmap. The payload's lifetime is the IBlobSource, not
        // the source arena, so stabilisation across arena boundaries is a no-op.
        // Reading the bytes here would also fail — Stabilize doesn't carry the
        // IBlobSource, and the destination arena's coordinate space is 32-bit while
        // the sidecar's is 64-bit.
        if (value.IsInSidecar) return value;

        // Inline arrays carry their elements in the struct's _p0-_p3 payload region.
        // The bytes follow the value through any copy — no external store backs them,
        // so stabilisation across arena boundaries is a pass-through.
        if (value.IsInlineArray) return value;

        return value.Kind switch
        {
            // Byte array via the new IsArray flag — copy bytes into retention store.
            // Must come before the scalar-UInt8 arm so it matches first; otherwise
            // the scalar arm would return the value unchanged (incorrect for an
            // arena-backed byte array). Parallel to the legacy UInt8Array case
            // below; PR3 removes that case.
            DataKind.UInt8 when value.IsArray => DataValue.FromByteArray(
                value.AsUInt8Array(sourceStore),
                retentionStore),

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

            DataKind.JsonValue => DataValue.FromJsonValue(
                value.AsJsonValue(sourceStore),
                retentionStore),

            DataKind.UInt8Array => DataValue.FromByteArray(
                value.AsUInt8Array(sourceStore),
                retentionStore),

            DataKind.Image => DataValue.FromImage(
                value.AsImage(sourceStore),
                retentionStore),

            // Vector / Matrix / Tensor / Array / Struct retention paths aren't implemented
            // yet because no current retention site uses them as keys. Add a case when needed.
            _ => throw new NotSupportedException(
                $"Retention of {value.Kind} is not implemented. Add a case to " +
                "DataValueRetention.Stabilize when a retention site needs it."),
        };
    }
}

namespace DatumIngest.Model;

/// <summary>
/// Single canonical comparison implementation for <see cref="DataValue"/> pairs.
/// </summary>
/// <remarks>
/// <para>
/// Nulls are <strong>not</strong> handled here — callers are responsible for checking
/// <see cref="DataValue.IsNull"/> before calling. This keeps null-ordering semantics
/// (nulls-last vs. nulls-first) as an explicit caller decision.
/// </para>
/// <para>
/// Returns <c>0</c> for kinds that have no ordinal semantics such as
/// <see cref="DataKind.Array"/>, <see cref="DataKind.Vector"/>, and
/// <see cref="DataKind.Image"/>. In practice those kinds are blocked by the type
/// resolver before reaching comparison contexts.
/// </para>
/// </remarks>
internal static class DataValueComparer
{
    /// <summary>
    /// Compares two non-null values of the same kind.
    /// When <paramref name="arena"/> is supplied, arena-backed strings are compared
    /// via their raw UTF-8 byte spans for maximum throughput.
    /// When the two values have <em>different</em> kinds (cross-kind numeric comparison,
    /// e.g. an <c>INT32</c> column against a <c>FLOAT32</c> literal), both values
    /// are widened to <see cref="double"/> before comparison.
    /// </summary>
    internal static int Compare(DataValue left, DataValue right, StringArena? arena = null)
    {
        // Cross-kind: widen both to double. This mirrors the ToFloat-based fallback in the
        // original per-class implementations and handles cases like INT column vs FLOAT literal.
        if (left.Kind != right.Kind)
        {
            return ToDouble(left).CompareTo(ToDouble(right));
        }

        return left.Kind switch
        {
            DataKind.Float32  => left.AsFloat32().CompareTo(right.AsFloat32()),
            DataKind.Float64  => left.AsFloat64().CompareTo(right.AsFloat64()),
            DataKind.UInt8    => left.AsUInt8().CompareTo(right.AsUInt8()),
            DataKind.Int8     => left.AsInt8().CompareTo(right.AsInt8()),
            DataKind.Int16    => left.AsInt16().CompareTo(right.AsInt16()),
            DataKind.UInt16   => left.AsUInt16().CompareTo(right.AsUInt16()),
            DataKind.Int32    => left.AsInt32().CompareTo(right.AsInt32()),
            DataKind.UInt32   => left.AsUInt32().CompareTo(right.AsUInt32()),
            DataKind.Int64    => left.AsInt64().CompareTo(right.AsInt64()),
            DataKind.UInt64   => left.AsUInt64().CompareTo(right.AsUInt64()),
            DataKind.Boolean  => left.AsBoolean().CompareTo(right.AsBoolean()),
            DataKind.Date     => left.AsDate().CompareTo(right.AsDate()),
            DataKind.DateTime => left.AsDateTime().CompareTo(right.AsDateTime()),
            DataKind.Time     => left.AsTime().CompareTo(right.AsTime()),
            DataKind.Duration => left.AsDuration().CompareTo(right.AsDuration()),
            DataKind.Uuid     => left.AsUuid().CompareTo(right.AsUuid()),
            DataKind.String   => arena is not null
                ? CompareStrings(left, right, arena)
                : string.Compare(left.AsString(), right.AsString(), StringComparison.Ordinal),
            _ => 0,
        };
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="kind"/> supports natural
    /// ordering via <see cref="Compare"/>. This includes all numeric scalars, strings,
    /// date/time types, duration, uuid, and boolean.
    /// </summary>
    internal static bool IsComparable(DataKind kind) =>
        kind is DataKind.Float32 or DataKind.Float64
            or DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64
            or DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64
            or DataKind.Boolean
            or DataKind.String
            or DataKind.Date or DataKind.DateTime or DataKind.Time
            or DataKind.Duration or DataKind.Uuid;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="kind"/> is any integer or
    /// floating-point scalar kind that can be losslessly widened to <see cref="float"/>
    /// or <see cref="double"/>.
    /// </summary>
    internal static bool IsNumericScalar(DataKind kind) =>
        kind is DataKind.Float32 or DataKind.Float64
            or DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64
            or DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64
            or DataKind.Boolean;

    /// <summary>Delegates to <see cref="DataValue.ToFloat"/>.</summary>
    internal static float ToFloat(DataValue value) => value.ToFloat();

    private static double ToDouble(DataValue value) =>
        value.TryToDouble(out double d) ? d : 0.0;

    private static int CompareStrings(DataValue left, DataValue right, StringArena arena)
    {
        if (left.IsArenaBacked && right.IsArenaBacked)
        {
            return left.GetArenaStringSpan(arena).SequenceCompareTo(right.GetArenaStringSpan(arena));
        }

        string leftStr = left.IsArenaBacked ? left.AsString(arena) : left.AsString();
        string rightStr = right.IsArenaBacked ? right.AsString(arena) : right.AsString();
        return string.Compare(leftStr, rightStr, StringComparison.Ordinal);
    }
}

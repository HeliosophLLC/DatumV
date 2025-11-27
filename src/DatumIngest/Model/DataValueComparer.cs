using System.Globalization;

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
/// <see cref="DataKind.Image"/>, <see cref="DataKind.Struct"/>, and any
/// typed-array (Kind + IsArray) value. In practice those kinds are blocked
/// by the type resolver before reaching comparison contexts.
/// </para>
/// </remarks>
internal static class DataValueComparer
{
    /// <summary>
    /// Compares two non-null values without providing an <see cref="Arena"/>. Safe for
    /// scalar kinds, null values, and inline strings (which are self-contained in the
    /// <see cref="DataValue"/> struct). Throws for non-inline <see cref="DataKind.String"/>
    /// values — those require arenas to resolve payload
    /// bytes; use <see cref="Compare(DataValue, IValueStore, DataValue, IValueStore)"/> instead.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when either side is a non-inline String. The caller must
    /// supply arenas via the two-arena overload to compare those content-correctly.
    /// </exception>
    internal static int Compare(DataValue left, DataValue right)
    {
        bool leftNeedsArena = NeedsArenaForCompare(left);
        bool rightNeedsArena = NeedsArenaForCompare(right);
        if (leftNeedsArena || rightNeedsArena)
        {
            throw new InvalidOperationException(
                "Comparing non-inline String requires arenas. " +
                "Use DataValueComparer.Compare(left, leftArena, right, rightArena) instead.");
        }

        // Safe: arena is only read inside Compare for String reference-store kinds,
        // and we've just proven both sides are either inline, null, or scalar.
        return Compare(left, null!, right, null!);
    }

    private static bool NeedsArenaForCompare(DataValue value) =>
        value.Kind == DataKind.String
        && !value.IsNull
        && !value.IsInline;

    /// <summary>
    /// Compares two non-null values of the same kind.
    /// When the two values have <em>different</em> kinds (cross-kind numeric comparison,
    /// e.g. an <c>INT32</c> column against a <c>FLOAT32</c> literal), both values
    /// are widened to <see cref="double"/> before comparison.
    /// </summary>
    internal static int Compare(DataValue left, DataValue right, IValueStore arena)
        => Compare(left, arena, right, arena);

    /// <summary>
    /// Compares two non-null values of the same kind.
    /// When the two values have <em>different</em> kinds (cross-kind numeric comparison,
    /// e.g. an <c>INT32</c> column against a <c>FLOAT32</c> literal), both values
    /// are widened to <see cref="double"/> before comparison.
    /// </summary>
    internal static int Compare(DataValue left, IValueStore leftArena, DataValue right, IValueStore rightArena)
    {
        // Cross-kind: widen both to double. This mirrors the ToFloat-based fallback in the
        // original per-class implementations and handles cases like INT column vs FLOAT literal.
        if (left.Kind != right.Kind)
        {
            return ToDouble(left).CompareTo(ToDouble(right));
        }

        return left.Kind switch
        {
            DataKind.Float16  => left.AsFloat16().CompareTo(right.AsFloat16()),
            DataKind.Float32  => left.AsFloat32().CompareTo(right.AsFloat32()),
            DataKind.Float64  => left.AsFloat64().CompareTo(right.AsFloat64()),
            DataKind.Decimal  => left.AsDecimal().CompareTo(right.AsDecimal()),
            DataKind.UInt8    => left.AsUInt8().CompareTo(right.AsUInt8()),
            DataKind.Int8     => left.AsInt8().CompareTo(right.AsInt8()),
            DataKind.Int16    => left.AsInt16().CompareTo(right.AsInt16()),
            DataKind.UInt16   => left.AsUInt16().CompareTo(right.AsUInt16()),
            DataKind.Int32    => left.AsInt32().CompareTo(right.AsInt32()),
            DataKind.UInt32   => left.AsUInt32().CompareTo(right.AsUInt32()),
            DataKind.Int64    => left.AsInt64().CompareTo(right.AsInt64()),
            DataKind.UInt64   => left.AsUInt64().CompareTo(right.AsUInt64()),
            DataKind.Int128   => left.AsInt128().CompareTo(right.AsInt128()),
            DataKind.UInt128  => left.AsUInt128().CompareTo(right.AsUInt128()),
            DataKind.Boolean  => left.AsBoolean().CompareTo(right.AsBoolean()),
            DataKind.Date     => left.AsDate().CompareTo(right.AsDate()),
            DataKind.DateTime => left.AsDateTime().CompareTo(right.AsDateTime()),
            DataKind.Time     => left.AsTime().CompareTo(right.AsTime()),
            DataKind.Duration => left.AsDuration().CompareTo(right.AsDuration()),
            DataKind.Uuid     => left.AsUuid().CompareTo(right.AsUuid()),
            DataKind.Type     => ((byte)left.AsType()).CompareTo((byte)right.AsType()),
            DataKind.String   => left.AsUtf8Span(leftArena).SequenceCompareTo(right.AsUtf8Span(rightArena)),
            _ => 0,
        };
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="kind"/> supports natural
    /// ordering via <see cref="Compare(DataValue, IValueStore, DataValue, IValueStore)"/>. This includes all numeric scalars, strings,
    /// date/time types, duration, uuid, and boolean.
    /// </summary>
    internal static bool IsComparable(DataKind kind) =>
        kind is DataKind.Float16 or DataKind.Float32 or DataKind.Float64
            or DataKind.Decimal
            or DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64 or DataKind.Int128
            or DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64 or DataKind.UInt128
            or DataKind.Boolean
            or DataKind.String
            or DataKind.Date or DataKind.DateTime or DataKind.Time
            or DataKind.Duration or DataKind.Uuid;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="kind"/> is any integer or
    /// floating-point scalar kind that can be widened to <see cref="float"/>
    /// or <see cref="double"/>. <see cref="DataKind.Decimal"/> is included even
    /// though widening to <see cref="double"/> may lose precision — comparison
    /// callers stay in same-kind decimal arithmetic via the
    /// <see cref="Compare(DataValue, IValueStore, DataValue, IValueStore)"/>
    /// switch; the cross-kind double fallback is the same compromise applied to
    /// <see cref="DataKind.Int64"/>/<see cref="DataKind.UInt64"/>.
    /// </summary>
    internal static bool IsNumericScalar(DataKind kind) =>
        kind is DataKind.Float16 or DataKind.Float32 or DataKind.Float64
            or DataKind.Decimal
            or DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64 or DataKind.Int128
            or DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64 or DataKind.UInt128
            or DataKind.Boolean;

    // ───────────────────── Cast compatibility ─────────────────────

    /// <summary>
    /// Returns whether a specific value can be represented in the target numeric
    /// type without overflow or truncation of the integer part.
    /// The value is tested as a <see cref="double"/> intermediate (matching CAST's
    /// internal conversion path). Integer targets require a whole number within range;
    /// float targets require a finite, representable value.
    /// </summary>
    /// <summary>
    /// Returns whether a specific value can be cast to the target numeric type
    /// without overflow. Truncation of fractional parts is allowed (matching CAST
    /// semantics) — only out-of-range values return <c>false</c>.
    /// </summary>
    internal static bool CanFitNumeric(double value, DataKind targetKind)
    {
        if (double.IsNaN(value)) return false;

        return targetKind switch
        {
            DataKind.UInt8   => value >= 0 && value <= 255,
            DataKind.Int8    => value >= sbyte.MinValue && value <= sbyte.MaxValue,
            DataKind.Int16   => value >= short.MinValue && value <= short.MaxValue,
            DataKind.UInt16  => value >= 0 && value <= ushort.MaxValue,
            DataKind.Int32   => value >= int.MinValue && value <= int.MaxValue,
            DataKind.UInt32  => value >= 0 && value <= uint.MaxValue,
            DataKind.Int64   => value >= long.MinValue && value <= long.MaxValue,
            DataKind.UInt64  => value >= 0 && value <= ulong.MaxValue,
            // Int128/UInt128 always cover the entire double range — Int128.MaxValue ≈ 1.7e38,
            // UInt128.MaxValue ≈ 3.4e38, both well past double's exponent ceiling. The double
            // intermediate is only narrowing, never overflowing, so any finite double fits.
            DataKind.Int128  => true,
            DataKind.UInt128 => value >= 0,
            DataKind.Float16 => value == 0 || (Math.Abs(value) >= (double)Half.Epsilon && Math.Abs(value) <= 65504.0),
            DataKind.Float32 => value == 0 || (System.Math.Abs(value) >= float.MinValue && System.Math.Abs(value) <= float.MaxValue),
            DataKind.Float64 => true, // Already a double — always fits.
            DataKind.Decimal => value >= (double)decimal.MinValue && value <= (double)decimal.MaxValue,
            _ => false,
        };
    }

    /// <summary>
    /// Creates a <see cref="DataValue"/> of the given numeric <paramref name="targetKind"/>
    /// from a <see cref="double"/> intermediate. Returns <c>null</c> if
    /// <paramref name="targetKind"/> is not a numeric kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the counterpart to <see cref="CanFitNumeric"/>: one checks whether a
    /// value fits, this one performs the conversion. Both use the same double-intermediate
    /// path so their range semantics stay in sync.
    /// </para>
    /// <para>
    /// Integer types use truncating casts (fractional parts are discarded).
    /// UInt8 saturates at [0, 255] via <see cref="System.Math.Clamp(double, double, double)"/>.
    /// All other integer types wrap on overflow (C# unchecked behavior).
    /// </para>
    /// </remarks>
    internal static DataValue? MakeNumeric(double value, DataKind targetKind)
    {
        return targetKind switch
        {
            DataKind.UInt8   => DataValue.FromUInt8((byte)System.Math.Clamp(value, 0.0, 255.0)),
            DataKind.Int8    => DataValue.FromInt8((sbyte)value),
            DataKind.Int16   => DataValue.FromInt16((short)value),
            DataKind.UInt16  => DataValue.FromUInt16((ushort)value),
            DataKind.Int32   => DataValue.FromInt32((int)value),
            DataKind.UInt32  => DataValue.FromUInt32((uint)value),
            DataKind.Int64   => DataValue.FromInt64((long)value),
            DataKind.UInt64  => DataValue.FromUInt64((ulong)value),
            DataKind.Int128  => DataValue.FromInt128((Int128)value),
            DataKind.UInt128 => DataValue.FromUInt128((UInt128)value),
            DataKind.Float16 => DataValue.FromFloat16((Half)value),
            DataKind.Float32 => DataValue.FromFloat32((float)value),
            DataKind.Float64 => DataValue.FromFloat64(value),
            DataKind.Decimal => DataValue.FromDecimal((decimal)value),
            _ => null,
        };
    }

    /// <summary>
    /// Converts a managed-boxed primitive back into a <see cref="DataValue"/> of the given kind.
    /// Used for zone-map values which are stored as boxed managed primitives rather than
    /// <see cref="DataValue"/> to avoid arena dependencies on long-lived metadata.
    /// </summary>
    /// <param name="kind">The target <see cref="DataKind"/>.</param>
    /// <param name="boxed">The managed-boxed primitive value (e.g. <see cref="long"/>, <see cref="string"/>, <see cref="DateOnly"/>), or <c>null</c>.</param>
    /// <param name="store">Value store for arena-backed kinds (String).</param>
    /// <returns>The materialized <see cref="DataValue"/>, or <c>null</c> if <paramref name="boxed"/> is null or the kind is unsupported.</returns>
    internal static DataValue? MakeFromBoxed(DataKind kind, object? boxed, IValueStore store)
    {
        if (boxed is null) return null;

        return kind switch
        {
            DataKind.Boolean  => DataValue.FromUInt8((bool)boxed ? (byte)1 : (byte)0),
            DataKind.UInt8    => DataValue.FromUInt8((byte)boxed),
            DataKind.Int8     => DataValue.FromInt8((sbyte)boxed),
            DataKind.Int16    => DataValue.FromInt16((short)boxed),
            DataKind.UInt16   => DataValue.FromUInt16((ushort)boxed),
            DataKind.Int32    => DataValue.FromInt32((int)boxed),
            DataKind.UInt32   => DataValue.FromUInt32((uint)boxed),
            DataKind.Int64    => DataValue.FromInt64((long)boxed),
            DataKind.UInt64   => DataValue.FromUInt64((ulong)boxed),
            DataKind.Int128   => DataValue.FromInt128((Int128)boxed),
            DataKind.UInt128  => DataValue.FromUInt128((UInt128)boxed),
            DataKind.Float16  => DataValue.FromFloat16((Half)boxed),
            DataKind.Float32  => DataValue.FromFloat32((float)boxed),
            DataKind.Float64  => DataValue.FromFloat64((double)boxed),
            DataKind.Decimal  => DataValue.FromDecimal((decimal)boxed),
            DataKind.Date     => DataValue.FromDate((DateOnly)boxed),
            DataKind.DateTime => DataValue.FromDateTime((DateTimeOffset)boxed),
            DataKind.Time     => DataValue.FromTime((TimeOnly)boxed),
            DataKind.Duration => DataValue.FromDuration((TimeSpan)boxed),
            DataKind.Uuid     => DataValue.FromUuid((Guid)boxed),
            DataKind.String   => store is not null
                ? DataValue.FromString((string)boxed, store)
                : throw new ArgumentNullException(nameof(store), "String kind requires a value store."),
            _ => null,
        };
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="text"/> is a recognised
    /// boolean literal: case-insensitive <c>"true"</c> / <c>"false"</c>, or single-character
    /// <c>"0"</c> / <c>"1"</c>. Span-based and zero-allocation; callers on hot paths
    /// (e.g. CSV inference) avoid the <see cref="string"/>-allocating overload of
    /// <see cref="CanParseString"/>.
    /// </summary>
    internal static bool IsBooleanLiteral(ReadOnlySpan<char> text)
    {
        if (text.Length == 1) return text[0] == '0' || text[0] == '1';
        return text.Equals("true", StringComparison.OrdinalIgnoreCase)
            || text.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns whether a string can be parsed as the target type without failure.
    /// Uses culture-invariant TryParse for each supported target kind.
    /// </summary>
    internal static bool CanParseString(string value, DataKind targetKind)
    {
        return targetKind switch
        {
            DataKind.UInt8    => byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            DataKind.Int8     => sbyte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            DataKind.Int16    => short.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            DataKind.UInt16   => ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            DataKind.Int32    => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            DataKind.UInt32   => uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            DataKind.Int64    => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            DataKind.UInt64   => ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            DataKind.Int128   => Int128.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            DataKind.UInt128  => UInt128.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            DataKind.Float16  => Half.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _),
            DataKind.Float32  => float.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _),
            DataKind.Float64  => double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _),
            DataKind.Decimal  => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
            DataKind.Date     => DateOnly.TryParse(value, CultureInfo.InvariantCulture, out _),
            DataKind.DateTime => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            DataKind.Time     => TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out _),
            DataKind.Duration => TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out _),
            DataKind.Uuid     => Guid.TryParse(value, out _),
            DataKind.Boolean  => IsBooleanLiteral(value.AsSpan()),
            DataKind.String   => true,
            _ => false,
        };
    }

    /// <summary>
    /// Returns whether a semantic (non-numeric, non-string-parse) conversion path
    /// exists from <paramref name="from"/> to <paramref name="to"/>. These are
    /// structural conversions supported by CAST that don't go through the numeric
    /// widening chain or string parsing — e.g. Date↔DateTime, Uuid↔String,
    /// byte reinterpretation, and temporal↔numeric epoch conversions.
    /// </summary>
    internal static bool HasSemanticCastPath(DataKind from, DataKind to)
    {
        return (from, to) switch
        {
            // Date/DateTime interconversion
            (DataKind.Date, DataKind.DateTime) => true,
            (DataKind.DateTime, DataKind.Date) => true,

            // Temporal → String (formatting)
            (DataKind.Date, DataKind.String) => true,
            (DataKind.DateTime, DataKind.String) => true,
            (DataKind.Time, DataKind.String) => true,
            (DataKind.Duration, DataKind.String) => true,
            (DataKind.Uuid, DataKind.String) => true,
            (DataKind.Boolean, DataKind.String) => true,

            // Date/DateTime → numeric (epoch conversion)
            (DataKind.Date, DataKind.Float32 or DataKind.Float64 or DataKind.Int32 or DataKind.Int64) => true,
            (DataKind.DateTime, DataKind.Float32 or DataKind.Float64 or DataKind.Int64) => true,

            // DateTime → Time (extract time component)
            (DataKind.DateTime, DataKind.Time) => true,

            // Time ↔ numeric (seconds since midnight)
            (DataKind.Time, DataKind.Float32 or DataKind.Float64) => true,
            (DataKind.Float32 or DataKind.Float64, DataKind.Time) => true,

            // Duration ↔ numeric (total seconds)
            (DataKind.Duration, DataKind.Float32 or DataKind.Float64) => true,
            (DataKind.Float32 or DataKind.Float64, DataKind.Duration) => true,

            // Byte-array ↔ Image cast was supported in the old model but the
            // (DataKind, DataKind) signature can't express "UInt8 + IsArray".
            // Add it back — keyed on (DataValue, DataKind) — when a demo needs
            // it. The rebuilt cast function deliberately doesn't ship byte↔Image.

            _ => false,
        };
    }

    // ───────────────────── CLR type mapping ─────────────────────

    /// <summary>
    /// Maps a CLR <see cref="Type"/> to the engine's <see cref="DataKind"/>.
    /// Unwraps <see cref="Nullable{T}"/> automatically. Falls back to
    /// <see cref="DataKind.String"/> for unrecognised types.
    /// </summary>
    internal static DataKind MapClrType(Type clrType)
    {
        Type t = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (t == typeof(Half)) return DataKind.Float16;
        if (t == typeof(float)) return DataKind.Float32;
        if (t == typeof(double)) return DataKind.Float64;
        if (t == typeof(decimal)) return DataKind.Decimal;
        if (t == typeof(int)) return DataKind.Int32;
        if (t == typeof(long)) return DataKind.Int64;
        if (t == typeof(Int128)) return DataKind.Int128;
        if (t == typeof(short)) return DataKind.Int16;
        if (t == typeof(ushort)) return DataKind.UInt16;
        if (t == typeof(uint)) return DataKind.UInt32;
        if (t == typeof(ulong)) return DataKind.UInt64;
        if (t == typeof(UInt128)) return DataKind.UInt128;
        if (t == typeof(sbyte)) return DataKind.Int8;
        if (t == typeof(byte)) return DataKind.UInt8;
        if (t == typeof(string)) return DataKind.String;
        // byte[] → UInt8; callers that need the array shape must set IsArray
        // on the descriptor / DataValue separately.
        if (t == typeof(byte[])) return DataKind.UInt8;
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return DataKind.DateTime;
        if (t == typeof(DateOnly)) return DataKind.Date;
        if (t == typeof(bool)) return DataKind.Boolean;

        return DataKind.String;
    }

    // ───────────────────── Widening helpers ─────────────────────

    /// <summary>Delegates to <see cref="DataValue.ToFloat"/>.</summary>
    internal static float ToFloat(DataValue value) => value.ToFloat();

    private static double ToDouble(DataValue value) =>
        value.TryToDouble(out double d) ? d : 0.0;
}

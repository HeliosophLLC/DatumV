using System.Numerics;
using System.Runtime.CompilerServices;

namespace Heliosoph.DatumV.Model;

public readonly partial struct DataValue
{
    // ───────────────────────── Factory methods ─────────────────────────

    /// <summary>Creates a value from a 32-bit floating-point number.</summary>
    public static DataValue FromFloat32(float value)
    {
        if (value == 0f) return Float32Zero;
        if (value == 1f) return Float32One;
        return new(DataKind.Float32, flags: 0, p0: BitConverter.SingleToInt32Bits(value));
    }

    /// <summary>Creates a value from an unsigned 8-bit integer.</summary>
    public static DataValue FromUInt8(byte value) =>
        new(DataKind.UInt8, flags: 0, p0: value);

    /// <summary>Creates a value from a signed 8-bit integer.</summary>
    public static DataValue FromInt8(sbyte value) =>
        new(DataKind.Int8, flags: 0, p0: value);

    /// <summary>Creates a value from a signed 16-bit integer.</summary>
    public static DataValue FromInt16(short value) =>
        new(DataKind.Int16, flags: 0, p0: value);

    /// <summary>Creates a value from an unsigned 16-bit integer.</summary>
    public static DataValue FromUInt16(ushort value) =>
        new(DataKind.UInt16, flags: 0, p0: value);

    /// <summary>Creates a value from a signed 32-bit integer.</summary>
    public static DataValue FromInt32(int value) =>
        new(DataKind.Int32, flags: 0, p0: value);

    /// <summary>Creates a value from an unsigned 32-bit integer.</summary>
    public static DataValue FromUInt32(uint value) =>
        new(DataKind.UInt32, flags: 0, p0: unchecked((int)value));

    /// <summary>Creates a value from a signed 64-bit integer.</summary>
    public static DataValue FromInt64(long value) =>
        new(DataKind.Int64, flags: 0, p0: (int)value, p1: (int)(value >> 32));

    /// <summary>Creates a value from an unsigned 64-bit integer.</summary>
    public static DataValue FromUInt64(ulong value) =>
        new(DataKind.UInt64, flags: 0, p0: (int)value, p1: (int)(value >> 32));

    /// <summary>Creates a value from a 64-bit double-precision floating-point number.</summary>
    public static DataValue FromFloat64(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        return new(DataKind.Float64, flags: 0, p0: (int)bits, p1: (int)(bits >> 32));
    }

    /// <summary>Creates a value from a 16-bit IEEE 754 binary16 floating-point number.</summary>
    public static DataValue FromFloat16(Half value)
    {
        ushort bits = BitConverter.HalfToUInt16Bits(value);
        return new(DataKind.Float16, flags: 0, p0: bits);
    }

    /// <summary>Creates a value from a 128-bit decimal floating-point number.</summary>
    public static DataValue FromDecimal(decimal value)
    {
        // System.Decimal is 16 bytes — fits exactly inline in DataValue's _p0–_p3.
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        return new(DataKind.Decimal, flags: 0,
            p0: bits[0], p1: bits[1], p2: bits[2], p3: bits[3]);
    }

    /// <summary>Creates a value from an unsigned 128-bit integer.</summary>
    public static DataValue FromUInt128(UInt128 value)
    {
        // UInt128 is 16 bytes — reinterpret as four int32 words and store inline.
        ref int words = ref Unsafe.As<UInt128, int>(ref value);
        return new(DataKind.UInt128, flags: 0,
            p0: words,
            p1: Unsafe.Add(ref words, 1),
            p2: Unsafe.Add(ref words, 2),
            p3: Unsafe.Add(ref words, 3));
    }

    /// <summary>Creates a value from a signed 128-bit integer.</summary>
    public static DataValue FromInt128(Int128 value)
    {
        ref int words = ref Unsafe.As<Int128, int>(ref value);
        return new(DataKind.Int128, flags: 0,
            p0: words,
            p1: Unsafe.Add(ref words, 1),
            p2: Unsafe.Add(ref words, 2),
            p3: Unsafe.Add(ref words, 3));
    }

    /// <summary>Creates a 2D point value from X / Y single-precision components.</summary>
    public static DataValue FromPoint2D(float x, float y) =>
        new(DataKind.Point2D, flags: 0,
            p0: BitConverter.SingleToInt32Bits(x),
            p1: BitConverter.SingleToInt32Bits(y));

    /// <summary>Creates a 2D point value from a <see cref="Vector2"/>.</summary>
    public static DataValue FromPoint2D(Vector2 value) =>
        FromPoint2D(value.X, value.Y);

    /// <summary>Creates a 3D point value from X / Y / Z single-precision components.</summary>
    public static DataValue FromPoint3D(float x, float y, float z) =>
        new(DataKind.Point3D, flags: 0,
            p0: BitConverter.SingleToInt32Bits(x),
            p1: BitConverter.SingleToInt32Bits(y),
            p2: BitConverter.SingleToInt32Bits(z));

    /// <summary>Creates a 3D point value from a <see cref="Vector3"/>.</summary>
    public static DataValue FromPoint3D(Vector3 value) =>
        FromPoint3D(value.X, value.Y, value.Z);

    /// <summary>
    /// Creates a 32-bit RGBA colour value. Bytes pack into <c>_p0</c> as
    /// <c>r | (g &lt;&lt; 8) | (b &lt;&lt; 16) | (a &lt;&lt; 24)</c>. Entirely
    /// inline — no arena or sidecar backing.
    /// </summary>
    public static DataValue FromColor(byte r, byte g, byte b, byte a = 255) =>
        new(DataKind.Color, flags: 0,
            p0: unchecked((int)((uint)r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24))));

    /// <summary>
    /// Reads an RGBA colour value as <c>(r, g, b, a)</c> bytes. Asserts
    /// <see cref="Kind"/> is <see cref="DataKind.Color"/>.
    /// </summary>
    public (byte R, byte G, byte B, byte A) AsColor()
    {
        if (_kind != DataKind.Color)
        {
            throw new InvalidOperationException(
                $"AsColor called on a {_kind} value (expected Color).");
        }
        uint packed = unchecked((uint)_p0);
        return (
            (byte)(packed & 0xFF),
            (byte)((packed >> 8) & 0xFF),
            (byte)((packed >> 16) & 0xFF),
            (byte)((packed >> 24) & 0xFF));
    }

    /// <summary>
    /// Creates a runtime-only <see cref="DataKind.VideoFrame"/> handle pointing at frame
    /// <paramref name="frameIndex"/> of the video registered under
    /// <paramref name="videoId"/> in the per-query video registry. The inline payload is
    /// <c>(videoId, frameIndex)</c> at <c>(_p0, _p1)</c>; no arena bytes are written.
    /// Materialization is deferred until a consumer routes the handle through the registry.
    /// </summary>
    /// <param name="videoId">
    /// Id assigned by the per-query video registry when the source video was registered.
    /// Treated as an opaque token by everything outside the registry.
    /// </param>
    /// <param name="frameIndex">
    /// Zero-based index of the target frame within the registered video. Negative values
    /// are reserved for relative-from-end semantics (e.g. <c>-1</c> = last frame) and
    /// honoured by the registry's <c>Materialize</c> path.
    /// </param>
    public static DataValue FromVideoFrame(uint videoId, int frameIndex) =>
        new(DataKind.VideoFrame, flags: 0,
            p0: unchecked((int)videoId),
            p1: frameIndex);


    /// <summary>Creates a value from a calendar date.</summary>
    public static DataValue FromDate(DateOnly value) =>
        new(DataKind.Date, flags: 0, p0: value.DayNumber);

    /// <summary>
    /// Creates a <see cref="DataKind.TimestampTz"/> value (PG <c>timestamptz</c>).
    /// The input offset is normalised to UTC at construction and discarded;
    /// two values for the same instant compare and hash equal regardless of
    /// the input offset.
    /// </summary>
    public static DataValue FromTimestampTz(DateTimeOffset value)
    {
        long utcTicks = value.UtcTicks;
        return new(DataKind.TimestampTz, flags: 0,
            p0: (int)utcTicks, p1: (int)(utcTicks >> 32));
    }

    /// <summary>
    /// Creates a <see cref="DataKind.Timestamp"/> value (PG <c>timestamp</c>):
    /// naive wall-clock ticks with no time-zone information. The input's
    /// <see cref="DateTimeKind"/> is ignored — only <see cref="DateTime.Ticks"/>
    /// is stored.
    /// </summary>
    public static DataValue FromTimestamp(DateTime value) =>
        new(DataKind.Timestamp, flags: 0,
            p0: (int)value.Ticks, p1: (int)(value.Ticks >> 32));

    /// <summary>Creates a value from a 128-bit universally unique identifier.</summary>
    public static DataValue FromUuid(Guid value)
    {
        ref int words = ref Unsafe.As<Guid, int>(ref value);
        return new(DataKind.Uuid, flags: 0,
            p0: words,
            p1: Unsafe.Add(ref words, 1),
            p2: Unsafe.Add(ref words, 2),
            p3: Unsafe.Add(ref words, 3));
    }

    /// <summary>Creates a boolean value.</summary>
    public static DataValue FromBoolean(bool value) =>
        value ? BooleanTrue : BooleanFalse;

    /// <summary>Creates a value from a time-of-day.</summary>
    public static DataValue FromTime(TimeOnly value)
    {
        long ticks = value.Ticks;
        return new(DataKind.Time, flags: 0, p0: (int)ticks, p1: (int)(ticks >> 32));
    }

    /// <summary>Creates a value from a duration (elapsed time span).</summary>
    public static DataValue FromDuration(TimeSpan value)
    {
        long ticks = value.Ticks;
        return new(DataKind.Duration, flags: 0, p0: (int)ticks, p1: (int)(ticks >> 32));
    }

    /// <summary>
    /// Creates a value from a Postgres-compatible <see cref="Interval"/>:
    /// months at <c>_p0</c>, days at <c>_p1</c>, microseconds packed across
    /// <c>_p2</c>+<c>_p3</c>. Total 16 bytes inline — no arena allocation.
    /// </summary>
    public static DataValue FromInterval(Interval value) =>
        new(DataKind.Interval, flags: 0,
            p0: value.Months,
            p1: value.Days,
            p2: (int)value.Microseconds,
            p3: (int)(value.Microseconds >> 32));

    /// <summary>
    /// Creates an <see cref="Interval"/> value from its three independent
    /// components. Convenience for callers that already have the fields in hand.
    /// </summary>
    public static DataValue FromInterval(int months, int days, long microseconds) =>
        FromInterval(new Interval(months, days, microseconds));

    /// <summary>
    /// Creates a type tag value that describes another <see cref="DataKind"/>. When
    /// <paramref name="typeId"/> is non-zero, the tag carries a <see cref="TypeRegistry"/>
    /// id describing the rich shape (struct field names, nested array element types).
    /// For primitive arrays (no descriptor interned), pass <paramref name="describesArray"/>
    /// (and <paramref name="describesMultiDim"/> if the source carried a shape prefix) so
    /// <see cref="FormatType"/> can render <c>Array&lt;...&gt;</c> from the annotation.
    /// </summary>
    /// <remarks>
    /// The array annotation rides in <c>_p1</c> rather than the value's
    /// <see cref="DataValueFlags.IsArray"/> flag deliberately: a Type tag is a
    /// scalar value carrying type *metadata*, never an array of types. Reusing
    /// the IsArray flag would make every "this is a typed-array" consumer
    /// (<c>WebCellFormatter.ShouldRouteToJson</c>, accessor routing, …) mis-treat
    /// the Type tag as if it were an array.
    /// </remarks>
    public static DataValue FromType(DataKind value, ushort typeId = 0, bool describesArray = false, bool describesMultiDim = false)
    {
        int p1 = 0;
        if (describesArray) p1 |= TypeTagDescribesArrayBit;
        if (describesMultiDim) p1 |= TypeTagDescribesMultiDimBit;
        return new(DataKind.Type, flags: 0, typeId: typeId, p0: (int)value, p1: p1);
    }

    private const int TypeTagDescribesArrayBit = 0x01;
    private const int TypeTagDescribesMultiDimBit = 0x02;

    /// <summary>
    /// For a <see cref="DataKind.Type"/> tag: whether the described type is an array
    /// (i.e. the value <c>typeof()</c> was called on had its IsArray flag set).
    /// Always <c>false</c> for non-Type values.
    /// </summary>
    public bool TypeDescribesArray =>
        _kind == DataKind.Type && (_p1 & TypeTagDescribesArrayBit) != 0;

    /// <summary>
    /// For a <see cref="DataKind.Type"/> tag: whether the described array is multi-dim.
    /// Always <c>false</c> for non-Type values, and for scalar Type tags.
    /// </summary>
    public bool TypeDescribesMultiDim =>
        _kind == DataKind.Type && (_p1 & TypeTagDescribesMultiDimBit) != 0;

    /// <summary>
    /// Creates a typed null array value: <c>Kind=elementKind</c> with the
    /// <see cref="DataValueFlags.IsArray"/> and <see cref="DataValueFlags.IsNull"/>
    /// flags set. Mirrors <see cref="NullByteArray"/> generalised to any
    /// element kind. Combined with <see cref="FromStringArray"/> /
    /// <see cref="FromImageArray"/> / <see cref="FromStructArray"/> /
    /// <see cref="FromArenaArray{T}"/> / <see cref="FromArenaArrayBytes"/>,
    /// this is the canonical null carrier for typed arrays.
    /// </summary>
    public static DataValue NullArrayOf(DataKind elementKind) =>
        new(elementKind, flags: DataValueFlags.IsNull | DataValueFlags.IsArray, p0: 0);

    /// <summary>Creates a typed null value.</summary>
    public static DataValue Null(DataKind kind)
        => new(kind, flags: DataValueFlags.IsNull, p0: 0);

    /// <summary>Creates a typed null byte array (UInt8 + IsNull + IsArray).</summary>
    public static DataValue NullByteArray()
        => new(DataKind.UInt8, flags: DataValueFlags.IsNull | DataValueFlags.IsArray, p0: 0);

    /// <summary>
    /// Creates a null value whose type is not statically known.
    /// </summary>
    /// <remarks>
    /// SQL NULL has no inherent type. When a NULL literal appears outside a typed context
    /// (e.g. <c>SELECT NULL</c>), neither the parser nor the evaluator can determine its
    /// kind. This factory produces a <see cref="DataKind.Unknown"/> null. Downstream
    /// consumers (aggregations, output writers, CASE coercion) resolve the actual kind
    /// from context. Call sites should prefer a typed <see cref="Null(DataKind)"/> when
    /// the expected kind is known.
    /// </remarks>
    public static DataValue UnknownNull() => NullUnknown;
}

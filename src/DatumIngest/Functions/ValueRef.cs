using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Function-internal value type. Replaces store-aware <see cref="DataValue"/>
/// passing through scalar function bodies: a <see cref="ValueRef"/> either
/// carries an inline payload (small primitives, short strings, type tags)
/// or a managed reference (long strings, byte arrays, etc.). Lifetime is
/// GC-managed; functions never touch arenas, never thread an
/// <c>IValueStore</c>, never call <c>Stabilize</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ExpressionEvaluator</c> converts inputs at the function
/// boundary: arena/sidecar-backed <see cref="DataValue"/>s are resolved
/// into managed payloads before <see cref="IScalarFunction.Execute"/> is
/// called. The function then operates entirely on managed memory and
/// returns a <see cref="ValueRef"/>; the evaluator converts back to a
/// <see cref="DataValue"/> (writing managed payloads to the query arena
/// or inlining short strings) before handing the row out.
/// </para>
/// <para>
/// State invariants:
/// <list type="bullet">
///   <item><description>
///     <see cref="IsNull"/>: <c>_inline.IsNull == true</c> and
///     <c>Materialized == null</c>.
///   </description></item>
///   <item><description>
///     Inline non-null: <c>_inline</c> carries everything,
///     <c>Materialized == null</c>.
///   </description></item>
///   <item><description>
///     Materialized non-null: <c>_inline</c> is a typed null tag holding
///     <see cref="Kind"/> + <see cref="IsArray"/> only;
///     <c>Materialized</c> holds the real payload (e.g. a <see cref="string"/>
///     for <see cref="DataKind.String"/>, a <see cref="byte"/>[] for
///     byte arrays — <see cref="DataKind.UInt8"/> with the IsArray flag —
///     and <see cref="DataKind.Image"/>).
///   </description></item>
/// </list>
/// </para>
/// </remarks>
public readonly struct ValueRef
{
    private readonly DataValue _inline;
    private readonly object? _materialized;

    private ValueRef(DataValue inline, object? materialized)
    {
        _inline = inline;
        _materialized = materialized;
    }

    /// <summary>The kind of the value carried by this reference.</summary>
    public DataKind Kind => _inline.Kind;

    /// <summary>True when the value is the typed null for its kind.</summary>
    public bool IsNull => _materialized is null && _inline.IsNull;

    /// <summary>True when the value carries the IsArray flag.</summary>
    public bool IsArray => _inline.IsArray;

    /// <summary>True when the value is a byte array (Kind=UInt8 + IsArray).</summary>
    public bool IsByteArrayKind => _inline.IsByteArrayKind;

    /// <summary>
    /// The inline carrier. Only meaningful when <see cref="IsNull"/> is
    /// <c>false</c> and <see cref="Materialized"/> is <c>null</c>; in other
    /// states callers should use <see cref="Materialized"/> or
    /// <see cref="IsNull"/> instead.
    /// </summary>
    public DataValue InlineDataValue => _inline;

    /// <summary>
    /// The managed payload, populated for non-inline kinds (e.g. long
    /// strings, byte arrays). <c>null</c> for inline-or-null values.
    /// </summary>
    public object? Materialized => _materialized;

    /// <summary>
    /// Creates the typed null for the given <paramref name="kind"/>.
    /// </summary>
    public static ValueRef Null(DataKind kind) =>
        new(DataValue.Null(kind), null);

    /// <summary>
    /// Wraps a <see cref="DataValue"/> that is already inline-or-null. Use
    /// this when you have a <see cref="DataValue"/> in hand and know it
    /// doesn't reference an arena — e.g. the result of <see cref="DataValue.FromInt32"/>.
    /// Throws when the value is non-inline non-null because the caller
    /// would have to provide a managed payload to materialize.
    /// </summary>
    public static ValueRef FromInline(DataValue value)
    {
        if (!value.IsNull && !value.IsInline)
        {
            throw new ArgumentException(
                $"FromInline requires an inline or null DataValue; got {value.Kind} with non-inline storage. "
                + "Use the FromString / FromBytes / etc. helpers to wrap a materialized payload instead.",
                nameof(value));
        }
        return new(value, null);
    }

    /// <summary>Boolean inline value.</summary>
    public static ValueRef FromBoolean(bool value) =>
        new(DataValue.FromBoolean(value), null);

    /// <summary>UInt8 inline value.</summary>
    public static ValueRef FromUInt8(byte value) =>
        new(DataValue.FromUInt8(value), null);

    /// <summary>Int8 inline value.</summary>
    public static ValueRef FromInt8(sbyte value) =>
        new(DataValue.FromInt8(value), null);

    /// <summary>Int16 inline value.</summary>
    public static ValueRef FromInt16(short value) =>
        new(DataValue.FromInt16(value), null);

    /// <summary>UInt16 inline value.</summary>
    public static ValueRef FromUInt16(ushort value) =>
        new(DataValue.FromUInt16(value), null);

    /// <summary>Int32 inline value.</summary>
    public static ValueRef FromInt32(int value) =>
        new(DataValue.FromInt32(value), null);

    /// <summary>UInt32 inline value.</summary>
    public static ValueRef FromUInt32(uint value) =>
        new(DataValue.FromUInt32(value), null);

    /// <summary>Int64 inline value.</summary>
    public static ValueRef FromInt64(long value) =>
        new(DataValue.FromInt64(value), null);

    /// <summary>UInt64 inline value.</summary>
    public static ValueRef FromUInt64(ulong value) =>
        new(DataValue.FromUInt64(value), null);

    /// <summary>Float32 inline value.</summary>
    public static ValueRef FromFloat32(float value) =>
        new(DataValue.FromFloat32(value), null);

    /// <summary>Float64 inline value.</summary>
    public static ValueRef FromFloat64(double value) =>
        new(DataValue.FromFloat64(value), null);

    /// <summary>Date inline value.</summary>
    public static ValueRef FromDate(DateOnly value) =>
        new(DataValue.FromDate(value), null);

    /// <summary>DateTime inline value.</summary>
    public static ValueRef FromDateTime(DateTimeOffset value) =>
        new(DataValue.FromDateTime(value), null);

    /// <summary>Time inline value.</summary>
    public static ValueRef FromTime(TimeOnly value) =>
        new(DataValue.FromTime(value), null);

    /// <summary>Duration inline value.</summary>
    public static ValueRef FromDuration(TimeSpan value) =>
        new(DataValue.FromDuration(value), null);

    /// <summary>UUID inline value.</summary>
    public static ValueRef FromUuid(Guid value) =>
        new(DataValue.FromUuid(value), null);

    /// <summary>DataKind tag (the value of <c>typeof(x)</c>).</summary>
    public static ValueRef FromType(DataKind value) =>
        new(DataValue.FromType(value), null);

    /// <summary>
    /// String value carried as a managed payload. The boundary conversion
    /// chooses whether to inline (UTF-8 ≤16 bytes) or write to the query
    /// arena when converting back to <see cref="DataValue"/>.
    /// </summary>
    public static ValueRef FromString(string value) =>
        new(DataValue.Null(DataKind.String), value);

    /// <summary>
    /// Byte-array payload. Pass <see cref="DataKind.Image"/> for image bytes,
    /// or <see cref="DataKind.UInt8"/> with <paramref name="isArray"/> set
    /// to <c>true</c> for generic byte arrays. The DataValue tag carries the
    /// kind (and IsArray flag for byte arrays); the actual bytes live in
    /// <see cref="Materialized"/>.
    /// </summary>
    public static ValueRef FromBytes(DataKind kind, byte[] value, bool isArray = false)
    {
        if (kind == DataKind.Image)
        {
            return new(DataValue.Null(DataKind.Image), value);
        }
        if (kind == DataKind.UInt8 && isArray)
        {
            return new(DataValue.NullByteArray(), value);
        }
        throw new ArgumentException(
            $"FromBytes is only valid for Image or (UInt8 with IsArray=true); got {kind}, isArray={isArray}.",
            nameof(kind));
    }

    /// <summary>
    /// Reads the value as a <see cref="string"/>. For inline strings this
    /// reads the UTF-8 payload from the carrier; for materialized strings
    /// this returns the managed payload directly.
    /// </summary>
    public string AsString()
    {
        if (_materialized is string materialized)
        {
            return materialized;
        }
        if (_inline.IsInline && _inline.Kind == DataKind.String)
        {
            return _inline.AsString();
        }
        throw new InvalidOperationException(
            $"ValueRef of kind {Kind} cannot be read as String. Materialized payload type: "
            + (_materialized?.GetType().Name ?? "<none>"));
    }

    /// <summary>Reads as a UTF-8 byte span where applicable.</summary>
    public byte[] AsBytes()
    {
        if (_materialized is byte[] bytes)
        {
            return bytes;
        }
        throw new InvalidOperationException(
            $"ValueRef of kind {Kind} does not carry a byte payload.");
    }

    /// <summary>Boolean accessor (inline only).</summary>
    public bool AsBoolean() => _inline.AsBoolean();
    /// <summary>UInt8 accessor (inline only).</summary>
    public byte AsUInt8() => _inline.AsUInt8();
    /// <summary>Int8 accessor (inline only).</summary>
    public sbyte AsInt8() => _inline.AsInt8();
    /// <summary>Int16 accessor (inline only).</summary>
    public short AsInt16() => _inline.AsInt16();
    /// <summary>UInt16 accessor (inline only).</summary>
    public ushort AsUInt16() => _inline.AsUInt16();
    /// <summary>Int32 accessor (inline only).</summary>
    public int AsInt32() => _inline.AsInt32();
    /// <summary>UInt32 accessor (inline only).</summary>
    public uint AsUInt32() => _inline.AsUInt32();
    /// <summary>Int64 accessor (inline only).</summary>
    public long AsInt64() => _inline.AsInt64();
    /// <summary>UInt64 accessor (inline only).</summary>
    public ulong AsUInt64() => _inline.AsUInt64();
    /// <summary>Float32 accessor (inline only).</summary>
    public float AsFloat32() => _inline.AsFloat32();
    /// <summary>Float64 accessor (inline only).</summary>
    public double AsFloat64() => _inline.AsFloat64();
    /// <summary>Date accessor (inline only).</summary>
    public DateOnly AsDate() => _inline.AsDate();
    /// <summary>DateTime accessor (inline only).</summary>
    public DateTimeOffset AsDateTime() => _inline.AsDateTime();
    /// <summary>Time accessor (inline only).</summary>
    public TimeOnly AsTime() => _inline.AsTime();
    /// <summary>Duration accessor (inline only).</summary>
    public TimeSpan AsDuration() => _inline.AsDuration();
    /// <summary>UUID accessor (inline only).</summary>
    public Guid AsUuid() => _inline.AsUuid();
    /// <summary>DataKind tag accessor (inline only).</summary>
    public DataKind AsType() => _inline.AsType();
}

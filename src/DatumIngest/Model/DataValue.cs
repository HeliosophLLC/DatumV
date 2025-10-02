using System.Runtime.CompilerServices;
using DatumIngest.Functions.Image;

namespace DatumIngest.Model;

/// <summary>
/// An immutable, discriminated union value that carries typed data through the query pipeline.
/// Use the static factory methods (<see cref="FromFloat32"/>, <see cref="FromVector"/>, etc.)
/// to construct instances and the accessor methods to retrieve typed payloads.
/// </summary>
/// <remarks>
/// <para>
/// The struct is 24 bytes and fully blittable — it contains no managed reference fields.
/// This means <see cref="DataValue"/> arrays are invisible to the garbage collector,
/// eliminating GC scanning overhead for column buffers and row value arrays.
/// </para>
/// <para>
/// Fixed-size primitives (integers, floats, dates, booleans, UUIDs) are stored inline
/// in <c>_numericBits</c> and <c>_bits1</c>.  Reference-type payloads (strings, float
/// arrays, byte arrays, image handles, typed arrays) are stored in a thread-local
/// <see cref="ReferenceStore"/> and accessed via an integer index in <c>_referenceIndex</c>.
/// Arena-backed strings bypass the store entirely, using offset/length in the inline fields.
/// </para>
/// <para>
/// <c>default(DataValue)</c> has <see cref="DataKind.Unknown"/> (= 0) and is not null.
/// It represents an uninitialized or untyped value. Always use factory methods or
/// <see cref="Null"/> to construct intentional values.
/// </para>
/// </remarks>
public readonly struct DataValue : IEquatable<DataValue>
{
    // ───────────────────────── Flag constants ─────────────────────────

    /// <summary>Bit mask for the null flag in <see cref="_flags"/>.</summary>
    private const byte FlagIsNull = 0x01;

    /// <summary>Bit mask indicating the value has a payload in the thread-local <see cref="ReferenceStore"/>.</summary>
    private const byte FlagHasReference = 0x02;

    // ───────────────────────── Fields (24 bytes, blittable) ─────────────────────────

    private readonly DataKind _kind;       //  1 byte  — type discriminator
    private readonly byte _flags;          //  1 byte  — FlagIsNull | FlagHasReference
    private readonly short _meta;          //  2 bytes — type-specific metadata (Array element kind)
    private readonly int _referenceIndex;  //  4 bytes — index into ReferenceStore (when FlagHasReference set)
    private readonly long _numericBits;    //  8 bytes — primary inline payload
    private readonly long _bits1;          //  8 bytes — secondary inline payload

    private DataValue(DataKind kind, long numericBits, long bits1, byte flags, short meta = 0, int referenceIndex = 0)
    {
        _kind = kind;
        _flags = flags;
        _meta = meta;
        _referenceIndex = referenceIndex;
        _numericBits = numericBits;
        _bits1 = bits1;
    }

    /// <summary>The type discriminator for this value.</summary>
    public DataKind Kind => _kind;

    /// <summary>Whether this value represents a typed null.</summary>
    public bool IsNull => (_flags & FlagIsNull) != 0;

    /// <summary>Whether this value has a reference-type payload in the <see cref="ReferenceStore"/>.</summary>
    internal bool HasReference => (_flags & FlagHasReference) != 0;

    // ───────────────────────── Cached common instances ─────────────────────────

    private static readonly DataValue Float32Zero = new(DataKind.Float32, numericBits: 0L, bits1: 0L, flags: 0);
    private static readonly DataValue Float32One = new(DataKind.Float32, numericBits: BitConverter.SingleToInt32Bits(1f), bits1: 0L, flags: 0);
    private static readonly DataValue NullUnknown = new(DataKind.Unknown, numericBits: 0L, bits1: 0L, flags: FlagIsNull);
    private static readonly DataValue BooleanTrue = new(DataKind.Boolean, numericBits: 1L, bits1: 0L, flags: 0);
    private static readonly DataValue BooleanFalse = new(DataKind.Boolean, numericBits: 0L, bits1: 0L, flags: 0);

    // ───────────────────────── Factory methods ─────────────────────────

    /// <summary>Creates a value from a 32-bit floating-point number.</summary>
    public static DataValue FromFloat32(float value)
    {
        if (value == 0f) return Float32Zero;
        if (value == 1f) return Float32One;
        return new(DataKind.Float32, numericBits: BitConverter.SingleToInt32Bits(value), bits1: 0L, flags: 0);
    }

    /// <summary>Creates a value from an unsigned 8-bit integer.</summary>
    public static DataValue FromUInt8(byte value) =>
        new(DataKind.UInt8, numericBits: value, bits1: 0L, flags: 0);

    /// <summary>Creates a value from a signed 8-bit integer.</summary>
    public static DataValue FromInt8(sbyte value) =>
        new(DataKind.Int8, numericBits: value, bits1: 0L, flags: 0);

    /// <summary>Creates a value from a signed 16-bit integer.</summary>
    public static DataValue FromInt16(short value) =>
        new(DataKind.Int16, numericBits: value, bits1: 0L, flags: 0);

    /// <summary>Creates a value from an unsigned 16-bit integer.</summary>
    public static DataValue FromUInt16(ushort value) =>
        new(DataKind.UInt16, numericBits: value, bits1: 0L, flags: 0);

    /// <summary>Creates a value from a signed 32-bit integer.</summary>
    public static DataValue FromInt32(int value) =>
        new(DataKind.Int32, numericBits: value, bits1: 0L, flags: 0);

    /// <summary>Creates a value from an unsigned 32-bit integer.</summary>
    public static DataValue FromUInt32(uint value) =>
        new(DataKind.UInt32, numericBits: value, bits1: 0L, flags: 0);

    /// <summary>Creates a value from a signed 64-bit integer.</summary>
    public static DataValue FromInt64(long value) =>
        new(DataKind.Int64, numericBits: value, bits1: 0L, flags: 0);

    /// <summary>Creates a value from an unsigned 64-bit integer.</summary>
    public static DataValue FromUInt64(ulong value) =>
        new(DataKind.UInt64, numericBits: unchecked((long)value), bits1: 0L, flags: 0);

    /// <summary>Creates a value from a 64-bit double-precision floating-point number.</summary>
    public static DataValue FromFloat64(double value) =>
        new(DataKind.Float64, numericBits: BitConverter.DoubleToInt64Bits(value), bits1: 0L, flags: 0);

    /// <summary>Creates a value from a byte array.</summary>
    public static DataValue FromUInt8Array(byte[] value)
    {
        int index = ReferenceStore.Current().Add(value);
        return new(DataKind.UInt8Array, numericBits: 0L, bits1: 0L, flags: FlagHasReference, referenceIndex: index);
    }

    /// <summary>Creates a value from a text string.</summary>
    public static DataValue FromString(string value)
    {
        int index = ReferenceStore.Current().InternString(value);
        return new(DataKind.String, numericBits: 0L, bits1: 0L, flags: FlagHasReference, referenceIndex: index);
    }

    /// <summary>
    /// Creates a value from a reference index that has already been interned in the
    /// current <see cref="ReferenceStore"/>. Used by decoders that call
    /// <see cref="ReferenceStore.InternStringFromUtf8"/> directly to avoid a
    /// redundant dictionary lookup.
    /// </summary>
    /// <param name="kind">The data kind (must be a reference-backed kind such as
    /// <see cref="DataKind.String"/> or <see cref="DataKind.JsonValue"/>).</param>
    /// <param name="referenceIndex">Index returned by a prior intern call.</param>
    internal static DataValue FromInternedReference(DataKind kind, int referenceIndex) =>
        new(kind, numericBits: 0L, bits1: 0L, flags: FlagHasReference, referenceIndex: referenceIndex);

    /// <summary>
    /// Creates an arena-backed string value from an offset and length within a
    /// <see cref="StringArena"/>.  The actual bytes are not stored in this struct;
    /// callers must resolve via <see cref="AsString(StringArena)"/>.
    /// </summary>
    /// <param name="offset">Byte offset into the owning <see cref="StringArena"/>.</param>
    /// <param name="length">Byte length of the UTF-8 encoded string.</param>
    public static DataValue FromStringSlice(int offset, int length) =>
        new(DataKind.String, numericBits: offset, bits1: length, flags: 0);

    /// <summary>Creates a rank-1 tensor (vector) from a float array.</summary>
    public static DataValue FromVector(float[] value)
    {
        int index = ReferenceStore.Current().Add(value);
        return new(DataKind.Vector, numericBits: 0L, bits1: 0L, flags: FlagHasReference, referenceIndex: index);
    }

    /// <summary>Creates a rank-2 tensor (matrix) from a flat float array and its dimensions.</summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="rows"/> * <paramref name="columns"/> does not equal the data length.
    /// </exception>
    public static DataValue FromMatrix(float[] data, int rows, int columns)
    {
        if (data.Length != rows * columns)
        {
            throw new ArgumentException(
                $"Data length {data.Length} does not match shape {rows}x{columns}.");
        }

        int index = ReferenceStore.Current().Add(data);
        return new DataValue(DataKind.Matrix, numericBits: (long)rows | ((long)columns << 32),
                             bits1: 0L, flags: FlagHasReference, referenceIndex: index);
    }

    /// <summary>Creates an arbitrary-rank tensor from a flat float array and its shape.</summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the product of <paramref name="shape"/> dimensions does not equal the data length.
    /// </exception>
    public static DataValue FromTensor(float[] data, int[] shape)
    {
        int expectedLength = 1;
        foreach (int dimension in shape)
        {
            expectedLength *= dimension;
        }

        if (data.Length != expectedLength)
        {
            throw new ArgumentException(
                $"Data length {data.Length} does not match shape [{string.Join(", ", shape)}].");
        }

        ReferenceStore store = ReferenceStore.Current();
        int index = store.AddPair(data, shape);
        return new DataValue(DataKind.Tensor, numericBits: 0L, bits1: 0L, flags: FlagHasReference, referenceIndex: index);
    }

    /// <summary>Creates a value from encoded image bytes.</summary>
    public static DataValue FromImage(byte[] value)
    {
        int index = ReferenceStore.Current().Add(value);
        return new(DataKind.Image, numericBits: 0L, bits1: 0L, flags: FlagHasReference, referenceIndex: index);
    }

    /// <summary>
    /// Creates a value from an <see cref="ImageHandle"/>.
    /// The handle carries a decoded bitmap and/or encoded bytes, enabling
    /// fused image pipelines that avoid redundant decode/encode cycles.
    /// </summary>
    internal static DataValue FromImageHandle(ImageHandle handle)
    {
        int index = ReferenceStore.Current().Add(handle);
        return new(DataKind.Image, numericBits: 0L, bits1: 0L, flags: FlagHasReference, referenceIndex: index);
    }

    /// <summary>Creates a value from a calendar date.</summary>
    public static DataValue FromDate(DateOnly value) =>
        new(DataKind.Date, numericBits: value.DayNumber, bits1: 0L, flags: 0);

    /// <summary>Creates a value from a date and time with UTC offset.</summary>
    public static DataValue FromDateTime(DateTimeOffset value) =>
        new(DataKind.DateTime, numericBits: value.Ticks, bits1: value.Offset.Ticks / TimeSpan.TicksPerMinute, flags: 0);

    /// <summary>Creates a value from a raw JSON string.</summary>
    public static DataValue FromJsonValue(string value)
    {
        int index = ReferenceStore.Current().InternString(value);
        return new(DataKind.JsonValue, numericBits: 0L, bits1: 0L, flags: FlagHasReference, referenceIndex: index);
    }

    /// <summary>Creates a value from a 128-bit universally unique identifier.</summary>
    public static DataValue FromUuid(Guid value)
    {
        ref long pair = ref Unsafe.As<Guid, long>(ref value);
        return new(DataKind.Uuid, numericBits: pair, bits1: Unsafe.Add(ref pair, 1), flags: 0);
    }

    /// <summary>Creates a boolean value.</summary>
    public static DataValue FromBoolean(bool value) =>
        value ? BooleanTrue : BooleanFalse;

    /// <summary>Creates a value from a time-of-day.</summary>
    public static DataValue FromTime(TimeOnly value) =>
        new(DataKind.Time, numericBits: value.Ticks, bits1: 0L, flags: 0);

    /// <summary>Creates a value from a duration (elapsed time span).</summary>
    public static DataValue FromDuration(TimeSpan value) =>
        new(DataKind.Duration, numericBits: value.Ticks, bits1: 0L, flags: 0);

    /// <summary>Creates a type tag value that describes another <see cref="DataKind"/>.</summary>
    public static DataValue FromType(DataKind value) =>
        new(DataKind.Type, numericBits: (long)value, bits1: 0L, flags: 0);

    // ───────────────────────── Arena state ─────────────────────────

    /// <summary>
    /// Whether this value stores an arena offset rather than a direct reference.
    /// True for string/JSON values created via <see cref="FromStringSlice"/>.
    /// </summary>
    public bool IsArenaBacked =>
        (_kind is DataKind.String or DataKind.JsonValue) && (_flags & (FlagIsNull | FlagHasReference)) == 0;

    /// <summary>
    /// Returns a new <see cref="DataValue"/> with all arena-backed data materialised
    /// into self-contained managed objects.  Non-arena values are returned unchanged.
    /// </summary>
    /// <param name="stringArena">Arena for string data.</param>
    /// <param name="dataArena">Arena for float/byte blob data.</param>
    /// <returns>A self-contained value that does not reference any arena.</returns>
    public DataValue Materialize(StringArena stringArena, DataArena dataArena)
    {
        if (!IsArenaBacked) return this;

        return _kind switch
        {
            DataKind.String => FromString(stringArena.GetString((int)_numericBits, (int)_bits1)),
            DataKind.JsonValue => FromJsonValue(stringArena.GetString((int)_numericBits, (int)_bits1)),
            _ => this,
        };
    }

    /// <summary>
    /// Returns a new arena-backed <see cref="DataValue"/> whose offset has been shifted by
    /// <paramref name="delta"/> bytes.  Used when merging per-column private arenas into a
    /// shared batch arena after parallel decode.
    /// </summary>
    /// <param name="delta">Number of bytes to add to the current offset.</param>
    /// <returns>An adjusted value whose length is unchanged.</returns>
    internal DataValue WithArenaOffset(int delta)
    {
        return new DataValue(_kind, numericBits: _numericBits + delta, bits1: _bits1, flags: 0);
    }

    /// <summary>
    /// Creates a typed array value from an element kind and an array of elements.
    /// The element kind is stored in the <c>_meta</c> field so it can be recovered at runtime.
    /// </summary>
    /// <param name="elementKind">The <see cref="DataKind"/> shared by all elements.</param>
    /// <param name="elements">The array of element values.</param>
    public static DataValue FromArray(DataKind elementKind, DataValue[] elements)
    {
        int index = ReferenceStore.Current().Add(elements);
        return new(DataKind.Array, numericBits: 0L, bits1: 0L, flags: FlagHasReference,
                   meta: (short)elementKind, referenceIndex: index);
    }

    /// <summary>Creates a typed null array with the given element kind.</summary>
    /// <param name="elementKind">The element kind of the null array.</param>
    public static DataValue NullArray(DataKind elementKind) =>
        new(DataKind.Array, numericBits: 0L, bits1: 0L, flags: FlagIsNull, meta: (short)elementKind);

    /// <summary>
    /// Creates a struct value from a positional array of field values.
    /// The field names and kinds are not stored per-value — they live in the
    /// <see cref="ColumnInfo.Fields"/> descriptor that is shared across all rows.
    /// </summary>
    /// <param name="fieldCount">The number of fields, stored in <c>_meta</c> for fast validation.</param>
    /// <param name="fields">Positional field values, one entry per field in declaration order.</param>
    public static DataValue FromStruct(short fieldCount, DataValue[] fields)
    {
        int index = ReferenceStore.Current().Add(fields);
        return new(DataKind.Struct, numericBits: 0L, bits1: 0L, flags: FlagHasReference,
                   meta: fieldCount, referenceIndex: index);
    }

    /// <summary>Creates a typed null struct with the given field count.</summary>
    public static DataValue NullStruct(short fieldCount) =>
        new(DataKind.Struct, numericBits: 0L, bits1: 0L, flags: FlagIsNull, meta: fieldCount);

    /// <summary>Creates a typed null value.</summary>
    public static DataValue Null(DataKind kind)
        => new(kind, numericBits: 0L, bits1: 0L, flags: FlagIsNull);

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

    // ───────────────────────── Literal conversion ─────────────────────────

    /// <summary>
    /// Converts a CLR literal value (typically from an AST <see cref="Parsing.Ast.LiteralExpression"/>)
    /// to a <see cref="DataValue"/> using the natural type mapping. This is the single canonical
    /// conversion point for all literal-to-DataValue bridging in the system.
    /// </summary>
    /// <param name="rawLiteral">
    /// A boxed CLR value: <see cref="double"/> (from the SQL parser), <see cref="int"/>,
    /// <see cref="long"/>, <see cref="float"/> (from rewriters), <see cref="string"/>,
    /// <see cref="bool"/>, or an existing <see cref="DataValue"/>.
    /// </param>
    /// <returns>A <see cref="DataValue"/> preserving the CLR type's natural precision.</returns>
    /// <exception cref="ArgumentException">The literal type is not supported.</exception>
    public static DataValue FromLiteral(object rawLiteral)
    {
        return rawLiteral switch
        {
            DataValue dataValue => dataValue,
            sbyte int8Value => FromInt8(int8Value),
            short int16Value => FromInt16(int16Value),
            int intValue => FromInt32(intValue),
            long longValue => FromInt64(longValue),
            float floatValue => FromFloat32(floatValue),
            double doubleValue => FromFloat64(doubleValue),
            decimal decimalValue => FromFloat64((double)decimalValue),
            string stringValue => FromString(stringValue),
            bool boolValue => FromBoolean(boolValue),
            _ => throw new ArgumentException(
                $"Unsupported literal type: {rawLiteral.GetType().Name}.", nameof(rawLiteral)),
        };
    }

    /// <summary>
    /// Coerces this value to a different <see cref="DataKind"/>. Used when the column's
    /// stored type differs from the literal's natural type (e.g. a <see cref="DataKind.Float64"/>
    /// literal compared against a <see cref="DataKind.Int32"/> bitmap index key).
    /// </summary>
    /// <remarks>
    /// Numeric and boolean values are coerced via a <see cref="double"/> intermediary.
    /// Non-numeric/non-boolean values, or values whose kind already matches
    /// <paramref name="targetKind"/>, are returned unchanged.
    /// </remarks>
    /// <param name="targetKind">The desired <see cref="DataKind"/>.</param>
    /// <returns>A new value of the target kind, or this value unchanged if coercion is not applicable.</returns>
    public DataValue CoerceToKind(DataKind targetKind)
    {
        if (_kind == targetKind)
        {
            return this;
        }

        if (!IsCoercibleKind(_kind) || !IsCoercibleKind(targetKind))
        {
            return this;
        }

        double intermediate = ToDoubleRaw();
        return FromDoubleRaw(intermediate, targetKind);
    }

    /// <summary>
    /// Returns whether the given kind participates in numeric/boolean coercion.
    /// </summary>
    private static bool IsCoercibleKind(DataKind kind)
    {
        return kind is DataKind.Float32 or DataKind.Float64
            or DataKind.UInt8 or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Boolean;
    }

    /// <summary>
    /// Extracts the numeric payload as a <see cref="double"/> for coercion purposes.
    /// </summary>
    private double ToDoubleRaw()
    {
        return _kind switch
        {
            DataKind.Float32 => BitConverter.Int32BitsToSingle((int)_numericBits),
            DataKind.Float64 => BitConverter.Int64BitsToDouble(_numericBits),
            DataKind.UInt8 => (byte)_numericBits,
            DataKind.Int8 => (sbyte)_numericBits,
            DataKind.Int16 => (short)_numericBits,
            DataKind.UInt16 => (ushort)_numericBits,
            DataKind.Int32 => (int)_numericBits,
            DataKind.UInt32 => (uint)_numericBits,
            DataKind.Int64 => _numericBits,
            DataKind.UInt64 => (ulong)_numericBits,
            DataKind.Boolean => _numericBits != 0L ? 1d : 0d,
            _ => 0d,
        };
    }

    /// <summary>
    /// Creates a <see cref="DataValue"/> of the specified kind from a <see cref="double"/> value.
    /// </summary>
    private static DataValue FromDoubleRaw(double value, DataKind targetKind)
    {
        return targetKind switch
        {
            DataKind.Float32 => FromFloat32((float)value),
            DataKind.Float64 => FromFloat64(value),
            DataKind.UInt8 => FromUInt8((byte)value),
            DataKind.Int8 => FromInt8((sbyte)value),
            DataKind.Int16 => FromInt16((short)value),
            DataKind.UInt16 => FromUInt16((ushort)value),
            DataKind.Int32 => FromInt32((int)value),
            DataKind.UInt32 => FromUInt32((uint)value),
            DataKind.Int64 => FromInt64((long)value),
            DataKind.UInt64 => FromUInt64((ulong)value),
            DataKind.Boolean => FromBoolean(value != 0d),
            _ => throw new ArgumentException(
                $"Cannot coerce to non-numeric kind {targetKind}.", nameof(targetKind)),
        };
    }

    // ───────────────────────── Accessor methods ─────────────────────────

    /// <summary>Returns the 32-bit floating-point payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public float AsFloat32()
    {
        ThrowIfNullOrWrongKind(DataKind.Float32);
        return BitConverter.Int32BitsToSingle((int)_numericBits);
    }

    /// <summary>Returns the unsigned 8-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public byte AsUInt8()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt8);
        return (byte)_numericBits;
    }

    /// <summary>Returns the signed 8-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public sbyte AsInt8()
    {
        ThrowIfNullOrWrongKind(DataKind.Int8);
        return (sbyte)_numericBits;
    }

    /// <summary>Returns the signed 16-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public short AsInt16()
    {
        ThrowIfNullOrWrongKind(DataKind.Int16);
        return (short)_numericBits;
    }

    /// <summary>Returns the unsigned 16-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public ushort AsUInt16()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt16);
        return (ushort)_numericBits;
    }

    /// <summary>Returns the signed 32-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public int AsInt32()
    {
        ThrowIfNullOrWrongKind(DataKind.Int32);
        return (int)_numericBits;
    }

    /// <summary>Returns the unsigned 32-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public uint AsUInt32()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt32);
        return (uint)_numericBits;
    }

    /// <summary>Returns the signed 64-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public long AsInt64()
    {
        ThrowIfNullOrWrongKind(DataKind.Int64);
        return _numericBits;
    }

    /// <summary>Returns the unsigned 64-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public ulong AsUInt64()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt64);
        return unchecked((ulong)_numericBits);
    }

    /// <summary>Returns the 64-bit double-precision floating-point payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public double AsFloat64()
    {
        ThrowIfNullOrWrongKind(DataKind.Float64);
        return BitConverter.Int64BitsToDouble(_numericBits);
    }

    // ─────────────────────── Widening numeric conversions ───────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when this value's <see cref="Kind"/> is any integer,
    /// floating-point, or boolean scalar that can be widened to <see cref="float"/> or <see cref="double"/>.
    /// Boolean values are treated as 1 (true) and 0 (false).
    /// </summary>
    public bool IsNumericScalar => IsNumericScalarKind(_kind);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="kind"/> is any integer,
    /// floating-point, or boolean scalar that can be converted to a numeric value.
    /// </summary>
    public static bool IsNumericScalarKind(DataKind kind) =>
        kind is DataKind.Float32 or DataKind.Float64
            or DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64
            or DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64
            or DataKind.Boolean;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="kind"/> is any integer type
    /// (signed or unsigned). Excludes floating-point and boolean kinds.
    /// Use for function parameters that are logically integer (positions, counts, indices).
    /// </summary>
    public static bool IsIntegerKind(DataKind kind) =>
        kind is DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64
            or DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64;

    /// <summary>
    /// Widens any numeric scalar value to <see cref="float"/>.
    /// Int64/UInt64 values may lose precision beyond 2^24.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null or not a numeric scalar kind.</exception>
    public float ToFloat()
    {
        if (TryToFloat(out float result)) return result;
        throw new InvalidOperationException($"Cannot convert {_kind} to float.");
    }

    /// <summary>
    /// Widens any numeric scalar value to <see cref="double"/>.
    /// UInt64 values may lose precision beyond 2^53.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null or not a numeric scalar kind.</exception>
    public double ToDouble()
    {
        if (TryToDouble(out double result)) return result;
        throw new InvalidOperationException($"Cannot convert {_kind} to double.");
    }

    /// <summary>
    /// Converts any numeric scalar value to <see cref="int"/>.
    /// Floating-point values are truncated. Values outside the int range overflow silently.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null or not a numeric scalar kind.</exception>
    public int ToInt32()
    {
        if (TryToInt32(out int result)) return result;
        throw new InvalidOperationException($"Cannot convert {_kind} to int.");
    }

    /// <summary>
    /// Converts any numeric scalar value to <see cref="long"/>.
    /// Floating-point values are truncated. UInt64 values beyond <see cref="long.MaxValue"/> overflow silently.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null or not a numeric scalar kind.</exception>
    public long ToInt64()
    {
        if (TryToInt64(out long result)) return result;
        throw new InvalidOperationException($"Cannot convert {_kind} to long.");
    }

    /// <summary>Attempts to widen this value to <see cref="float"/>. Returns <see langword="false"/> for non-numeric kinds or null values.</summary>
    public bool TryToFloat(out float result)
    {
        if (IsNull) { result = default; return false; }
        switch (_kind)
        {
            case DataKind.Float32: result = AsFloat32(); return true;
            case DataKind.Float64: result = (float)AsFloat64(); return true;
            case DataKind.UInt8:   result = AsUInt8(); return true;
            case DataKind.Int8:    result = AsInt8(); return true;
            case DataKind.Int16:   result = AsInt16(); return true;
            case DataKind.UInt16:  result = AsUInt16(); return true;
            case DataKind.Int32:   result = AsInt32(); return true;
            case DataKind.UInt32:  result = AsUInt32(); return true;
            case DataKind.Int64:   result = AsInt64(); return true;
            case DataKind.UInt64:  result = AsUInt64(); return true;
            case DataKind.Boolean: result = AsBoolean() ? 1f : 0f; return true;
            default: result = default; return false;
        }
    }

    /// <summary>Attempts to widen this value to <see cref="double"/>. Returns <see langword="false"/> for non-numeric kinds or null values.</summary>
    public bool TryToDouble(out double result)
    {
        if (IsNull) { result = default; return false; }
        switch (_kind)
        {
            case DataKind.Float32: result = AsFloat32(); return true;
            case DataKind.Float64: result = AsFloat64(); return true;
            case DataKind.UInt8:   result = AsUInt8(); return true;
            case DataKind.Int8:    result = AsInt8(); return true;
            case DataKind.Int16:   result = AsInt16(); return true;
            case DataKind.UInt16:  result = AsUInt16(); return true;
            case DataKind.Int32:   result = AsInt32(); return true;
            case DataKind.UInt32:  result = AsUInt32(); return true;
            case DataKind.Int64:   result = AsInt64(); return true;
            case DataKind.UInt64:  result = (double)AsUInt64(); return true;
            case DataKind.Boolean: result = AsBoolean() ? 1.0 : 0.0; return true;
            default: result = default; return false;
        }
    }

    /// <summary>Attempts to convert this value to <see cref="int"/>. Returns <see langword="false"/> for non-numeric kinds or null values.</summary>
    public bool TryToInt32(out int result)
    {
        if (IsNull) { result = default; return false; }
        switch (_kind)
        {
            case DataKind.Float32: result = (int)AsFloat32(); return true;
            case DataKind.Float64: result = (int)AsFloat64(); return true;
            case DataKind.UInt8:   result = AsUInt8(); return true;
            case DataKind.Int8:    result = AsInt8(); return true;
            case DataKind.Int16:   result = AsInt16(); return true;
            case DataKind.UInt16:  result = AsUInt16(); return true;
            case DataKind.Int32:   result = AsInt32(); return true;
            case DataKind.UInt32:  result = (int)AsUInt32(); return true;
            case DataKind.Int64:   result = (int)AsInt64(); return true;
            case DataKind.UInt64:  result = (int)AsUInt64(); return true;
            case DataKind.Boolean: result = AsBoolean() ? 1 : 0; return true;
            default: result = default; return false;
        }
    }

    /// <summary>Attempts to convert this value to <see cref="long"/>. Returns <see langword="false"/> for non-numeric kinds or null values.</summary>
    public bool TryToInt64(out long result)
    {
        if (IsNull) { result = default; return false; }
        switch (_kind)
        {
            case DataKind.Float32: result = (long)AsFloat32(); return true;
            case DataKind.Float64: result = (long)AsFloat64(); return true;
            case DataKind.UInt8:   result = AsUInt8(); return true;
            case DataKind.Int8:    result = AsInt8(); return true;
            case DataKind.Int16:   result = AsInt16(); return true;
            case DataKind.UInt16:  result = AsUInt16(); return true;
            case DataKind.Int32:   result = AsInt32(); return true;
            case DataKind.UInt32:  result = AsUInt32(); return true;
            case DataKind.Int64:   result = AsInt64(); return true;
            case DataKind.UInt64:  result = (long)AsUInt64(); return true;
            case DataKind.Boolean: result = AsBoolean() ? 1L : 0L; return true;
            default: result = default; return false;
        }
    }

    // ─────────────────────── Date/time widening ───────────────────────────────

    /// <summary>
    /// Converts a <see cref="DataKind.Date"/> or <see cref="DataKind.DateTime"/>
    /// value to <see cref="DateTimeOffset"/>. Date values become midnight UTC.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The value is null or its kind is neither Date nor DateTime.
    /// </exception>
    public DateTimeOffset ToDateTimeOffset()
    {
        if (IsNull) throw new InvalidOperationException("Cannot convert a null DataValue to DateTimeOffset.");
        return _kind switch
        {
            DataKind.Date => new DateTimeOffset(AsDate().ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            DataKind.DateTime => AsDateTime(),
            _ => throw new InvalidOperationException(
                $"Cannot convert DataKind.{_kind} to DateTimeOffset. Expected Date or DateTime."),
        };
    }

    // ─────────────────────── Object boxing ─────────────────────────────────────

    /// <summary>
    /// Returns the value as its natural boxed CLR type. Useful for JSON serialization
    /// and other contexts where the typed object is needed rather than a string.
    /// </summary>
    /// <returns>
    /// The boxed primitive (<see cref="float"/>, <see cref="int"/>, <see cref="bool"/>, etc.),
    /// the reference-type payload (<see cref="string"/>, <c>float[]</c>, <c>byte[]</c>, etc.),
    /// or <see langword="null"/> when <see cref="IsNull"/> is true.
    /// Composite types (<see cref="DataKind.Array"/>, <see cref="DataKind.Struct"/>) return
    /// the raw <c>DataValue[]</c> — callers that need recursive conversion should handle
    /// those kinds explicitly.
    /// </returns>
    public object? ToObject()
    {
        if (IsNull) return null;

        return _kind switch
        {
            DataKind.Float32   => AsFloat32(),
            DataKind.Float64   => AsFloat64(),
            DataKind.UInt8     => AsUInt8(),
            DataKind.Int8      => AsInt8(),
            DataKind.Int16     => AsInt16(),
            DataKind.UInt16    => AsUInt16(),
            DataKind.Int32     => AsInt32(),
            DataKind.UInt32    => AsUInt32(),
            DataKind.Int64     => AsInt64(),
            DataKind.UInt64    => AsUInt64(),
            DataKind.Boolean   => AsBoolean(),
            DataKind.String    => AsString(),
            DataKind.Date      => AsDate(),
            DataKind.DateTime  => AsDateTime(),
            DataKind.Time      => AsTime(),
            DataKind.Duration  => AsDuration(),
            DataKind.Uuid      => AsUuid(),
            DataKind.JsonValue => AsJsonValue(),
            DataKind.Vector    => AsVector(),
            DataKind.Matrix    => AsMatrix(out _, out _),
            DataKind.Tensor    => AsTensor(out _),
            DataKind.UInt8Array => AsUInt8Array(),
            DataKind.Image     => AsImage(),
            DataKind.Array     => AsArray(),
            DataKind.Struct    => AsStruct(),
            _ => ToString(),
        };
    }

    // ─────────────────────── Display formatting ───────────────────────────────

    /// <summary>
    /// Returns a human-readable string representation of this value suitable for
    /// display in tables, logs, and diagnostics.
    /// </summary>
    /// <param name="converter">
    /// Optional per-kind override. When supplied, the delegate is called first with the
    /// value's <see cref="DataKind"/>. If it returns <c>(true, result)</c> the result is
    /// used directly; if it returns <c>(false, _)</c> the canonical formatting applies.
    /// This lets callers customise specific kinds (e.g. numeric precision, date format)
    /// while inheriting default formatting for everything else.
    /// </param>
    /// <returns>
    /// A formatted string, or <c>"NULL"</c> when <see cref="IsNull"/> is true.
    /// </returns>
    public string ToDisplayString(Func<DataValue, (bool Handled, string? Result)>? converter = null)
    {
        if (IsNull) return "NULL";

        if (converter is not null)
        {
            (bool handled, string? result) = converter(this);
            if (handled) return result ?? "NULL";
        }

        return _kind switch
        {
            DataKind.Float32  => AsFloat32().ToString("G"),
            DataKind.Float64  => AsFloat64().ToString("G"),
            DataKind.UInt8    => AsUInt8().ToString(),
            DataKind.Int8     => AsInt8().ToString(),
            DataKind.Int16    => AsInt16().ToString(),
            DataKind.UInt16   => AsUInt16().ToString(),
            DataKind.Int32    => AsInt32().ToString(),
            DataKind.UInt32   => AsUInt32().ToString(),
            DataKind.Int64    => AsInt64().ToString(),
            DataKind.UInt64   => AsUInt64().ToString(),
            DataKind.Boolean  => AsBoolean() ? "true" : "false",
            DataKind.String   => AsString(),
            DataKind.Date     => AsDate().ToString("yyyy-MM-dd"),
            DataKind.DateTime => AsDateTime().ToString("O"),
            DataKind.Time     => AsTime().ToString("HH:mm:ss.FFFFFFF"),
            DataKind.Duration => AsDuration().ToString("c"),
            DataKind.Uuid     => AsUuid().ToString("D"),
            DataKind.JsonValue => AsJsonValue(),
            DataKind.Vector   => $"[{string.Join(", ", AsVector().Select(v => v.ToString("G")))}]",
            DataKind.Matrix   => FormatMatrixDisplay(),
            DataKind.Tensor   => FormatTensorDisplay(),
            DataKind.UInt8Array => $"UInt8Array[{AsUInt8Array().Length}]",
            DataKind.Image    => $"Image[{AsImage().Length} bytes]",
            DataKind.Array    => FormatArrayDisplay(),
            DataKind.Struct   => FormatStructDisplay(),
            DataKind.Type     => AsType().ToString(),
            _ => ToString() ?? _kind.ToString(),
        };
    }

    private string FormatMatrixDisplay()
    {
        float[] data = AsMatrix(out int rows, out int columns);
        return $"Matrix[{rows}x{columns}]";
    }

    private string FormatTensorDisplay()
    {
        float[] data = AsTensor(out int[] shape);
        return $"Tensor[{string.Join("x", shape)}]";
    }

    private string FormatArrayDisplay()
    {
        DataValue[] elements = AsArray();
        return $"[{string.Join(", ", elements.Select(e => e.ToDisplayString()))}]";
    }

    private string FormatStructDisplay()
    {
        DataValue[] fields = AsStruct();
        return $"{{{string.Join(", ", fields.Select(f => f.ToDisplayString()))}}}";
    }

    // ─────────────────────── Reference-type accessors ─────────────────────────

    /// <summary>Returns the byte array payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public byte[] AsUInt8Array()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt8Array);
        return ReferenceStore.Current().Get<byte[]>(_referenceIndex);
    }

    /// <summary>Returns the text string payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public string AsString()
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        if ((_flags & FlagHasReference) == 0)
        {
            throw new InvalidOperationException(
                "This string value is arena-backed. Use AsString(StringArena) to materialise it.");
        }

        return ReferenceStore.Current().Get<string>(_referenceIndex);
    }

    /// <summary>
    /// Returns the text string payload, resolving arena-backed values from the
    /// given <see cref="StringArena"/>.  Falls back to the reference store for
    /// non-arena values.
    /// </summary>
    /// <param name="arena">The arena that owns the UTF-8 bytes.</param>
    /// <returns>The decoded string.</returns>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public string AsString(StringArena arena)
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        if ((_flags & FlagHasReference) != 0)
        {
            return ReferenceStore.Current().Get<string>(_referenceIndex);
        }

        return arena.GetString((int)_numericBits, (int)_bits1);
    }

    /// <summary>
    /// Returns the raw UTF-8 bytes for an arena-backed string without allocating.
    /// </summary>
    /// <param name="arena">The arena that owns the UTF-8 bytes.</param>
    /// <returns>A span of UTF-8 bytes.  Valid only while the arena is alive.</returns>
    /// <exception cref="InvalidOperationException">Wrong kind, null, or not arena-backed.</exception>
    public ReadOnlySpan<byte> GetArenaStringSpan(StringArena arena)
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        return arena.GetSpan((int)_numericBits, (int)_bits1);
    }

    /// <summary>Returns the vector (rank-1) float array payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public float[] AsVector()
    {
        ThrowIfNullOrWrongKind(DataKind.Vector);
        return ReferenceStore.Current().Get<float[]>(_referenceIndex);
    }

    /// <summary>Returns the matrix (rank-2) flat float array and its dimensions.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public float[] AsMatrix(out int rows, out int columns)
    {
        ThrowIfNullOrWrongKind(DataKind.Matrix);
        rows = (int)_numericBits;
        columns = (int)(_numericBits >> 32);
        return ReferenceStore.Current().Get<float[]>(_referenceIndex);
    }

    /// <summary>Returns the tensor flat float array and its shape.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public float[] AsTensor(out int[] shape)
    {
        ThrowIfNullOrWrongKind(DataKind.Tensor);
        ReferenceStore store = ReferenceStore.Current();
        float[] data = store.Get<float[]>(_referenceIndex);
        shape = store.Get<int[]>(_referenceIndex + 1);
        return data;
    }

    /// <summary>
    /// Returns the encoded image byte array payload.
    /// When the payload is an <see cref="ImageHandle"/> (from a fused pipeline),
    /// the bytes are lazily encoded on first access.
    /// </summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public byte[] AsImage()
    {
        ThrowIfNullOrWrongKind(DataKind.Image);
        object payload = ReferenceStore.Current().Get(_referenceIndex);

        if (payload is ImageHandle handle)
        {
            return handle.GetEncodedBytes();
        }

        return (byte[])payload;
    }

    /// <summary>
    /// Returns the <see cref="ImageHandle"/> for this image value.
    /// If the payload is raw bytes, wraps them in a new handle (no bitmap decode yet).
    /// If the payload is already an <see cref="ImageHandle"/>, returns it directly.
    /// </summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    internal ImageHandle GetImageHandle()
    {
        ThrowIfNullOrWrongKind(DataKind.Image);
        object payload = ReferenceStore.Current().Get(_referenceIndex);

        if (payload is ImageHandle handle)
        {
            return handle;
        }

        byte[] bytes = (byte[])payload;
        return new ImageHandle(bytes, ImageEncoder.ResolveFormat(bytes, formatOverride: null));
    }

    /// <summary>
    /// Returns the <see cref="ImageHandle"/> payload if this value already owns one,
    /// or <c>null</c> if the payload is raw bytes. Used by the evaluator to check
    /// for disposable intermediate handles without allocating a new wrapper.
    /// </summary>
    internal ImageHandle? TryGetOwnedImageHandle()
    {
        return ReferenceStore.Current().Get(_referenceIndex) as ImageHandle;
    }

    /// <summary>Returns the calendar date payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DateOnly AsDate()
    {
        ThrowIfNullOrWrongKind(DataKind.Date);
        return DateOnly.FromDayNumber((int)_numericBits);
    }

    /// <summary>Returns the date and time payload with UTC offset.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DateTimeOffset AsDateTime()
    {
        ThrowIfNullOrWrongKind(DataKind.DateTime);
        return new DateTimeOffset(_numericBits, new TimeSpan(_bits1 * TimeSpan.TicksPerMinute));
    }

    /// <summary>Returns the raw JSON string payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public string AsJsonValue()
    {
        ThrowIfNullOrWrongKind(DataKind.JsonValue);
        return ReferenceStore.Current().Get<string>(_referenceIndex);
    }

    /// <summary>Returns the UUID payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public Guid AsUuid()
    {
        ThrowIfNullOrWrongKind(DataKind.Uuid);
        Guid result = default;
        Unsafe.As<Guid, long>(ref result) = _numericBits;
        Unsafe.Add(ref Unsafe.As<Guid, long>(ref result), 1) = _bits1;
        return result;
    }

    /// <summary>Returns the boolean payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public bool AsBoolean()
    {
        ThrowIfNullOrWrongKind(DataKind.Boolean);
        return _numericBits != 0L;
    }

    /// <summary>Returns the time-of-day payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public TimeOnly AsTime()
    {
        ThrowIfNullOrWrongKind(DataKind.Time);
        return new TimeOnly(_numericBits);
    }

    /// <summary>Returns the duration payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public TimeSpan AsDuration()
    {
        ThrowIfNullOrWrongKind(DataKind.Duration);
        return new TimeSpan(_numericBits);
    }

    /// <summary>Returns the <see cref="DataKind"/> that this type tag describes.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DataKind AsType()
    {
        ThrowIfNullOrWrongKind(DataKind.Type);
        return (DataKind)(byte)_numericBits;
    }

    /// <summary>Returns the typed array payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DataValue[] AsArray()
    {
        ThrowIfNullOrWrongKind(DataKind.Array);
        return ReferenceStore.Current().Get<DataValue[]>(_referenceIndex);
    }

    /// <summary>
    /// Returns the element <see cref="DataKind"/> for an <see cref="DataKind.Array"/> value.
    /// Available on both null and non-null array values.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on a non-array value.</exception>
    public DataKind ArrayElementKind
    {
        get
        {
            if (_kind != DataKind.Array)
            {
                throw new InvalidOperationException(
                    $"Cannot read ArrayElementKind on a {_kind} value.");
            }

            return (DataKind)_meta;
        }
    }

    /// <summary>Returns the positional field-value array for a struct value.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DataValue[] AsStruct()
    {
        ThrowIfNullOrWrongKind(DataKind.Struct);
        return ReferenceStore.Current().Get<DataValue[]>(_referenceIndex);
    }

    /// <summary>
    /// Returns the declared field count for a <see cref="DataKind.Struct"/> value.
    /// Available on both null and non-null struct values.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on a non-struct value.</exception>
    public short StructFieldCount
    {
        get
        {
            if (_kind != DataKind.Struct)
            {
                throw new InvalidOperationException(
                    $"Cannot read StructFieldCount on a {_kind} value.");
            }

            return _meta;
        }
    }

    // ───────────────────── Zero-copy conversions ──────────────────────

    /// <summary>
    /// Converts a <see cref="DataKind.Vector"/> or <see cref="DataKind.Matrix"/> to a
    /// <see cref="DataKind.Tensor"/> without copying the underlying data.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on a non-vector, non-matrix value.</exception>
    public DataValue ToTensor()
    {
        return _kind switch
        {
            DataKind.Vector =>
                FromTensor(ReferenceStore.Current().Get<float[]>(_referenceIndex),
                           [ReferenceStore.Current().Get<float[]>(_referenceIndex).Length]),

            DataKind.Matrix =>
                FromTensor(ReferenceStore.Current().Get<float[]>(_referenceIndex),
                           [(int)_numericBits, (int)(_numericBits >> 32)]),

            _ => throw new InvalidOperationException(
                $"Cannot convert {_kind} to Tensor. Only Vector and Matrix are supported."),
        };
    }

    /// <summary>
    /// Converts a rank-1 <see cref="DataKind.Tensor"/> back to a <see cref="DataKind.Vector"/>
    /// without copying the underlying data.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on a non-tensor or tensor with rank != 1.</exception>
    public DataValue ToVector()
    {
        ThrowIfNullOrWrongKind(DataKind.Tensor);
        ReferenceStore store = ReferenceStore.Current();
        int[] shape = store.Get<int[]>(_referenceIndex + 1);

        if (shape.Length != 1)
        {
            throw new InvalidOperationException(
                $"Cannot convert rank-{shape.Length} tensor to Vector. Rank must be 1.");
        }

        return FromVector(store.Get<float[]>(_referenceIndex));
    }

    /// <summary>
    /// Converts a rank-2 <see cref="DataKind.Tensor"/> back to a <see cref="DataKind.Matrix"/>
    /// without copying the underlying data.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on a non-tensor or tensor with rank != 2.</exception>
    public DataValue ToMatrix()
    {
        ThrowIfNullOrWrongKind(DataKind.Tensor);
        ReferenceStore store = ReferenceStore.Current();
        int[] shape = store.Get<int[]>(_referenceIndex + 1);

        if (shape.Length != 2)
        {
            throw new InvalidOperationException(
                $"Cannot convert rank-{shape.Length} tensor to Matrix. Rank must be 2.");
        }

        return FromMatrix(store.Get<float[]>(_referenceIndex), shape[0], shape[1]);
    }

    // ───────────────────────── Equality ─────────────────────────

    /// <inheritdoc/>
    public override bool Equals(object? other) => other is DataValue dv && Equals(dv);

    /// <inheritdoc/>
    public bool Equals(DataValue other)
    {
        if (_kind != other._kind) return false;
        if (IsNull && other.IsNull) return true;
        if (IsNull != other.IsNull) return false;

        return _kind switch
        {
            // Unknown sentinel: no payload, so non-null Unknown values are always equal.
            DataKind.Unknown => true,

            // Fixed-size integer types: compare bits directly (no -0 ambiguity for integers).
            DataKind.UInt8 or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Boolean or DataKind.Date or DataKind.Time or DataKind.Duration
            or DataKind.Type
                => _numericBits == other._numericBits,

            // Float types: recover the actual float so IEEE semantics (NaN != NaN, -0 == 0) are preserved.
            DataKind.Float32
                => BitConverter.Int32BitsToSingle((int)_numericBits) == BitConverter.Int32BitsToSingle((int)other._numericBits),
            DataKind.Float64
                => BitConverter.Int64BitsToDouble(_numericBits) == BitConverter.Int64BitsToDouble(other._numericBits),

            // Reference types:
            DataKind.String or DataKind.JsonValue
                => CompareStrings(in this, in other),
            DataKind.Uuid
                => _numericBits == other._numericBits && _bits1 == other._bits1,
            DataKind.DateTime
                => _numericBits == other._numericBits && _bits1 == other._bits1,
            DataKind.Vector
                => ReferenceStore.Current().Get<float[]>(_referenceIndex).AsSpan()
                       .SequenceEqual(ReferenceStore.Current().Get<float[]>(other._referenceIndex)),
            DataKind.Matrix
                => _numericBits == other._numericBits
                   && ReferenceStore.Current().Get<float[]>(_referenceIndex).AsSpan()
                          .SequenceEqual(ReferenceStore.Current().Get<float[]>(other._referenceIndex)),
            DataKind.Tensor
                => CompareTensors(in this, in other),
            DataKind.UInt8Array
                => ReferenceStore.Current().Get<byte[]>(_referenceIndex).AsSpan()
                       .SequenceEqual(ReferenceStore.Current().Get<byte[]>(other._referenceIndex)),
            DataKind.Image
                => AsImage().AsSpan().SequenceEqual(other.AsImage()),
            DataKind.Array
                => _meta == other._meta
                   && ReferenceStore.Current().Get<DataValue[]>(_referenceIndex).AsSpan()
                          .SequenceEqual(ReferenceStore.Current().Get<DataValue[]>(other._referenceIndex)),
            DataKind.Struct
                => _meta == other._meta
                   && ReferenceStore.Current().Get<DataValue[]>(_referenceIndex).AsSpan()
                          .SequenceEqual(ReferenceStore.Current().Get<DataValue[]>(other._referenceIndex)),
            _ => false,
        };
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        if (IsNull) return HashCode.Combine(_kind, true);

        return _kind switch
        {
            // Fixed-size integer types: hash bits directly.
            DataKind.UInt8 or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Boolean or DataKind.Date or DataKind.Time or DataKind.Duration
            or DataKind.Type
                => HashCode.Combine(_kind, _numericBits),

            // Float types: delegate to float/double GetHashCode so -0 == 0 hashing is preserved.
            DataKind.Float32
                => HashCode.Combine(_kind, BitConverter.Int32BitsToSingle((int)_numericBits)),
            DataKind.Float64
                => HashCode.Combine(_kind, BitConverter.Int64BitsToDouble(_numericBits)),

            // Reference types:
            DataKind.String or DataKind.JsonValue
                => (_flags & FlagHasReference) != 0
                    ? HashCode.Combine(_kind, ReferenceStore.Current().Get<string>(_referenceIndex))
                    : HashCode.Combine(_kind, _numericBits, _bits1),
            DataKind.DateTime
                => HashCode.Combine(_kind, _numericBits, _bits1),
            DataKind.Uuid
                => HashCode.Combine(_kind, _numericBits, _bits1),
            DataKind.Vector
                => CombineFloatArrayHash(_kind, ReferenceStore.Current().Get<float[]>(_referenceIndex)),
            DataKind.Matrix
                => CombineFloatArrayHash(_kind, ReferenceStore.Current().Get<float[]>(_referenceIndex), _numericBits),
            DataKind.Tensor
                => CombineTensorHash(_kind, _referenceIndex),
            DataKind.UInt8Array
                => CombineByteArrayHash(_kind, ReferenceStore.Current().Get<byte[]>(_referenceIndex)),
            DataKind.Image
                => CombineByteArrayHash(_kind, AsImage()),
            DataKind.Array
                => CombineArrayHash(_kind, ReferenceStore.Current().Get<DataValue[]>(_referenceIndex), _meta),
            DataKind.Struct
                => CombineArrayHash(_kind, ReferenceStore.Current().Get<DataValue[]>(_referenceIndex), _meta),
            _ => HashCode.Combine(_kind),
        };
    }

    /// <inheritdoc/>
    public static bool operator ==(DataValue left, DataValue right) => left.Equals(right);

    /// <inheritdoc/>
    public static bool operator !=(DataValue left, DataValue right) => !left.Equals(right);

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// Compares two string or JSON values, handling the case where one or both
    /// are arena-backed (no reference in the store). Arena-backed values from the
    /// same batch share offset/length identity; cross-arena comparison requires
    /// materialisation before calling <see cref="Equals(DataValue)"/>.
    /// </summary>
    private static bool CompareStrings(in DataValue left, in DataValue right)
    {
        bool leftArena = (left._flags & FlagHasReference) == 0;
        bool rightArena = (right._flags & FlagHasReference) == 0;

        if (!leftArena && !rightArena)
        {
            ReferenceStore store = ReferenceStore.Current();
            return (string)store.Get(left._referenceIndex) == (string)store.Get(right._referenceIndex);
        }

        if (leftArena && rightArena)
        {
            // Same arena, same offset+length → same content.
            return left._numericBits == right._numericBits && left._bits1 == right._bits1;
        }

        // Mixed arena/non-arena: cannot compare without arena context.
        // Materialise before comparing.
        return false;
    }

    /// <summary>
    /// Compares two tensor values by checking both shape and data arrays from
    /// the <see cref="ReferenceStore"/>.
    /// </summary>
    private static bool CompareTensors(in DataValue left, in DataValue right)
    {
        ReferenceStore store = ReferenceStore.Current();
        int[] leftShape = store.Get<int[]>(left._referenceIndex + 1);
        int[] rightShape = store.Get<int[]>(right._referenceIndex + 1);

        if (!leftShape.AsSpan().SequenceEqual(rightShape))
        {
            return false;
        }

        return store.Get<float[]>(left._referenceIndex).AsSpan()
                   .SequenceEqual(store.Get<float[]>(right._referenceIndex));
    }

    private void ThrowIfNullOrWrongKind(DataKind expected)
    {
        if ((_flags & FlagIsNull) != 0)
        {
            throw new InvalidOperationException(
                $"Cannot read a null {_kind} value.");
        }

        if (_kind != expected)
        {
            throw new InvalidOperationException(
                $"Cannot read {_kind} as {expected}.");
        }
    }

    private static string FormatStructFields(DataValue[] fields)
    {
        return string.Join(", ", (IEnumerable<DataValue>)fields);
    }

    private static int CombineFloatArrayHash(DataKind kind, float[] data)
    {
        HashCode hash = new();
        hash.Add(kind);

        foreach (float element in data)
        {
            hash.Add(element);
        }

        return hash.ToHashCode();
    }

    private static int CombineFloatArrayHash(DataKind kind, float[] data, long shapeBits)
    {
        HashCode hash = new();
        hash.Add(kind);
        hash.Add((int)shapeBits);
        hash.Add((int)(shapeBits >> 32));

        foreach (float element in data)
        {
            hash.Add(element);
        }

        return hash.ToHashCode();
    }

    private static int CombineTensorHash(DataKind kind, int referenceIndex)
    {
        ReferenceStore store = ReferenceStore.Current();
        float[] data = store.Get<float[]>(referenceIndex);
        int[] shape = store.Get<int[]>(referenceIndex + 1);

        HashCode hash = new();
        hash.Add(kind);

        foreach (int dimension in shape)
        {
            hash.Add(dimension);
        }

        foreach (float element in data)
        {
            hash.Add(element);
        }

        return hash.ToHashCode();
    }

    private static int CombineByteArrayHash(DataKind kind, byte[] data)
    {
        HashCode hash = new();
        hash.Add(kind);

        foreach (byte element in data)
        {
            hash.Add(element);
        }

        return hash.ToHashCode();
    }

    private static int CombineArrayHash(DataKind kind, DataValue[] elements, short elementKindMeta)
    {
        HashCode hash = new();
        hash.Add(kind);
        hash.Add(elementKindMeta);

        foreach (DataValue element in elements)
        {
            hash.Add(element);
        }

        return hash.ToHashCode();
    }

    // ───────────────────────── Display ─────────────────────────

    /// <inheritdoc/>
    public override string ToString()
    {
        if (IsNull) return $"NULL({_kind})";

        return _kind switch
        {
            DataKind.Float32 => BitConverter.Int32BitsToSingle((int)_numericBits).ToString("G"),
            DataKind.UInt8 => ((byte)_numericBits).ToString(),
            DataKind.Int8 => ((sbyte)_numericBits).ToString(),
            DataKind.Int16 => ((short)_numericBits).ToString(),
            DataKind.UInt16 => ((ushort)_numericBits).ToString(),
            DataKind.Int32 => ((int)_numericBits).ToString(),
            DataKind.UInt32 => ((uint)_numericBits).ToString(),
            DataKind.Int64 => _numericBits.ToString(),
            DataKind.UInt64 => unchecked((ulong)_numericBits).ToString(),
            DataKind.Float64 => BitConverter.Int64BitsToDouble(_numericBits).ToString("G"),
            DataKind.String => (_flags & FlagHasReference) != 0
                ? ReferenceStore.Current().Get<string>(_referenceIndex)
                : $"String[arena@{_numericBits}+{_bits1}]",
            DataKind.Date => DateOnly.FromDayNumber((int)_numericBits).ToString("yyyy-MM-dd"),
            DataKind.DateTime => AsDateTime().ToString("O"),
            DataKind.JsonValue => (_flags & FlagHasReference) != 0
                ? ReferenceStore.Current().Get<string>(_referenceIndex)
                : $"JsonValue[arena@{_numericBits}+{_bits1}]",
            DataKind.Uuid => AsUuid().ToString("D"),
            DataKind.Boolean => _numericBits != 0L ? "true" : "false",
            DataKind.Time => new TimeOnly(_numericBits).ToString("HH:mm:ss.FFFFFFF"),
            DataKind.Duration => new TimeSpan(_numericBits).ToString("c"),
            DataKind.Vector => $"Vector[{ReferenceStore.Current().Get<float[]>(_referenceIndex).Length}]",
            DataKind.Matrix => $"Matrix[{(int)_numericBits}x{(int)(_numericBits >> 32)}]",
            DataKind.Tensor => $"Tensor[{string.Join("x", ReferenceStore.Current().Get<int[]>(_referenceIndex + 1))}]",
            DataKind.UInt8Array => $"UInt8Array[{ReferenceStore.Current().Get<byte[]>(_referenceIndex).Length}]",
            DataKind.Image => $"Image[{AsImage().Length} bytes]",
            DataKind.Array => $"Array<{(DataKind)_meta}>[{ReferenceStore.Current().Get<DataValue[]>(_referenceIndex).Length}]",
            DataKind.Struct => $"Struct({_meta}){{{FormatStructFields(ReferenceStore.Current().Get<DataValue[]>(_referenceIndex))}}}",
            _ => _kind.ToString(),
        };
    }
}

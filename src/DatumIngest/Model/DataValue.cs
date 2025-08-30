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
/// The internal layout uses two fields for the payload rather than a single <c>object?</c>:
/// </para>
/// <list type="bullet">
///   <item><c>_numericBits</c> — stores all fixed-size primitives (Float32/64, integers,
///     Boolean, Date, Time, Duration) without boxing, using the raw bit representation.</item>
///   <item><c>_reference</c> — stores reference-type payloads (String, JsonValue, float[],
///     byte[], DataValue[], ImageHandle) and large value types (Guid, DateTimeOffset) boxed once.</item>
/// </list>
/// <para>
/// This means <see cref="DataValue"/> stored in arrays is fully inlined with zero per-element
/// heap allocation for all numeric and temporal kinds.
/// </para>
/// <para>
/// <c>default(DataValue)</c> is equivalent to <c>DataValue.FromUInt8(0)</c>
/// (<see cref="DataKind.UInt8"/> = 0, not null). Always use factory methods or <see cref="Null"/>
/// to construct intentional values.
/// </para>
/// </remarks>
public readonly struct DataValue : IEquatable<DataValue>
{
    private readonly DataKind _kind;
    private readonly bool _isNull;

    // Stores all fixed-size primitive types without boxing:
    //   Float32   → BitConverter.SingleToInt32Bits(value) cast to long
    //   Float64   → BitConverter.DoubleToInt64Bits(value)
    //   UInt8     → (long)value
    //   Int8      → (long)value
    //   Int16     → (long)value
    //   UInt16    → (long)value
    //   Int32     → (long)value
    //   UInt32    → (long)value
    //   Int64     → value
    //   UInt64    → unchecked((long)value)
    //   Boolean   → 1L (true) or 0L (false)
    //   Date      → (long)value.DayNumber
    //   Time      → value.Ticks
    //   Duration  → value.Ticks
    //   Uuid (low half)    → low 8 bytes of the 128-bit Guid
    //   DateTime (ticks)   → DateTimeOffset.Ticks (local time)
    private readonly long _numericBits;

    // Second value slot for types that need more than 8 bytes of inline storage:
    //   Uuid (high half)   → high 8 bytes of the 128-bit Guid
    //   DateTime (offset)  → UTC offset in minutes
    private readonly long _bits1;

    // Stores reference-type payloads:
    //   String, JsonValue  → string (no extra allocation)
    //   Vector             → float[] (no extra allocation)
    //   Matrix, Tensor     → float[] (shape in _shape)
    //   UInt8Array         → byte[] (no extra allocation)
    //   Image              → byte[] or ImageHandle (no extra allocation)
    //   Array              → DataValue[] (element kind in _shape[0])
    private readonly object? _reference;

    // Shape metadata:
    //   Matrix  → [rows, cols]
    //   Tensor  → [d0, d1, ...]
    //   Array   → [(int)elementKind]
    private readonly int[]? _shape;

    private DataValue(DataKind kind, long numericBits, object? reference, int[]? shape, bool isNull, long bits1 = 0L)
    {
        _kind = kind;
        _isNull = isNull;
        _numericBits = numericBits;
        _bits1 = bits1;
        _reference = reference;
        _shape = shape;
    }

    /// <summary>The type discriminator for this value.</summary>
    public DataKind Kind => _kind;

    /// <summary>Whether this value represents a typed null.</summary>
    public bool IsNull => _isNull;

    // ───────────────────────── Cached common instances ─────────────────────────

    /// <summary>Cached Float32 0 — returned by boolean-false results to avoid per-evaluation allocation.</summary>
    private static readonly DataValue Float32Zero = new(DataKind.Float32, numericBits: 0L, reference: null, shape: null, isNull: false);

    /// <summary>Cached Float32 1 — returned by boolean-true results to avoid per-evaluation allocation.</summary>
    private static readonly DataValue Float32One = new(DataKind.Float32, numericBits: BitConverter.SingleToInt32Bits(1f), reference: null, shape: null, isNull: false);

    /// <summary>Cached null Float32 — the most common null kind in expression evaluation.</summary>
    private static readonly DataValue NullFloat32 = new(DataKind.Float32, numericBits: 0L, reference: null, shape: null, isNull: true);

    /// <summary>Cached null Int32 — common for integer column nulls.</summary>
    private static readonly DataValue NullInt32 = new(DataKind.Int32, numericBits: 0L, reference: null, shape: null, isNull: true);

    /// <summary>Cached null Int64 — common for integer column nulls.</summary>
    private static readonly DataValue NullInt64 = new(DataKind.Int64, numericBits: 0L, reference: null, shape: null, isNull: true);

    /// <summary>Cached null Float64 — common for double-precision column nulls.</summary>
    private static readonly DataValue NullFloat64 = new(DataKind.Float64, numericBits: 0L, reference: null, shape: null, isNull: true);

    /// <summary>Cached boolean true — avoids per-evaluation allocation for boolean results.</summary>
    private static readonly DataValue BooleanTrue = new(DataKind.Boolean, numericBits: 1L, reference: null, shape: null, isNull: false);

    /// <summary>Cached boolean false — avoids per-evaluation allocation for boolean results.</summary>
    private static readonly DataValue BooleanFalse = new(DataKind.Boolean, numericBits: 0L, reference: null, shape: null, isNull: false);

    // ───────────────────────── Factory methods ─────────────────────────

    /// <summary>Creates a value from a 32-bit floating-point number.</summary>
    public static DataValue FromFloat32(float value)
    {
        // Reuse cached instances for the two most common boolean-result values.
        if (value == 0f) return Float32Zero;
        if (value == 1f) return Float32One;
        return new(DataKind.Float32, numericBits: BitConverter.SingleToInt32Bits(value), reference: null, shape: null, isNull: false);
    }

    /// <summary>Creates a value from an unsigned 8-bit integer.</summary>
    public static DataValue FromUInt8(byte value) =>
        new(DataKind.UInt8, numericBits: value, reference: null, shape: null, isNull: false);

    /// <summary>Creates a value from a signed 8-bit integer.</summary>
    public static DataValue FromInt8(sbyte value) =>
        new(DataKind.Int8, numericBits: value, reference: null, shape: null, isNull: false);

    /// <summary>Creates a value from a signed 16-bit integer.</summary>
    public static DataValue FromInt16(short value) =>
        new(DataKind.Int16, numericBits: value, reference: null, shape: null, isNull: false);

    /// <summary>Creates a value from an unsigned 16-bit integer.</summary>
    public static DataValue FromUInt16(ushort value) =>
        new(DataKind.UInt16, numericBits: value, reference: null, shape: null, isNull: false);

    /// <summary>Creates a value from a signed 32-bit integer.</summary>
    public static DataValue FromInt32(int value) =>
        new(DataKind.Int32, numericBits: value, reference: null, shape: null, isNull: false);

    /// <summary>Creates a value from an unsigned 32-bit integer.</summary>
    public static DataValue FromUInt32(uint value) =>
        new(DataKind.UInt32, numericBits: value, reference: null, shape: null, isNull: false);

    /// <summary>Creates a value from a signed 64-bit integer.</summary>
    public static DataValue FromInt64(long value) =>
        new(DataKind.Int64, numericBits: value, reference: null, shape: null, isNull: false);

    /// <summary>Creates a value from an unsigned 64-bit integer.</summary>
    public static DataValue FromUInt64(ulong value) =>
        new(DataKind.UInt64, numericBits: unchecked((long)value), reference: null, shape: null, isNull: false);

    /// <summary>Creates a value from a 64-bit double-precision floating-point number.</summary>
    public static DataValue FromFloat64(double value) =>
        new(DataKind.Float64, numericBits: BitConverter.DoubleToInt64Bits(value), reference: null, shape: null, isNull: false);

    /// <summary>Creates a value from a byte array.</summary>
    public static DataValue FromUInt8Array(byte[] value) =>
        new(DataKind.UInt8Array, numericBits: 0L, reference: value, shape: null, isNull: false);

    /// <summary>Creates a value from a text string.</summary>
    public static DataValue FromString(string value) =>
        new(DataKind.String, numericBits: 0L, reference: value, shape: null, isNull: false);

    /// <summary>Creates a rank-1 tensor (vector) from a float array.</summary>
    public static DataValue FromVector(float[] value) =>
        new(DataKind.Vector, numericBits: 0L, reference: value, shape: null, isNull: false);

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

        return new DataValue(DataKind.Matrix, numericBits: 0L, reference: data, shape: [rows, columns], isNull: false);
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

        return new DataValue(DataKind.Tensor, numericBits: 0L, reference: data, shape: shape, isNull: false);
    }

    /// <summary>Creates a value from encoded image bytes.</summary>
    public static DataValue FromImage(byte[] value) =>
        new(DataKind.Image, numericBits: 0L, reference: value, shape: null, isNull: false);

    /// <summary>
    /// Creates a value from an <see cref="ImageHandle"/>.
    /// The handle carries a decoded bitmap and/or encoded bytes, enabling
    /// fused image pipelines that avoid redundant decode/encode cycles.
    /// </summary>
    internal static DataValue FromImageHandle(ImageHandle handle) =>
        new(DataKind.Image, numericBits: 0L, reference: handle, shape: null, isNull: false);

    /// <summary>Creates a value from a calendar date.</summary>
    public static DataValue FromDate(DateOnly value) =>
        new(DataKind.Date, numericBits: value.DayNumber, reference: null, shape: null, isNull: false);

    /// <summary>Creates a value from a date and time with UTC offset.</summary>
    public static DataValue FromDateTime(DateTimeOffset value) =>
        new(DataKind.DateTime, numericBits: value.Ticks, reference: null, shape: null, isNull: false,
            bits1: value.Offset.Ticks / TimeSpan.TicksPerMinute);

    /// <summary>Creates a value from a raw JSON string.</summary>
    public static DataValue FromJsonValue(string value) =>
        new(DataKind.JsonValue, numericBits: 0L, reference: value, shape: null, isNull: false);

    /// <summary>Creates a value from a 128-bit universally unique identifier.</summary>
    public static DataValue FromUuid(Guid value)
    {
        ref long pair = ref Unsafe.As<Guid, long>(ref value);
        return new(DataKind.Uuid, numericBits: pair, reference: null, shape: null, isNull: false,
                   bits1: Unsafe.Add(ref pair, 1));
    }

    /// <summary>Creates a boolean value.</summary>
    public static DataValue FromBoolean(bool value) =>
        value ? BooleanTrue : BooleanFalse;

    /// <summary>Creates a value from a time-of-day.</summary>
    public static DataValue FromTime(TimeOnly value) =>
        new(DataKind.Time, numericBits: value.Ticks, reference: null, shape: null, isNull: false);

    /// <summary>Creates a value from a duration (elapsed time span).</summary>
    public static DataValue FromDuration(TimeSpan value) =>
        new(DataKind.Duration, numericBits: value.Ticks, reference: null, shape: null, isNull: false);

    /// <summary>
    /// Creates a typed array value from an element kind and an array of elements.
    /// The element kind is stored in the shape metadata so it can be recovered at runtime.
    /// </summary>
    /// <param name="elementKind">The <see cref="DataKind"/> shared by all elements.</param>
    /// <param name="elements">The array of element values.</param>
    public static DataValue FromArray(DataKind elementKind, DataValue[] elements) =>
        new(DataKind.Array, numericBits: 0L, reference: elements, shape: [(int)elementKind], isNull: false);

    /// <summary>Creates a typed null array with the given element kind.</summary>
    /// <param name="elementKind">The element kind of the null array.</param>
    public static DataValue NullArray(DataKind elementKind) =>
        new(DataKind.Array, numericBits: 0L, reference: null, shape: [(int)elementKind], isNull: true);

    /// <summary>Creates a typed null value.</summary>
    public static DataValue Null(DataKind kind)
    {
        return kind switch
        {
            DataKind.Float32 => NullFloat32,
            DataKind.Int32 => NullInt32,
            DataKind.Int64 => NullInt64,
            DataKind.Float64 => NullFloat64,
            _ => new(kind, numericBits: 0L, reference: null, shape: null, isNull: true),
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

    /// <summary>Returns the byte array payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public byte[] AsUInt8Array()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt8Array);
        return (byte[])_reference!;
    }

    /// <summary>Returns the text string payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public string AsString()
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        return (string)_reference!;
    }

    /// <summary>Returns the vector (rank-1) float array payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public float[] AsVector()
    {
        ThrowIfNullOrWrongKind(DataKind.Vector);
        return (float[])_reference!;
    }

    /// <summary>Returns the matrix (rank-2) flat float array and its dimensions.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public float[] AsMatrix(out int rows, out int columns)
    {
        ThrowIfNullOrWrongKind(DataKind.Matrix);
        rows = _shape![0];
        columns = _shape[1];
        return (float[])_reference!;
    }

    /// <summary>Returns the tensor flat float array and its shape.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public float[] AsTensor(out int[] shape)
    {
        ThrowIfNullOrWrongKind(DataKind.Tensor);
        shape = _shape!;
        return (float[])_reference!;
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

        if (_reference is ImageHandle handle)
        {
            return handle.GetEncodedBytes();
        }

        return (byte[])_reference!;
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

        if (_reference is ImageHandle handle)
        {
            return handle;
        }

        byte[] bytes = (byte[])_reference!;
        return new ImageHandle(bytes, ImageEncoder.ResolveFormat(bytes, formatOverride: null));
    }

    /// <summary>
    /// Returns the <see cref="ImageHandle"/> payload if this value already owns one,
    /// or <c>null</c> if the payload is raw bytes. Used by the evaluator to check
    /// for disposable intermediate handles without allocating a new wrapper.
    /// </summary>
    internal ImageHandle? TryGetOwnedImageHandle()
    {
        return _reference as ImageHandle;
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
        return (string)_reference!;
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

    /// <summary>Returns the typed array payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DataValue[] AsArray()
    {
        ThrowIfNullOrWrongKind(DataKind.Array);
        return (DataValue[])_reference!;
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

            return (DataKind)_shape![0];
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
            DataKind.Vector => new DataValue(
                DataKind.Tensor,
                numericBits: 0L,
                reference: _reference,
                shape: [((float[])_reference!).Length],
                isNull: false),

            DataKind.Matrix => new DataValue(
                DataKind.Tensor,
                numericBits: 0L,
                reference: _reference,
                shape: _shape,
                isNull: false),

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

        if (_shape!.Length != 1)
        {
            throw new InvalidOperationException(
                $"Cannot convert rank-{_shape.Length} tensor to Vector. Rank must be 1.");
        }

        return new DataValue(DataKind.Vector, numericBits: 0L, reference: _reference, shape: null, isNull: false);
    }

    /// <summary>
    /// Converts a rank-2 <see cref="DataKind.Tensor"/> back to a <see cref="DataKind.Matrix"/>
    /// without copying the underlying data.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on a non-tensor or tensor with rank != 2.</exception>
    public DataValue ToMatrix()
    {
        ThrowIfNullOrWrongKind(DataKind.Tensor);

        if (_shape!.Length != 2)
        {
            throw new InvalidOperationException(
                $"Cannot convert rank-{_shape.Length} tensor to Matrix. Rank must be 2.");
        }

        return new DataValue(DataKind.Matrix, numericBits: 0L, reference: _reference, shape: _shape, isNull: false);
    }

    // ───────────────────────── Equality ─────────────────────────

    /// <inheritdoc/>
    public override bool Equals(object? other) => other is DataValue dv && Equals(dv);

    /// <inheritdoc/>
    public bool Equals(DataValue other)
    {
        if (_kind != other._kind) return false;
        if (_isNull && other._isNull) return true;
        if (_isNull != other._isNull) return false;

        return _kind switch
        {
            // Fixed-size integer types: compare bits directly (no -0 ambiguity for integers).
            DataKind.UInt8 or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Boolean or DataKind.Date or DataKind.Time or DataKind.Duration
                => _numericBits == other._numericBits,

            // Float types: recover the actual float so IEEE semantics (NaN != NaN, -0 == 0) are preserved.
            DataKind.Float32
                => BitConverter.Int32BitsToSingle((int)_numericBits) == BitConverter.Int32BitsToSingle((int)other._numericBits),
            DataKind.Float64
                => BitConverter.Int64BitsToDouble(_numericBits) == BitConverter.Int64BitsToDouble(other._numericBits),

            // Reference types:
            DataKind.String or DataKind.JsonValue
                => (string)_reference! == (string)other._reference!,
            DataKind.Uuid
                => _numericBits == other._numericBits && _bits1 == other._bits1,
            DataKind.DateTime
                => _numericBits == other._numericBits && _bits1 == other._bits1,
            DataKind.Vector
                => ((float[])_reference!).AsSpan().SequenceEqual((float[])other._reference!),
            DataKind.Matrix
                => _shape!.AsSpan().SequenceEqual(other._shape!)
                   && ((float[])_reference!).AsSpan().SequenceEqual((float[])other._reference!),
            DataKind.Tensor
                => _shape!.AsSpan().SequenceEqual(other._shape!)
                   && ((float[])_reference!).AsSpan().SequenceEqual((float[])other._reference!),
            DataKind.UInt8Array
                => ((byte[])_reference!).AsSpan().SequenceEqual((byte[])other._reference!),
            DataKind.Image
                => AsImage().AsSpan().SequenceEqual(other.AsImage()),
            DataKind.Array
                => _shape!.AsSpan().SequenceEqual(other._shape!)
                   && ((DataValue[])_reference!).AsSpan().SequenceEqual((DataValue[])other._reference!),
            _ => false,
        };
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        if (_isNull) return HashCode.Combine(_kind, true);

        return _kind switch
        {
            // Fixed-size integer types: hash bits directly.
            DataKind.UInt8 or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Boolean or DataKind.Date or DataKind.Time or DataKind.Duration
                => HashCode.Combine(_kind, _numericBits),

            // Float types: delegate to float/double GetHashCode so -0 == 0 hashing is preserved.
            DataKind.Float32
                => HashCode.Combine(_kind, BitConverter.Int32BitsToSingle((int)_numericBits)),
            DataKind.Float64
                => HashCode.Combine(_kind, BitConverter.Int64BitsToDouble(_numericBits)),

            // Reference types:
            DataKind.String or DataKind.JsonValue
                => HashCode.Combine(_kind, (string)_reference!),
            DataKind.DateTime
                => HashCode.Combine(_kind, _numericBits, _bits1),
            DataKind.Uuid
                => HashCode.Combine(_kind, _numericBits, _bits1),
            DataKind.Vector
                => CombineFloatArrayHash(_kind, (float[])_reference!, _shape),
            DataKind.Matrix
                => CombineFloatArrayHash(_kind, (float[])_reference!, _shape),
            DataKind.Tensor
                => CombineFloatArrayHash(_kind, (float[])_reference!, _shape),
            DataKind.UInt8Array
                => CombineByteArrayHash(_kind, (byte[])_reference!),
            DataKind.Image
                => CombineByteArrayHash(_kind, AsImage()),
            DataKind.Array
                => CombineArrayHash(_kind, (DataValue[])_reference!, _shape),
            _ => HashCode.Combine(_kind),
        };
    }

    /// <inheritdoc/>
    public static bool operator ==(DataValue left, DataValue right) => left.Equals(right);

    /// <inheritdoc/>
    public static bool operator !=(DataValue left, DataValue right) => !left.Equals(right);

    // ───────────────────────── Helpers ─────────────────────────

    private void ThrowIfNullOrWrongKind(DataKind expected)
    {
        if (_isNull)
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

    private static int CombineFloatArrayHash(DataKind kind, float[] data, int[]? shape)
    {
        HashCode hash = new();
        hash.Add(kind);

        if (shape is not null)
        {
            foreach (int dimension in shape)
            {
                hash.Add(dimension);
            }
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

    private static int CombineArrayHash(DataKind kind, DataValue[] elements, int[]? shape)
    {
        HashCode hash = new();
        hash.Add(kind);

        if (shape is not null)
        {
            foreach (int dimension in shape)
            {
                hash.Add(dimension);
            }
        }

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
        if (_isNull) return $"NULL({_kind})";

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
            DataKind.String => (string)_reference!,
            DataKind.Date => DateOnly.FromDayNumber((int)_numericBits).ToString("yyyy-MM-dd"),
            DataKind.DateTime => AsDateTime().ToString("O"),
            DataKind.JsonValue => (string)_reference!,
            DataKind.Uuid => AsUuid().ToString("D"),
            DataKind.Boolean => _numericBits != 0L ? "true" : "false",
            DataKind.Time => new TimeOnly(_numericBits).ToString("HH:mm:ss.FFFFFFF"),
            DataKind.Duration => new TimeSpan(_numericBits).ToString("c"),
            DataKind.Vector => $"Vector[{((float[])_reference!).Length}]",
            DataKind.Matrix => $"Matrix[{_shape![0]}x{_shape[1]}]",
            DataKind.Tensor => $"Tensor[{string.Join("x", _shape!)}]",
            DataKind.UInt8Array => $"UInt8Array[{((byte[])_reference!).Length}]",
            DataKind.Image => $"Image[{AsImage().Length} bytes]",
            DataKind.Array => $"Array<{(DataKind)_shape![0]}>[{((DataValue[])_reference!).Length}]",
            _ => _kind.ToString(),
        };
    }
}

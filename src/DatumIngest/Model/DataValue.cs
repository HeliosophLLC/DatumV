using DatumIngest.Functions.Image;

namespace DatumIngest.Model;

/// <summary>
/// An immutable, discriminated union value that carries typed data through the query pipeline.
/// Use the static factory methods (<see cref="FromFloat32"/>, <see cref="FromVector"/>, etc.)
/// to construct instances and the accessor methods to retrieve typed payloads.
/// </summary>
public sealed class DataValue : IEquatable<DataValue>
{
    private readonly DataKind _kind;
    private readonly bool _isNull;

    // Union payload — exactly one field is meaningful depending on _kind.
    private readonly object? _payload;

    // Shape metadata for Matrix ([rows, cols]) and Tensor (arbitrary rank).
    private readonly int[]? _shape;

    private DataValue(DataKind kind, object? payload, int[]? shape, bool isNull)
    {
        _kind = kind;
        _payload = payload;
        _shape = shape;
        _isNull = isNull;
    }

    /// <summary>The type discriminator for this value.</summary>
    public DataKind Kind => _kind;

    /// <summary>Whether this value represents a typed null.</summary>
    public bool IsNull => _isNull;

    // ───────────────────────── Cached common instances ─────────────────────────

    /// <summary>Cached Float32 0 — returned by all boolean-false results to avoid per-evaluation allocation.</summary>
    private static readonly DataValue Float32Zero = new(DataKind.Float32, 0f, shape: null, isNull: false);

    /// <summary>Cached Float32 1 — returned by all boolean-true results to avoid per-evaluation allocation.</summary>
    private static readonly DataValue Float32One = new(DataKind.Float32, 1f, shape: null, isNull: false);

    /// <summary>Cached null Float32 — the most common null kind in expression evaluation.</summary>
    private static readonly DataValue NullFloat32 = new(DataKind.Float32, payload: null, shape: null, isNull: true);

    /// <summary>Cached null Int32 — common for integer column nulls.</summary>
    private static readonly DataValue NullInt32 = new(DataKind.Int32, payload: null, shape: null, isNull: true);

    /// <summary>Cached null Int64 — common for integer column nulls.</summary>
    private static readonly DataValue NullInt64 = new(DataKind.Int64, payload: null, shape: null, isNull: true);

    /// <summary>Cached null Float64 — common for double-precision column nulls.</summary>
    private static readonly DataValue NullFloat64 = new(DataKind.Float64, payload: null, shape: null, isNull: true);

    /// <summary>Cached boolean true — avoids per-evaluation allocation for boolean results.</summary>
    private static readonly DataValue BooleanTrue = new(DataKind.Boolean, true, shape: null, isNull: false);

    /// <summary>Cached boolean false — avoids per-evaluation allocation for boolean results.</summary>
    private static readonly DataValue BooleanFalse = new(DataKind.Boolean, false, shape: null, isNull: false);

    // ───────────────────────── Factory methods ─────────────────────────

    /// <summary>Creates a value from a 32-bit floating-point number.</summary>
    public static DataValue FromFloat32(float value)
    {
        // Reuse cached instances for the two most common boolean-result values.
        if (value == 0f) return Float32Zero;
        if (value == 1f) return Float32One;
        return new(DataKind.Float32, value, shape: null, isNull: false);
    }

    /// <summary>Creates a value from an unsigned 8-bit integer.</summary>
    public static DataValue FromUInt8(byte value) =>
        new(DataKind.UInt8, value, shape: null, isNull: false);

    /// <summary>Creates a value from a signed 8-bit integer.</summary>
    public static DataValue FromInt8(sbyte value) =>
        new(DataKind.Int8, value, shape: null, isNull: false);

    /// <summary>Creates a value from a signed 16-bit integer.</summary>
    public static DataValue FromInt16(short value) =>
        new(DataKind.Int16, value, shape: null, isNull: false);

    /// <summary>Creates a value from an unsigned 16-bit integer.</summary>
    public static DataValue FromUInt16(ushort value) =>
        new(DataKind.UInt16, value, shape: null, isNull: false);

    /// <summary>Creates a value from a signed 32-bit integer.</summary>
    public static DataValue FromInt32(int value) =>
        new(DataKind.Int32, value, shape: null, isNull: false);

    /// <summary>Creates a value from an unsigned 32-bit integer.</summary>
    public static DataValue FromUInt32(uint value) =>
        new(DataKind.UInt32, value, shape: null, isNull: false);

    /// <summary>Creates a value from a signed 64-bit integer.</summary>
    public static DataValue FromInt64(long value) =>
        new(DataKind.Int64, value, shape: null, isNull: false);

    /// <summary>Creates a value from an unsigned 64-bit integer.</summary>
    public static DataValue FromUInt64(ulong value) =>
        new(DataKind.UInt64, value, shape: null, isNull: false);

    /// <summary>Creates a value from a 64-bit double-precision floating-point number.</summary>
    public static DataValue FromFloat64(double value) =>
        new(DataKind.Float64, value, shape: null, isNull: false);

    /// <summary>Creates a value from a byte array.</summary>
    public static DataValue FromUInt8Array(byte[] value) =>
        new(DataKind.UInt8Array, value, shape: null, isNull: false);

    /// <summary>Creates a value from a text string.</summary>
    public static DataValue FromString(string value) =>
        new(DataKind.String, value, shape: null, isNull: false);

    /// <summary>Creates a rank-1 tensor (vector) from a float array.</summary>
    public static DataValue FromVector(float[] value) =>
        new(DataKind.Vector, value, shape: null, isNull: false);

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

        return new DataValue(DataKind.Matrix, data, [rows, columns], isNull: false);
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

        return new DataValue(DataKind.Tensor, data, shape, isNull: false);
    }

    /// <summary>Creates a value from encoded image bytes.</summary>
    public static DataValue FromImage(byte[] value) =>
        new(DataKind.Image, value, shape: null, isNull: false);

    /// <summary>
    /// Creates a value from an <see cref="ImageHandle"/>.
    /// The handle carries a decoded bitmap and/or encoded bytes, enabling
    /// fused image pipelines that avoid redundant decode/encode cycles.
    /// </summary>
    internal static DataValue FromImageHandle(ImageHandle handle) =>
        new(DataKind.Image, handle, shape: null, isNull: false);

    /// <summary>Creates a value from a calendar date.</summary>
    public static DataValue FromDate(DateOnly value) =>
        new(DataKind.Date, value, shape: null, isNull: false);

    /// <summary>Creates a value from a date and time with UTC offset.</summary>
    public static DataValue FromDateTime(DateTimeOffset value) =>
        new(DataKind.DateTime, value, shape: null, isNull: false);

    /// <summary>Creates a value from a raw JSON string.</summary>
    public static DataValue FromJsonValue(string value) =>
        new(DataKind.JsonValue, value, shape: null, isNull: false);

    /// <summary>Creates a value from a 128-bit universally unique identifier.</summary>
    public static DataValue FromUuid(Guid value) =>
        new(DataKind.Uuid, value, shape: null, isNull: false);

    /// <summary>Creates a boolean value.</summary>
    public static DataValue FromBoolean(bool value) =>
        value ? BooleanTrue : BooleanFalse;

    /// <summary>Creates a value from a time-of-day.</summary>
    public static DataValue FromTime(TimeOnly value) =>
        new(DataKind.Time, value, shape: null, isNull: false);

    /// <summary>Creates a value from a duration (elapsed time span).</summary>
    public static DataValue FromDuration(TimeSpan value) =>
        new(DataKind.Duration, value, shape: null, isNull: false);

    /// <summary>
    /// Creates a typed array value from an element kind and an array of elements.
    /// The element kind is stored in the shape metadata so it can be recovered at runtime.
    /// </summary>
    /// <param name="elementKind">The <see cref="DataKind"/> shared by all elements.</param>
    /// <param name="elements">The array of element values.</param>
    public static DataValue FromArray(DataKind elementKind, DataValue[] elements) =>
        new(DataKind.Array, elements, shape: [(int)elementKind], isNull: false);

    /// <summary>Creates a typed null array with the given element kind.</summary>
    /// <param name="elementKind">The element kind of the null array.</param>
    public static DataValue NullArray(DataKind elementKind) =>
        new(DataKind.Array, payload: null, shape: [(int)elementKind], isNull: true);

    /// <summary>Creates a typed null value.</summary>
    public static DataValue Null(DataKind kind)
    {
        return kind switch
        {
            DataKind.Float32 => NullFloat32,
            DataKind.Int32 => NullInt32,
            DataKind.Int64 => NullInt64,
            DataKind.Float64 => NullFloat64,
            _ => new(kind, payload: null, shape: null, isNull: true),
        };
    }

    // ───────────────────────── Accessor methods ─────────────────────────

    /// <summary>Returns the 32-bit floating-point payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public float AsFloat32()
    {
        ThrowIfNullOrWrongKind(DataKind.Float32);
        return (float)_payload!;
    }

    /// <summary>Returns the unsigned 8-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public byte AsUInt8()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt8);
        return (byte)_payload!;
    }

    /// <summary>Returns the signed 8-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public sbyte AsInt8()
    {
        ThrowIfNullOrWrongKind(DataKind.Int8);
        return (sbyte)_payload!;
    }

    /// <summary>Returns the signed 16-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public short AsInt16()
    {
        ThrowIfNullOrWrongKind(DataKind.Int16);
        return (short)_payload!;
    }

    /// <summary>Returns the unsigned 16-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public ushort AsUInt16()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt16);
        return (ushort)_payload!;
    }

    /// <summary>Returns the signed 32-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public int AsInt32()
    {
        ThrowIfNullOrWrongKind(DataKind.Int32);
        return (int)_payload!;
    }

    /// <summary>Returns the unsigned 32-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public uint AsUInt32()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt32);
        return (uint)_payload!;
    }

    /// <summary>Returns the signed 64-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public long AsInt64()
    {
        ThrowIfNullOrWrongKind(DataKind.Int64);
        return (long)_payload!;
    }

    /// <summary>Returns the unsigned 64-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public ulong AsUInt64()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt64);
        return (ulong)_payload!;
    }

    /// <summary>Returns the 64-bit double-precision floating-point payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public double AsFloat64()
    {
        ThrowIfNullOrWrongKind(DataKind.Float64);
        return (double)_payload!;
    }

    /// <summary>Returns the byte array payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public byte[] AsUInt8Array()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt8Array);
        return (byte[])_payload!;
    }

    /// <summary>Returns the text string payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public string AsString()
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        return (string)_payload!;
    }

    /// <summary>Returns the vector (rank-1) float array payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public float[] AsVector()
    {
        ThrowIfNullOrWrongKind(DataKind.Vector);
        return (float[])_payload!;
    }

    /// <summary>Returns the matrix (rank-2) flat float array and its dimensions.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public float[] AsMatrix(out int rows, out int columns)
    {
        ThrowIfNullOrWrongKind(DataKind.Matrix);
        rows = _shape![0];
        columns = _shape[1];
        return (float[])_payload!;
    }

    /// <summary>Returns the tensor flat float array and its shape.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public float[] AsTensor(out int[] shape)
    {
        ThrowIfNullOrWrongKind(DataKind.Tensor);
        shape = _shape!;
        return (float[])_payload!;
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

        if (_payload is ImageHandle handle)
        {
            return handle.GetEncodedBytes();
        }

        return (byte[])_payload!;
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

        if (_payload is ImageHandle handle)
        {
            return handle;
        }

        byte[] bytes = (byte[])_payload!;
        return new ImageHandle(bytes, ImageEncoder.ResolveFormat(bytes, formatOverride: null));
    }

    /// <summary>
    /// Returns the <see cref="ImageHandle"/> payload if this value already owns one,
    /// or <c>null</c> if the payload is raw bytes. Used by the evaluator to check
    /// for disposable intermediate handles without allocating a new wrapper.
    /// </summary>
    internal ImageHandle? TryGetOwnedImageHandle()
    {
        return _payload as ImageHandle;
    }

    /// <summary>Returns the calendar date payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DateOnly AsDate()
    {
        ThrowIfNullOrWrongKind(DataKind.Date);
        return (DateOnly)_payload!;
    }

    /// <summary>Returns the date and time payload with UTC offset.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DateTimeOffset AsDateTime()
    {
        ThrowIfNullOrWrongKind(DataKind.DateTime);
        return (DateTimeOffset)_payload!;
    }

    /// <summary>Returns the raw JSON string payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public string AsJsonValue()
    {
        ThrowIfNullOrWrongKind(DataKind.JsonValue);
        return (string)_payload!;
    }

    /// <summary>Returns the UUID payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public Guid AsUuid()
    {
        ThrowIfNullOrWrongKind(DataKind.Uuid);
        return (Guid)_payload!;
    }

    /// <summary>Returns the boolean payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public bool AsBoolean()
    {
        ThrowIfNullOrWrongKind(DataKind.Boolean);
        return (bool)_payload!;
    }

    /// <summary>Returns the time-of-day payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public TimeOnly AsTime()
    {
        ThrowIfNullOrWrongKind(DataKind.Time);
        return (TimeOnly)_payload!;
    }

    /// <summary>Returns the duration payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public TimeSpan AsDuration()
    {
        ThrowIfNullOrWrongKind(DataKind.Duration);
        return (TimeSpan)_payload!;
    }

    /// <summary>Returns the typed array payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DataValue[] AsArray()
    {
        ThrowIfNullOrWrongKind(DataKind.Array);
        return (DataValue[])_payload!;
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
                _payload,
                [((float[])_payload!).Length],
                isNull: false),

            DataKind.Matrix => new DataValue(
                DataKind.Tensor,
                _payload,
                _shape,
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

        return new DataValue(DataKind.Vector, _payload, shape: null, isNull: false);
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

        return new DataValue(DataKind.Matrix, _payload, _shape, isNull: false);
    }

    // ───────────────────────── Equality ─────────────────────────

    /// <inheritdoc/>
    public override bool Equals(object? other) => Equals(other as DataValue);

    /// <inheritdoc/>
    public bool Equals(DataValue? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (_kind != other._kind) return false;
        if (_isNull && other._isNull) return true;
        if (_isNull != other._isNull) return false;

        return _kind switch
        {
            DataKind.Float32 => (float)_payload! == (float)other._payload!,
            DataKind.UInt8 => (byte)_payload! == (byte)other._payload!,
            DataKind.Int8 => (sbyte)_payload! == (sbyte)other._payload!,
            DataKind.Int16 => (short)_payload! == (short)other._payload!,
            DataKind.UInt16 => (ushort)_payload! == (ushort)other._payload!,
            DataKind.Int32 => (int)_payload! == (int)other._payload!,
            DataKind.UInt32 => (uint)_payload! == (uint)other._payload!,
            DataKind.Int64 => (long)_payload! == (long)other._payload!,
            DataKind.UInt64 => (ulong)_payload! == (ulong)other._payload!,
            DataKind.Float64 => (double)_payload! == (double)other._payload!,
            DataKind.String or DataKind.JsonValue => (string)_payload! == (string)other._payload!,
            DataKind.Vector => ((float[])_payload!).AsSpan().SequenceEqual((float[])other._payload!),
            DataKind.Matrix => _shape!.AsSpan().SequenceEqual(other._shape!)
                && ((float[])_payload!).AsSpan().SequenceEqual((float[])other._payload!),
            DataKind.Tensor => _shape!.AsSpan().SequenceEqual(other._shape!)
                && ((float[])_payload!).AsSpan().SequenceEqual((float[])other._payload!),
            DataKind.UInt8Array => ((byte[])_payload!).AsSpan().SequenceEqual((byte[])other._payload!),
            DataKind.Image => AsImage().AsSpan().SequenceEqual(other.AsImage()),
            DataKind.Date => (DateOnly)_payload! == (DateOnly)other._payload!,
            DataKind.DateTime => (DateTimeOffset)_payload! == (DateTimeOffset)other._payload!,
            DataKind.Uuid => (Guid)_payload! == (Guid)other._payload!,
            DataKind.Boolean => (bool)_payload! == (bool)other._payload!,
            DataKind.Time => (TimeOnly)_payload! == (TimeOnly)other._payload!,
            DataKind.Duration => (TimeSpan)_payload! == (TimeSpan)other._payload!,
            DataKind.Array => _shape!.AsSpan().SequenceEqual(other._shape!)
                && ((DataValue[])_payload!).AsSpan().SequenceEqual((DataValue[])other._payload!),
            _ => false,
        };
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        if (_isNull) return HashCode.Combine(_kind, true);

        return _kind switch
        {
            DataKind.Float32 => HashCode.Combine(_kind, (float)_payload!),
            DataKind.UInt8 => HashCode.Combine(_kind, (byte)_payload!),
            DataKind.Int8 => HashCode.Combine(_kind, (sbyte)_payload!),
            DataKind.Int16 => HashCode.Combine(_kind, (short)_payload!),
            DataKind.UInt16 => HashCode.Combine(_kind, (ushort)_payload!),
            DataKind.Int32 => HashCode.Combine(_kind, (int)_payload!),
            DataKind.UInt32 => HashCode.Combine(_kind, (uint)_payload!),
            DataKind.Int64 => HashCode.Combine(_kind, (long)_payload!),
            DataKind.UInt64 => HashCode.Combine(_kind, (ulong)_payload!),
            DataKind.Float64 => HashCode.Combine(_kind, (double)_payload!),
            DataKind.String or DataKind.JsonValue => HashCode.Combine(_kind, (string)_payload!),
            DataKind.Date => HashCode.Combine(_kind, (DateOnly)_payload!),
            DataKind.DateTime => HashCode.Combine(_kind, (DateTimeOffset)_payload!),
            DataKind.Uuid => HashCode.Combine(_kind, (Guid)_payload!),
            DataKind.Boolean => HashCode.Combine(_kind, (bool)_payload!),
            DataKind.Time => HashCode.Combine(_kind, (TimeOnly)_payload!),
            DataKind.Duration => HashCode.Combine(_kind, (TimeSpan)_payload!),
            DataKind.Vector => CombineFloatArrayHash(_kind, (float[])_payload!, _shape),
            DataKind.Matrix => CombineFloatArrayHash(_kind, (float[])_payload!, _shape),
            DataKind.Tensor => CombineFloatArrayHash(_kind, (float[])_payload!, _shape),
            DataKind.UInt8Array => CombineByteArrayHash(_kind, (byte[])_payload!),
            DataKind.Image => CombineByteArrayHash(_kind, AsImage()),
            DataKind.Array => CombineArrayHash(_kind, (DataValue[])_payload!, _shape),
            _ => HashCode.Combine(_kind),
        };
    }

    /// <inheritdoc/>
    public static bool operator ==(DataValue? left, DataValue? right) =>
        left is null ? right is null : left.Equals(right);

    /// <inheritdoc/>
    public static bool operator !=(DataValue? left, DataValue? right) => !(left == right);

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
            DataKind.Float32 => ((float)_payload!).ToString("G"),
            DataKind.UInt8 => ((byte)_payload!).ToString(),
            DataKind.Int8 => ((sbyte)_payload!).ToString(),
            DataKind.Int16 => ((short)_payload!).ToString(),
            DataKind.UInt16 => ((ushort)_payload!).ToString(),
            DataKind.Int32 => ((int)_payload!).ToString(),
            DataKind.UInt32 => ((uint)_payload!).ToString(),
            DataKind.Int64 => ((long)_payload!).ToString(),
            DataKind.UInt64 => ((ulong)_payload!).ToString(),
            DataKind.Float64 => ((double)_payload!).ToString("G"),
            DataKind.String => (string)_payload!,
            DataKind.Date => ((DateOnly)_payload!).ToString("yyyy-MM-dd"),
            DataKind.DateTime => ((DateTimeOffset)_payload!).ToString("O"),
            DataKind.JsonValue => (string)_payload!,
            DataKind.Uuid => ((Guid)_payload!).ToString("D"),
            DataKind.Boolean => (bool)_payload! ? "true" : "false",
            DataKind.Time => ((TimeOnly)_payload!).ToString("HH:mm:ss.FFFFFFF"),
            DataKind.Duration => ((TimeSpan)_payload!).ToString("c"),
            DataKind.Vector => $"Vector[{((float[])_payload!).Length}]",
            DataKind.Matrix => $"Matrix[{_shape![0]}x{_shape[1]}]",
            DataKind.Tensor => $"Tensor[{string.Join("x", _shape!)}]",
            DataKind.UInt8Array => $"UInt8Array[{((byte[])_payload!).Length}]",
            DataKind.Image => $"Image[{AsImage().Length} bytes]",
            DataKind.Array => $"Array<{(DataKind)_shape![0]}>[{((DataValue[])_payload!).Length}]",
            _ => _kind.ToString(),
        };
    }
}

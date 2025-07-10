using Axon.QueryEngine.Functions.Image;

namespace Axon.QueryEngine.Model;

/// <summary>
/// An immutable, discriminated union value that carries typed data through the query pipeline.
/// Use the static factory methods (<see cref="FromScalar"/>, <see cref="FromVector"/>, etc.)
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

    /// <summary>Cached scalar 0 — returned by all boolean-false results to avoid per-evaluation allocation.</summary>
    private static readonly DataValue ScalarZero = new(DataKind.Scalar, 0f, shape: null, isNull: false);

    /// <summary>Cached scalar 1 — returned by all boolean-true results to avoid per-evaluation allocation.</summary>
    private static readonly DataValue ScalarOne = new(DataKind.Scalar, 1f, shape: null, isNull: false);

    /// <summary>Cached null scalar — the most common null kind in expression evaluation.</summary>
    private static readonly DataValue NullScalar = new(DataKind.Scalar, payload: null, shape: null, isNull: true);

    // ───────────────────────── Factory methods ─────────────────────────

    /// <summary>Creates a scalar value from a 32-bit float.</summary>
    public static DataValue FromScalar(float value)
    {
        // Reuse cached instances for the two most common boolean-result values.
        if (value == 0f) return ScalarZero;
        if (value == 1f) return ScalarOne;
        return new(DataKind.Scalar, value, shape: null, isNull: false);
    }

    /// <summary>Creates a value from an unsigned 8-bit integer.</summary>
    public static DataValue FromUInt8(byte value) =>
        new(DataKind.UInt8, value, shape: null, isNull: false);

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

    /// <summary>Creates a value from a date and time.</summary>
    public static DataValue FromDateTime(DateTime value) =>
        new(DataKind.DateTime, value, shape: null, isNull: false);

    /// <summary>Creates a value from a raw JSON string.</summary>
    public static DataValue FromJsonValue(string value) =>
        new(DataKind.JsonValue, value, shape: null, isNull: false);

    /// <summary>Creates a typed null value.</summary>
    public static DataValue Null(DataKind kind)
    {
        if (kind == DataKind.Scalar) return NullScalar;
        return new(kind, payload: null, shape: null, isNull: true);
    }

    // ───────────────────────── Accessor methods ─────────────────────────

    /// <summary>Returns the scalar float payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public float AsScalar()
    {
        ThrowIfNullOrWrongKind(DataKind.Scalar);
        return (float)_payload!;
    }

    /// <summary>Returns the unsigned 8-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public byte AsUInt8()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt8);
        return (byte)_payload!;
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

    /// <summary>Returns the date and time payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DateTime AsDateTime()
    {
        ThrowIfNullOrWrongKind(DataKind.DateTime);
        return (DateTime)_payload!;
    }

    /// <summary>Returns the raw JSON string payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public string AsJsonValue()
    {
        ThrowIfNullOrWrongKind(DataKind.JsonValue);
        return (string)_payload!;
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
            DataKind.Scalar => (float)_payload! == (float)other._payload!,
            DataKind.UInt8 => (byte)_payload! == (byte)other._payload!,
            DataKind.String or DataKind.JsonValue => (string)_payload! == (string)other._payload!,
            DataKind.Vector => ((float[])_payload!).AsSpan().SequenceEqual((float[])other._payload!),
            DataKind.Matrix => _shape!.AsSpan().SequenceEqual(other._shape!)
                && ((float[])_payload!).AsSpan().SequenceEqual((float[])other._payload!),
            DataKind.Tensor => _shape!.AsSpan().SequenceEqual(other._shape!)
                && ((float[])_payload!).AsSpan().SequenceEqual((float[])other._payload!),
            DataKind.UInt8Array => ((byte[])_payload!).AsSpan().SequenceEqual((byte[])other._payload!),
            DataKind.Image => AsImage().AsSpan().SequenceEqual(other.AsImage()),
            DataKind.Date => (DateOnly)_payload! == (DateOnly)other._payload!,
            DataKind.DateTime => (DateTime)_payload! == (DateTime)other._payload!,
            _ => false,
        };
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        if (_isNull) return HashCode.Combine(_kind, true);

        return _kind switch
        {
            DataKind.Scalar => HashCode.Combine(_kind, (float)_payload!),
            DataKind.UInt8 => HashCode.Combine(_kind, (byte)_payload!),
            DataKind.String or DataKind.JsonValue => HashCode.Combine(_kind, (string)_payload!),
            DataKind.Date => HashCode.Combine(_kind, (DateOnly)_payload!),
            DataKind.DateTime => HashCode.Combine(_kind, (DateTime)_payload!),
            DataKind.Vector => CombineFloatArrayHash(_kind, (float[])_payload!, _shape),
            DataKind.Matrix => CombineFloatArrayHash(_kind, (float[])_payload!, _shape),
            DataKind.Tensor => CombineFloatArrayHash(_kind, (float[])_payload!, _shape),
            DataKind.UInt8Array => CombineByteArrayHash(_kind, (byte[])_payload!),
            DataKind.Image => CombineByteArrayHash(_kind, AsImage()),
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
}

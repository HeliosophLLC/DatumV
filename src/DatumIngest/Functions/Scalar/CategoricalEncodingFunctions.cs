using System.IO.Hashing;
using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// One-hot encodes a string value against an explicit domain: one_hot(value, label1, label2, ...).
/// Returns a Vector of length K (domain size) with a single 1.0 at the matching index.
/// Unknown values produce a zero vector. Null input produces null.
/// </summary>
public sealed class OneHotFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "one_hot";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 2)
            throw new ArgumentException("one_hot() requires at least 2 arguments (value, label1, ...).");
        for (int i = 0; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] != DataKind.String)
                throw new ArgumentException($"one_hot() argument {i + 1} must be String.");
        }
        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
            return DataValue.Null(DataKind.Vector);

        int domainSize = arguments.Length - 1;
        float[] result = new float[domainSize];
        string value = arguments[0].AsString();

        for (int i = 0; i < domainSize; i++)
        {
            if (string.Equals(value, arguments[i + 1].AsString(), StringComparison.Ordinal))
            {
                result[i] = 1f;
                return DataValue.FromVector(result);
            }
        }

        return DataValue.FromVector(result);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull)
            return DataValue.Null(DataKind.Vector);

        int domainSize = arguments.Length - 1;
        float[] result = new float[domainSize];
        string value = arguments[0].AsString(store);

        for (int i = 0; i < domainSize; i++)
        {
            if (string.Equals(value, arguments[i + 1].AsString(store), StringComparison.Ordinal))
            {
                result[i] = 1f;
                return DataValue.FromVector(result);
            }
        }

        return DataValue.FromVector(result);
    }
}

/// <summary>
/// One-hot encodes a string value with an unknown bucket: one_hot_unk(value, label1, label2, ...).
/// Returns a Vector of length K+1. Known values produce a 1.0 at the matching index.
/// Unknown values produce a 1.0 in the last (K+1) position. Null input produces null.
/// </summary>
public sealed class OneHotUnknownFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "one_hot_unk";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 2)
            throw new ArgumentException("one_hot_unk() requires at least 2 arguments (value, label1, ...).");
        for (int i = 0; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] != DataKind.String)
                throw new ArgumentException($"one_hot_unk() argument {i + 1} must be String.");
        }
        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
            return DataValue.Null(DataKind.Vector);

        int domainSize = arguments.Length - 1;
        float[] result = new float[domainSize + 1];
        string value = arguments[0].AsString();

        for (int i = 0; i < domainSize; i++)
        {
            if (string.Equals(value, arguments[i + 1].AsString(), StringComparison.Ordinal))
            {
                result[i] = 1f;
                return DataValue.FromVector(result);
            }
        }

        // Unknown value — activate last dimension.
        result[domainSize] = 1f;
        return DataValue.FromVector(result);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull)
            return DataValue.Null(DataKind.Vector);

        int domainSize = arguments.Length - 1;
        float[] result = new float[domainSize + 1];
        string value = arguments[0].AsString(store);

        for (int i = 0; i < domainSize; i++)
        {
            if (string.Equals(value, arguments[i + 1].AsString(store), StringComparison.Ordinal))
            {
                result[i] = 1f;
                return DataValue.FromVector(result);
            }
        }

        // Unknown value — activate last dimension.
        result[domainSize] = 1f;
        return DataValue.FromVector(result);
    }
}

/// <summary>
/// Label-encodes a string value against an explicit domain: label_encode(value, label1, label2, ...).
/// Returns the zero-based index of the matching label as a Scalar.
/// Unknown values return -1. Null input produces null.
/// </summary>
public sealed class LabelEncodeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "label_encode";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 2)
            throw new ArgumentException("label_encode() requires at least 2 arguments (value, label1, ...).");
        for (int i = 0; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] != DataKind.String)
                throw new ArgumentException($"label_encode() argument {i + 1} must be String.");
        }
        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
            return DataValue.Null(DataKind.Float32);

        string value = arguments[0].AsString();
        int domainSize = arguments.Length - 1;

        for (int i = 0; i < domainSize; i++)
        {
            if (string.Equals(value, arguments[i + 1].AsString(), StringComparison.Ordinal))
                return DataValue.FromFloat32(i);
        }

        return DataValue.FromFloat32(-1f);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull)
            return DataValue.Null(DataKind.Float32);

        string value = arguments[0].AsString(store);
        int domainSize = arguments.Length - 1;

        for (int i = 0; i < domainSize; i++)
        {
            if (string.Equals(value, arguments[i + 1].AsString(store), StringComparison.Ordinal))
                return DataValue.FromFloat32(i);
        }

        return DataValue.FromFloat32(-1f);
    }
}

/// <summary>
/// Label-encodes a string value with an unknown bucket: label_encode_unk(value, label1, label2, ...).
/// Returns the zero-based index of the matching label as a Scalar.
/// Unknown values return K (the domain size). Null input produces null.
/// </summary>
public sealed class LabelEncodeUnknownFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "label_encode_unk";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 2)
            throw new ArgumentException("label_encode_unk() requires at least 2 arguments (value, label1, ...).");
        for (int i = 0; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] != DataKind.String)
                throw new ArgumentException($"label_encode_unk() argument {i + 1} must be String.");
        }
        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
            return DataValue.Null(DataKind.Float32);

        string value = arguments[0].AsString();
        int domainSize = arguments.Length - 1;

        for (int i = 0; i < domainSize; i++)
        {
            if (string.Equals(value, arguments[i + 1].AsString(), StringComparison.Ordinal))
                return DataValue.FromFloat32(i);
        }

        return DataValue.FromFloat32(domainSize);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull)
            return DataValue.Null(DataKind.Float32);

        string value = arguments[0].AsString(store);
        int domainSize = arguments.Length - 1;

        for (int i = 0; i < domainSize; i++)
        {
            if (string.Equals(value, arguments[i + 1].AsString(store), StringComparison.Ordinal))
                return DataValue.FromFloat32(i);
        }

        return DataValue.FromFloat32(domainSize);
    }
}

/// <summary>
/// Feature-hashes a string value into a fixed-size one-hot vector: hash_encode(value, num_buckets).
/// Uses XxHash32 modulo num_buckets to assign a bucket, producing a Vector with a single 1.0.
/// Handles any cardinality without requiring an explicit domain vocabulary.
/// Null input produces null.
/// </summary>
public sealed class HashEncodeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "hash_encode";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
            throw new ArgumentException("hash_encode() requires exactly 2 arguments (value, num_buckets).");
        if (argumentKinds[0] != DataKind.String)
            throw new ArgumentException("hash_encode() first argument must be String.");
        if (!DataValueComparer.IsNumericScalar(argumentKinds[1]))
            throw new ArgumentException("hash_encode() second argument (num_buckets) must be numeric.");
        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
            return DataValue.Null(DataKind.Vector);

        int numBuckets = arguments[1].ToInt32();

        if (numBuckets <= 0)
            throw new ArgumentException("hash_encode() num_buckets must be a positive integer.");

        string value = arguments[0].AsString();
        byte[] inputBytes = Encoding.UTF8.GetBytes(value);
        uint hash = XxHash32.HashToUInt32(inputBytes);
        int bucket = (int)(hash % (uint)numBuckets);

        float[] result = new float[numBuckets];
        result[bucket] = 1f;
        return DataValue.FromVector(result);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull)
            return DataValue.Null(DataKind.Vector);

        int numBuckets = arguments[1].ToInt32();

        if (numBuckets <= 0)
            throw new ArgumentException("hash_encode() num_buckets must be a positive integer.");

        ReadOnlySpan<byte> inputBytes = arguments[0].AsUtf8Span(store);
        uint hash = XxHash32.HashToUInt32(inputBytes);
        int bucket = (int)(hash % (uint)numBuckets);

        float[] result = new float[numBuckets];
        result[bucket] = 1f;
        return DataValue.FromVector(result);
    }
}

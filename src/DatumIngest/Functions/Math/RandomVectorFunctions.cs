using DatumIngest.Model;

namespace DatumIngest.Functions.Math;

/// <summary>
/// Generates a vector of uniform random floats in [0, 1): <c>random_vector(length)</c>.
/// </summary>
public sealed class RandomVectorFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "random_vector";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("random_vector() requires exactly 1 argument (length).");

        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
            throw new ArgumentException($"random_vector() length must be Scalar or UInt8, got {argumentKinds[0]}.");

        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        int length = (int)(arguments[0].Kind is DataKind.UInt8
            ? arguments[0].AsUInt8()
            : arguments[0].AsScalar());

        if (length <= 0)
            throw new ArgumentException($"random_vector() length must be positive, got {length}.");

        float[] result = new float[length];
        for (int i = 0; i < length; i++)
            result[i] = (float)Random.Shared.NextDouble();

        return DataValue.FromVector(result);
    }
}

/// <summary>
/// Generates a vector of Gaussian random floats: <c>random_normal_vector(length, mean, stddev)</c>.
/// Useful for noise injection and feature augmentation.
/// </summary>
public sealed class RandomNormalVectorFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "random_normal_vector";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
            throw new ArgumentException("random_normal_vector() requires exactly 3 arguments (length, mean, stddev).");

        for (int i = 0; i < 3; i++)
        {
            if (argumentKinds[i] is not (DataKind.Scalar or DataKind.UInt8))
                throw new ArgumentException($"random_normal_vector() argument {i + 1} must be Scalar or UInt8, got {argumentKinds[i]}.");
        }

        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        int length = (int)(arguments[0].Kind is DataKind.UInt8
            ? arguments[0].AsUInt8()
            : arguments[0].AsScalar());

        float mean = arguments[1].Kind is DataKind.UInt8
            ? arguments[1].AsUInt8()
            : arguments[1].AsScalar();

        float stddev = arguments[2].Kind is DataKind.UInt8
            ? arguments[2].AsUInt8()
            : arguments[2].AsScalar();

        if (length <= 0)
            throw new ArgumentException($"random_normal_vector() length must be positive, got {length}.");
        if (stddev < 0)
            throw new ArgumentException($"random_normal_vector() stddev must be non-negative, got {stddev}.");

        float[] result = new float[length];
        for (int i = 0; i < length; i++)
            result[i] = mean + stddev * RandomNormalFunction.SampleStandardNormal();

        return DataValue.FromVector(result);
    }
}

/// <summary>
/// Generates a random permutation vector of [0, length): <c>random_permutation(length)</c>.
/// Uses the Fisher-Yates shuffle for unbiased results.
/// Useful for shuffling column order and position augmentation.
/// </summary>
public sealed class RandomPermutationFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "random_permutation";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("random_permutation() requires exactly 1 argument (length).");

        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
            throw new ArgumentException($"random_permutation() length must be Scalar or UInt8, got {argumentKinds[0]}.");

        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        int length = (int)(arguments[0].Kind is DataKind.UInt8
            ? arguments[0].AsUInt8()
            : arguments[0].AsScalar());

        if (length <= 0)
            throw new ArgumentException($"random_permutation() length must be positive, got {length}.");

        float[] result = new float[length];
        for (int i = 0; i < length; i++)
            result[i] = i;

        // Fisher-Yates shuffle.
        for (int i = length - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }

        return DataValue.FromVector(result);
    }
}

/// <summary>
/// Samples <c>count</c> elements from an array without replacement:
/// <c>random_choice(array, count)</c>. Returns a new array of the same element kind.
/// Useful for random feature subset selection.
/// </summary>
public sealed class RandomChoiceFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "random_choice";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
            throw new ArgumentException("random_choice() requires exactly 2 arguments (array, count).");

        if (argumentKinds[0] != DataKind.Array)
            throw new ArgumentException($"random_choice() first argument must be Array, got {argumentKinds[0]}.");

        if (argumentKinds[1] is not (DataKind.Scalar or DataKind.UInt8))
            throw new ArgumentException($"random_choice() count (argument 2) must be Scalar or UInt8, got {argumentKinds[1]}.");

        return DataKind.Array;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
            return DataValue.Null(DataKind.Array);

        DataValue[] source = arguments[0].AsArray();
        DataKind elementKind = arguments[0].ArrayElementKind;

        int count = (int)(arguments[1].Kind is DataKind.UInt8
            ? arguments[1].AsUInt8()
            : arguments[1].AsScalar());

        if (count < 0)
            throw new ArgumentException($"random_choice() count must be non-negative, got {count}.");
        if (count > source.Length)
            throw new ArgumentException($"random_choice() count ({count}) exceeds array length ({source.Length}).");

        // Fisher-Yates partial shuffle: select 'count' random elements.
        int[] indices = new int[source.Length];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = i;

        DataValue[] result = new DataValue[count];
        for (int i = 0; i < count; i++)
        {
            int j = Random.Shared.Next(i, source.Length);
            (indices[i], indices[j]) = (indices[j], indices[i]);
            result[i] = source[indices[i]];
        }

        return DataValue.FromArray(elementKind, result);
    }
}

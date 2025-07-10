using DatumQuery.Model;

namespace DatumQuery.Functions.Math;

/// <summary>
/// Softmax function: softmax(vector) normalizes a vector into a probability distribution.
/// Uses the numerically stable variant: subtract max before exponentiating.
/// Input must be a Vector; output is a Vector of the same length.
/// </summary>
public sealed class SoftmaxFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "softmax";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("softmax() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not DataKind.Vector)
        {
            throw new ArgumentException("softmax() requires a Vector argument.");
        }

        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Vector);
        }

        float[] source = input.AsVector();
        float[] result = new float[source.Length];

        float max = float.NegativeInfinity;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] > max) max = source[i];
        }

        float sum = 0f;
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = MathF.Exp(source[i] - max);
            sum += result[i];
        }

        for (int i = 0; i < result.Length; i++)
        {
            result[i] /= sum;
        }

        return DataValue.FromVector(result);
    }
}

/// <summary>
/// Log-softmax function: log_softmax(vector) = log(softmax(vector)).
/// Computed in a numerically stable way using the log-sum-exp trick.
/// Input must be a Vector; output is a Vector of the same length.
/// </summary>
public sealed class LogSoftmaxFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "log_softmax";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("log_softmax() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not DataKind.Vector)
        {
            throw new ArgumentException("log_softmax() requires a Vector argument.");
        }

        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Vector);
        }

        float[] source = input.AsVector();
        float[] result = new float[source.Length];

        float max = float.NegativeInfinity;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] > max) max = source[i];
        }

        float logSumExp = 0f;
        for (int i = 0; i < source.Length; i++)
        {
            logSumExp += MathF.Exp(source[i] - max);
        }
        logSumExp = max + MathF.Log(logSumExp);

        for (int i = 0; i < source.Length; i++)
        {
            result[i] = source[i] - logSumExp;
        }

        return DataValue.FromVector(result);
    }
}

/// <summary>
/// L2 normalization: l2_normalize(vector) = vector / ||vector||₂.
/// Input must be a Vector; output is a unit-length Vector.
/// </summary>
public sealed class L2NormalizeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "l2_normalize";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("l2_normalize() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not DataKind.Vector)
        {
            throw new ArgumentException("l2_normalize() requires a Vector argument.");
        }

        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Vector);
        }

        float[] source = input.AsVector();
        float[] result = new float[source.Length];

        float sumSquares = 0f;
        for (int i = 0; i < source.Length; i++)
        {
            sumSquares += source[i] * source[i];
        }

        float norm = MathF.Sqrt(sumSquares);
        if (norm == 0f)
        {
            return DataValue.FromVector(result);
        }

        for (int i = 0; i < source.Length; i++)
        {
            result[i] = source[i] / norm;
        }

        return DataValue.FromVector(result);
    }
}

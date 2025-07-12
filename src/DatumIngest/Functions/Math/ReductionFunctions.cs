using DatumIngest.Model;
using static DatumIngest.Functions.Math.ReductionFunctionHelpers;

namespace DatumIngest.Functions.Math;

/// <summary>Reduces a Vector/Matrix/Tensor to a Scalar sum of all elements.</summary>
public sealed class VecSumFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_sum";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_sum() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
            throw new ArgumentException($"vec_sum() does not support {argumentKinds[0]}.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float[] data = ExtractFloats(arguments[0]);
        float sum = 0f;
        for (int i = 0; i < data.Length; i++) sum += data[i];
        return DataValue.FromScalar(sum);
    }
}

/// <summary>Reduces a Vector/Matrix/Tensor to a Scalar mean of all elements.</summary>
public sealed class VecMeanFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_mean";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_mean() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
            throw new ArgumentException($"vec_mean() does not support {argumentKinds[0]}.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float[] data = ExtractFloats(arguments[0]);
        if (data.Length == 0) return DataValue.FromScalar(float.NaN);
        float sum = 0f;
        for (int i = 0; i < data.Length; i++) sum += data[i];
        return DataValue.FromScalar(sum / data.Length);
    }
}

/// <summary>Reduces a Vector/Matrix/Tensor to a Scalar minimum of all elements.</summary>
public sealed class VecMinFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_min";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_min() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
            throw new ArgumentException($"vec_min() does not support {argumentKinds[0]}.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float[] data = ExtractFloats(arguments[0]);
        if (data.Length == 0) return DataValue.FromScalar(float.NaN);
        float min = data[0];
        for (int i = 1; i < data.Length; i++) if (data[i] < min) min = data[i];
        return DataValue.FromScalar(min);
    }
}

/// <summary>Reduces a Vector/Matrix/Tensor to a Scalar maximum of all elements.</summary>
public sealed class VecMaxFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_max";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_max() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
            throw new ArgumentException($"vec_max() does not support {argumentKinds[0]}.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float[] data = ExtractFloats(arguments[0]);
        if (data.Length == 0) return DataValue.FromScalar(float.NaN);
        float max = data[0];
        for (int i = 1; i < data.Length; i++) if (data[i] > max) max = data[i];
        return DataValue.FromScalar(max);
    }
}

/// <summary>Reduces a Vector/Matrix/Tensor to a Scalar population standard deviation.</summary>
public sealed class VecStdFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_std";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_std() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
            throw new ArgumentException($"vec_std() does not support {argumentKinds[0]}.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float[] data = ExtractFloats(arguments[0]);
        if (data.Length == 0) return DataValue.FromScalar(float.NaN);
        return DataValue.FromScalar(MathF.Sqrt(ComputeVariance(data)));
    }
}

/// <summary>Reduces a Vector/Matrix/Tensor to a Scalar population variance.</summary>
public sealed class VecVarFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_var";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_var() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
            throw new ArgumentException($"vec_var() does not support {argumentKinds[0]}.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float[] data = ExtractFloats(arguments[0]);
        if (data.Length == 0) return DataValue.FromScalar(float.NaN);
        return DataValue.FromScalar(ComputeVariance(data));
    }
}

/// <summary>Reduces a Vector/Matrix/Tensor to a Scalar median of all elements.</summary>
public sealed class VecMedianFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_median";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_median() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
            throw new ArgumentException($"vec_median() does not support {argumentKinds[0]}.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float[] data = ExtractFloats(arguments[0]);
        if (data.Length == 0) return DataValue.FromScalar(float.NaN);
        float[] sorted = new float[data.Length];
        Array.Copy(data, sorted, data.Length);
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        float median = sorted.Length % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2f
            : sorted[mid];
        return DataValue.FromScalar(median);
    }
}

/// <summary>Returns the index of the minimum element in a Vector/Matrix/Tensor.</summary>
public sealed class VecArgminFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_argmin";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_argmin() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
            throw new ArgumentException($"vec_argmin() does not support {argumentKinds[0]}.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float[] data = ExtractFloats(arguments[0]);
        if (data.Length == 0) return DataValue.FromScalar(float.NaN);
        int minIndex = 0;
        for (int i = 1; i < data.Length; i++)
        {
            if (data[i] < data[minIndex]) minIndex = i;
        }
        return DataValue.FromScalar(minIndex);
    }
}

/// <summary>Returns the index of the maximum element in a Vector/Matrix/Tensor.</summary>
public sealed class VecArgmaxFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_argmax";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_argmax() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
            throw new ArgumentException($"vec_argmax() does not support {argumentKinds[0]}.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float[] data = ExtractFloats(arguments[0]);
        if (data.Length == 0) return DataValue.FromScalar(float.NaN);
        int maxIndex = 0;
        for (int i = 1; i < data.Length; i++)
        {
            if (data[i] > data[maxIndex]) maxIndex = i;
        }
        return DataValue.FromScalar(maxIndex);
    }
}

/// <summary>
/// Computes the Lp norm of a Vector/Matrix/Tensor: vec_norm(x) or vec_norm(x, p).
/// Default p = 2 (Euclidean norm). p = 1 for Manhattan, p = Infinity for max-norm.
/// </summary>
public sealed class VecNormFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_norm";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (1 or 2))
            throw new ArgumentException("vec_norm() requires 1 or 2 arguments.");
        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
            throw new ArgumentException($"vec_norm() does not support {argumentKinds[0]}.");
        if (argumentKinds.Length == 2 && argumentKinds[1] is not (DataKind.Scalar or DataKind.UInt8))
            throw new ArgumentException("vec_norm() second argument (p) must be Scalar or UInt8.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float[] data = ExtractFloats(arguments[0]);

        float p = 2f;
        if (arguments.Length == 2 && !arguments[1].IsNull)
        {
            p = arguments[1].Kind is DataKind.UInt8 ? arguments[1].AsUInt8() : arguments[1].AsScalar();
        }

        if (float.IsPositiveInfinity(p))
        {
            float max = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float abs = MathF.Abs(data[i]);
                if (abs > max) max = abs;
            }
            return DataValue.FromScalar(max);
        }

        float sum = 0f;
        for (int i = 0; i < data.Length; i++)
        {
            sum += MathF.Pow(MathF.Abs(data[i]), p);
        }
        return DataValue.FromScalar(MathF.Pow(sum, 1f / p));
    }
}

/// <summary>Counts the number of non-zero elements in a Vector/Matrix/Tensor.</summary>
public sealed class VecCountNonzeroFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_count_nonzero";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_count_nonzero() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
            throw new ArgumentException($"vec_count_nonzero() does not support {argumentKinds[0]}.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float[] data = ExtractFloats(arguments[0]);
        int count = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != 0f) count++;
        }
        return DataValue.FromScalar(count);
    }
}

/// <summary>Returns 1 if any element is non-zero, 0 otherwise.</summary>
public sealed class VecAnyFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_any";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_any() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
            throw new ArgumentException($"vec_any() does not support {argumentKinds[0]}.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float[] data = ExtractFloats(arguments[0]);
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != 0f) return DataValue.FromScalar(1f);
        }
        return DataValue.FromScalar(0f);
    }
}

/// <summary>Returns 1 if all elements are non-zero, 0 otherwise.</summary>
public sealed class VecAllFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_all";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_all() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
            throw new ArgumentException($"vec_all() does not support {argumentKinds[0]}.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float[] data = ExtractFloats(arguments[0]);
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == 0f) return DataValue.FromScalar(0f);
        }
        return DataValue.FromScalar(1f);
    }
}

/// <summary>Reduces a Vector/Matrix/Tensor to a Scalar product of all elements.</summary>
public sealed class VecProductFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_product";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_product() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
            throw new ArgumentException($"vec_product() does not support {argumentKinds[0]}.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float[] data = ExtractFloats(arguments[0]);
        if (data.Length == 0) return DataValue.FromScalar(1f);
        float product = 1f;
        for (int i = 0; i < data.Length; i++) product *= data[i];
        return DataValue.FromScalar(product);
    }
}

/// <summary>
/// Internal helpers for reduction functions.
/// </summary>
internal static class ReductionFunctionHelpers
{
    /// <summary>
    /// Extracts a flat float array from a Vector, Matrix, or Tensor DataValue.
    /// </summary>
    internal static float[] ExtractFloats(DataValue value)
    {
        return value.Kind switch
        {
            DataKind.Vector => value.AsVector(),
            DataKind.Matrix => value.AsMatrix(out _, out _),
            DataKind.Tensor => value.AsTensor(out _),
            _ => []
        };
    }

    /// <summary>
    /// Computes population variance of a float array.
    /// </summary>
    internal static float ComputeVariance(float[] data)
    {
        float mean = 0f;
        for (int i = 0; i < data.Length; i++) mean += data[i];
        mean /= data.Length;

        float sumSqDiff = 0f;
        for (int i = 0; i < data.Length; i++)
        {
            float diff = data[i] - mean;
            sumSqDiff += diff * diff;
        }

        return sumSqDiff / data.Length;
    }
}

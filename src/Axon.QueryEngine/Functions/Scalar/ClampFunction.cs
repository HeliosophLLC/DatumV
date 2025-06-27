using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Functions.Scalar;

/// <summary>
/// Clamps a value to a [min, max] range. Works on scalar, vector, matrix, and tensor.
/// <c>clamp(val, min, max)</c>
/// </summary>
public sealed class ClampFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "clamp";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
        {
            throw new ArgumentException("clamp() requires exactly 3 arguments: value, min, max.");
        }

        DataKind inputKind = argumentKinds[0];
        if (inputKind is not (DataKind.Scalar or DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
        {
            throw new ArgumentException($"clamp() does not support {inputKind}.");
        }

        return inputKind;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(input.Kind);
        }

        float min = arguments[1].AsScalar();
        float max = arguments[2].AsScalar();

        switch (input.Kind)
        {
            case DataKind.Scalar:
                return DataValue.FromScalar(Math.Clamp(input.AsScalar(), min, max));

            case DataKind.Vector:
            {
                float[] source = input.AsVector();
                float[] result = new float[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    result[index] = Math.Clamp(source[index], min, max);
                }
                return DataValue.FromVector(result);
            }

            case DataKind.Matrix:
            {
                float[] source = input.AsMatrix(out int rows, out int columns);
                float[] result = new float[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    result[index] = Math.Clamp(source[index], min, max);
                }
                return DataValue.FromMatrix(result, rows, columns);
            }

            case DataKind.Tensor:
            {
                float[] source = input.AsTensor(out int[] shape);
                float[] result = new float[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    result[index] = Math.Clamp(source[index], min, max);
                }
                return DataValue.FromTensor(result, shape);
            }

            default:
                throw new InvalidOperationException($"clamp() does not support {input.Kind}.");
        }
    }
}

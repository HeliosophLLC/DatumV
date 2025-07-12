using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Denormalizes a value by multiplying by a factor.
/// <c>denormalize(val, factor)</c>
/// </summary>
public sealed class DenormalizeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "denormalize";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("denormalize() requires exactly 2 arguments: value, factor.");
        }

        DataKind inputKind = argumentKinds[0];
        if (inputKind is not (DataKind.Scalar or DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
        {
            throw new ArgumentException($"denormalize() does not support {inputKind}.");
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

        float factor = arguments[1].AsScalar();

        switch (input.Kind)
        {
            case DataKind.Scalar:
                return DataValue.FromScalar(input.AsScalar() * factor);

            case DataKind.Vector:
            {
                float[] source = input.AsVector();
                float[] result = new float[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    result[index] = source[index] * factor;
                }
                return DataValue.FromVector(result);
            }

            case DataKind.Matrix:
            {
                float[] source = input.AsMatrix(out int rows, out int columns);
                float[] result = new float[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    result[index] = source[index] * factor;
                }
                return DataValue.FromMatrix(result, rows, columns);
            }

            case DataKind.Tensor:
            {
                float[] source = input.AsTensor(out int[] shape);
                float[] result = new float[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    result[index] = source[index] * factor;
                }
                return DataValue.FromTensor(result, shape);
            }

            default:
                throw new InvalidOperationException($"denormalize() does not support {input.Kind}.");
        }
    }
}

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
        if (inputKind is not (DataKind.Float32 or DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
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

        float factor = arguments[1].AsFloat32();

        switch (input.Kind)
        {
            case DataKind.Float32:
                return DataValue.FromFloat32(input.AsFloat32() * factor);

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

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(input.Kind);
        }

        float factor = arguments[1].AsFloat32();

        switch (input.Kind)
        {
            case DataKind.Float32:
                return DataValue.FromFloat32(input.AsFloat32() * factor);

            case DataKind.Vector:
            {
                float[] source = input.AsVector(store);
                float[] result = new float[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    result[index] = source[index] * factor;
                }
                return DataValue.FromVector(result, store);
            }

            case DataKind.Matrix:
            {
                float[] source = input.AsMatrix(store, out int rows, out int columns);
                float[] result = new float[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    result[index] = source[index] * factor;
                }
                return DataValue.FromMatrix(result, rows, columns, store);
            }

            case DataKind.Tensor:
            {
                float[] source = input.AsTensor(store, out int[] shape);
                float[] result = new float[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    result[index] = source[index] * factor;
                }
                return DataValue.FromTensor(result, shape, store);
            }

            default:
                throw new InvalidOperationException($"denormalize() does not support {input.Kind}.");
        }
    }
}

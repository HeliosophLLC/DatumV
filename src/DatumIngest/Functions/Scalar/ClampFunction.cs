using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

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
        if (inputKind is not (DataKind.Float32 or DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
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

        float min = arguments[1].AsFloat32();
        float max = arguments[2].AsFloat32();

        switch (input.Kind)
        {
            case DataKind.Float32:
                return DataValue.FromFloat32(System.Math.Clamp(input.AsFloat32(), min, max));

            case DataKind.Vector:
            {
                float[] source = input.AsVector();
                float[] result = new float[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    result[index] = System.Math.Clamp(source[index], min, max);
                }
                return DataValue.FromVector(result);
            }

            case DataKind.Matrix:
            {
                float[] source = input.AsMatrix(out int rows, out int columns);
                float[] result = new float[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    result[index] = System.Math.Clamp(source[index], min, max);
                }
                return DataValue.FromMatrix(result, rows, columns);
            }

            case DataKind.Tensor:
            {
                float[] source = input.AsTensor(out int[] shape);
                float[] result = new float[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    result[index] = System.Math.Clamp(source[index], min, max);
                }
                return DataValue.FromTensor(result, shape);
            }

            default:
                throw new InvalidOperationException($"clamp() does not support {input.Kind}.");
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

        float min = arguments[1].AsFloat32();
        float max = arguments[2].AsFloat32();

        switch (input.Kind)
        {
            case DataKind.Float32:
                return DataValue.FromFloat32(System.Math.Clamp(input.AsFloat32(), min, max));

            case DataKind.Vector:
            {
                float[] source = input.AsVector(store);
                float[] result = new float[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    result[index] = System.Math.Clamp(source[index], min, max);
                }
                return DataValue.FromVector(result, store);
            }

            case DataKind.Matrix:
            {
                float[] source = input.AsMatrix(store, out int rows, out int columns);
                float[] result = new float[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    result[index] = System.Math.Clamp(source[index], min, max);
                }
                return DataValue.FromMatrix(result, rows, columns, store);
            }

            case DataKind.Tensor:
            {
                float[] source = input.AsTensor(store, out int[] shape);
                float[] result = new float[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    result[index] = System.Math.Clamp(source[index], min, max);
                }
                return DataValue.FromTensor(result, shape, store);
            }

            default:
                throw new InvalidOperationException($"clamp() does not support {input.Kind}.");
        }
    }
}

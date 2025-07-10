using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Functions.Scalar;

/// <summary>
/// Normalizes a value to the 0-1 range.
/// <c>normalize(val)</c> for byte/byte[] uses default 0-255 range.
/// <c>normalize(val, min, max)</c> for scalar/vector/tensor uses explicit range.
/// </summary>
public sealed class NormalizeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "normalize";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 1 || argumentKinds.Length > 3)
        {
            throw new ArgumentException("normalize() requires 1 to 3 arguments.");
        }

        DataKind inputKind = argumentKinds[0];

        if (inputKind == DataKind.UInt8)
        {
            return DataKind.Scalar;
        }

        if (inputKind == DataKind.UInt8Array)
        {
            return DataKind.Vector;
        }

        if (inputKind is DataKind.Scalar or DataKind.Vector or DataKind.Tensor or DataKind.Matrix)
        {
            if (argumentKinds.Length < 3)
            {
                throw new ArgumentException("normalize() for scalar/vector/tensor requires min and max arguments.");
            }
            return inputKind;
        }

        throw new ArgumentException($"normalize() does not support {inputKind}.");
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(input.Kind);
        }

        switch (input.Kind)
        {
            case DataKind.UInt8:
                return DataValue.FromScalar(input.AsUInt8() / 255.0f);

            case DataKind.UInt8Array:
            {
                byte[] bytes = input.AsUInt8Array();
                float[] result = new float[bytes.Length];
                for (int index = 0; index < bytes.Length; index++)
                {
                    result[index] = bytes[index] / 255.0f;
                }
                return DataValue.FromVector(result);
            }

            case DataKind.Scalar:
            {
                float min = arguments[1].AsScalar();
                float max = arguments[2].AsScalar();
                float range = max - min;
                if (range == 0)
                {
                    return DataValue.FromScalar(0);
                }
                return DataValue.FromScalar((input.AsScalar() - min) / range);
            }

            case DataKind.Vector:
            {
                float min = arguments[1].AsScalar();
                float max = arguments[2].AsScalar();
                float range = max - min;
                float[] sourceVector = input.AsVector();
                float[] result = new float[sourceVector.Length];
                for (int index = 0; index < sourceVector.Length; index++)
                {
                    result[index] = range == 0 ? 0 : (sourceVector[index] - min) / range;
                }
                return DataValue.FromVector(result);
            }

            case DataKind.Matrix:
            {
                float min = arguments[1].AsScalar();
                float max = arguments[2].AsScalar();
                float range = max - min;
                float[] sourceData = input.AsMatrix(out int rows, out int columns);
                float[] result = new float[sourceData.Length];
                for (int index = 0; index < sourceData.Length; index++)
                {
                    result[index] = range == 0 ? 0 : (sourceData[index] - min) / range;
                }
                return DataValue.FromMatrix(result, rows, columns);
            }

            case DataKind.Tensor:
            {
                float min = arguments[1].AsScalar();
                float max = arguments[2].AsScalar();
                float range = max - min;
                float[] sourceData = input.AsTensor(out int[] shape);
                float[] result = new float[sourceData.Length];
                for (int index = 0; index < sourceData.Length; index++)
                {
                    result[index] = range == 0 ? 0 : (sourceData[index] - min) / range;
                }
                return DataValue.FromTensor(result, shape);
            }

            default:
                throw new InvalidOperationException($"normalize() does not support {input.Kind}.");
        }
    }
}

using DatumQuery.Model;

namespace DatumQuery.Functions.Scalar;

/// <summary>
/// Returns the length of a string or collection.
/// <c>len(val)</c> works on String, Vector, UInt8Array, Matrix, and Tensor.
/// </summary>
public sealed class LenFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "len";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("len() requires exactly 1 argument.");
        }

        DataKind inputKind = argumentKinds[0];
        if (inputKind is not (DataKind.String or DataKind.Vector or DataKind.UInt8Array or DataKind.Matrix or DataKind.Tensor or DataKind.JsonValue))
        {
            throw new ArgumentException($"len() does not support {inputKind}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        switch (input.Kind)
        {
            case DataKind.String:
                return DataValue.FromScalar(input.AsString().Length);

            case DataKind.Vector:
                return DataValue.FromScalar(input.AsVector().Length);

            case DataKind.UInt8Array:
                return DataValue.FromScalar(input.AsUInt8Array().Length);

            case DataKind.Matrix:
            {
                input.AsMatrix(out int rows, out int columns);
                return DataValue.FromScalar(rows * columns);
            }

            case DataKind.Tensor:
            {
                input.AsTensor(out int[] shape);
                int totalElements = 1;
                foreach (int dimension in shape)
                {
                    totalElements *= dimension;
                }
                return DataValue.FromScalar(totalElements);
            }

            case DataKind.JsonValue:
                return DataValue.FromScalar(input.AsJsonValue().Length);

            default:
                throw new InvalidOperationException($"len() does not support {input.Kind}.");
        }
    }
}

using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the length of a string or collection.
/// <c>len(val)</c> works on String, Vector, UInt8Array, Matrix, Tensor, JsonValue, and Array.
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
        if (inputKind is not (DataKind.String or DataKind.Vector or DataKind.UInt8Array or DataKind.Matrix or DataKind.Tensor or DataKind.JsonValue or DataKind.Array))
        {
            throw new ArgumentException($"len() does not support {inputKind}.");
        }

        return DataKind.Int32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Int32);
        }

        switch (input.Kind)
        {
            case DataKind.String:
                return DataValue.FromInt32(input.AsString().Length);

            case DataKind.Vector:
                return DataValue.FromInt32(input.AsVector().Length);

            case DataKind.UInt8Array:
                return DataValue.FromInt32(input.AsUInt8Array().Length);

            case DataKind.Matrix:
            {
                input.AsMatrix(out int rows, out int columns);
                return DataValue.FromInt32(rows * columns);
            }

            case DataKind.Tensor:
            {
                input.AsTensor(out int[] shape);
                int totalElements = 1;
                foreach (int dimension in shape)
                {
                    totalElements *= dimension;
                }
                return DataValue.FromInt32(totalElements);
            }

            case DataKind.JsonValue:
                return DataValue.FromInt32(input.AsJsonValue().Length);

            case DataKind.Array:
                return DataValue.FromInt32(input.AsArray().Length);

            default:
                throw new InvalidOperationException($"len() does not support {input.Kind}.");
        }
    }
}

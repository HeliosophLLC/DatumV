using DatumQuery.Model;

namespace DatumQuery.Functions.Scalar;

/// <summary>
/// Reshapes a tensor to a new shape without copying data.
/// <c>reshape(tensor, dim1, dim2, ...)</c>
/// Validates that the total element count matches the new shape.
/// </summary>
public sealed class ReshapeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "reshape";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 2)
        {
            throw new ArgumentException("reshape() requires at least 2 arguments: value and one or more dimensions.");
        }

        DataKind inputKind = argumentKinds[0];
        if (inputKind is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
        {
            throw new ArgumentException($"reshape() does not support {inputKind}.");
        }

        int dimensionCount = argumentKinds.Length - 1;
        if (dimensionCount == 1)
        {
            return DataKind.Vector;
        }
        if (dimensionCount == 2)
        {
            return DataKind.Matrix;
        }
        return DataKind.Tensor;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(input.Kind);
        }

        // Extract the flat data from any supported type.
        float[] data;
        switch (input.Kind)
        {
            case DataKind.Vector:
                data = input.AsVector();
                break;
            case DataKind.Matrix:
                data = input.AsMatrix(out _, out _);
                break;
            case DataKind.Tensor:
                data = input.AsTensor(out _);
                break;
            default:
                throw new InvalidOperationException($"reshape() does not support {input.Kind}.");
        }

        // Build the new shape from the remaining arguments.
        int dimensionCount = arguments.Length - 1;
        int[] newShape = new int[dimensionCount];
        int expectedLength = 1;
        for (int index = 0; index < dimensionCount; index++)
        {
            int dimension = (int)arguments[index + 1].AsScalar();
            newShape[index] = dimension;
            expectedLength *= dimension;
        }

        if (expectedLength != data.Length)
        {
            throw new ArgumentException(
                $"reshape() cannot reshape {data.Length} elements into shape [{string.Join(", ", newShape)}] ({expectedLength} elements).");
        }

        // Zero-copy: reuse the same underlying array with a new shape interpretation.
        if (dimensionCount == 1)
        {
            return DataValue.FromVector(data);
        }
        if (dimensionCount == 2)
        {
            return DataValue.FromMatrix(data, newShape[0], newShape[1]);
        }
        return DataValue.FromTensor(data, newShape);
    }
}

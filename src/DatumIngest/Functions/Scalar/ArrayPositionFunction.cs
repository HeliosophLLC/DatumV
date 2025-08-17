using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the 1-based position of the first occurrence of a value within an
/// <see cref="DataKind.Array"/>. <c>array_position(arr, value)</c> returns a
/// <see cref="DataKind.Float32"/> index (1-based), or null if the value is not found.
/// Uses <see cref="DataValue.Equals(DataValue)"/> for comparison.
/// Returns null if the array itself is null.
/// </summary>
public sealed class ArrayPositionFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "array_position";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("array_position() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_position() requires an Array as the first argument, got {argumentKinds[0]}.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue arrayValue = arguments[0];
        DataValue searchValue = arguments[1];

        if (arrayValue.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        DataValue[] elements = arrayValue.AsArray();

        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i].Equals(searchValue))
            {
                return DataValue.FromFloat32(i + 1);
            }
        }

        return DataValue.Null(DataKind.Float32);
    }
}

using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Tests whether an <see cref="DataKind.Array"/> contains a given value.
/// <c>array_contains(arr, value)</c> returns a <see cref="DataKind.Boolean"/>
/// indicating whether any element in the array equals the search value
/// (using <see cref="DataValue.Equals(DataValue)"/>).
/// Returns null if the array is null.
/// </summary>
public sealed class ArrayContainsFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "array_contains";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("array_contains() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_contains() requires an Array as the first argument, got {argumentKinds[0]}.");
        }

        return DataKind.Boolean;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue arrayValue = arguments[0];
        DataValue searchValue = arguments[1];

        if (arrayValue.IsNull)
        {
            return DataValue.Null(DataKind.Boolean);
        }

        DataValue[] elements = arrayValue.AsArray();

        foreach (DataValue element in elements)
        {
            if (element.Equals(searchValue))
            {
                return DataValue.FromBoolean(true);
            }
        }

        return DataValue.FromBoolean(false);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue arrayValue = arguments[0];
        DataValue searchValue = arguments[1];

        if (arrayValue.IsNull)
        {
            return DataValue.Null(DataKind.Boolean);
        }

        DataValue[] elements = arrayValue.AsArray(store);

        foreach (DataValue element in elements)
        {
            if (element.Equals(searchValue))
            {
                return DataValue.FromBoolean(true);
            }
        }

        return DataValue.FromBoolean(false);
    }
}

using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns a copy of an <see cref="DataKind.Array"/> with duplicate elements removed.
/// <c>array_distinct(arr)</c> preserves the first occurrence of each unique value
/// (by <see cref="DataValue.Equals(DataValue)"/>) and original ordering.
/// Returns null if the input is null.
/// </summary>
public sealed class ArrayDistinctFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "array_distinct";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("array_distinct() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_distinct() requires an Array argument, got {argumentKinds[0]}.");
        }

        return DataKind.Array;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.NullArray(input.ArrayElementKind);
        }

        DataValue[] elements = input.AsArray();
        HashSet<DataValue> seen = new(elements.Length);
        List<DataValue> distinct = new(elements.Length);

        foreach (DataValue element in elements)
        {
            if (seen.Add(element))
            {
                distinct.Add(element);
            }
        }

        return DataValue.FromArray(input.ArrayElementKind, [.. distinct]);
    }
}

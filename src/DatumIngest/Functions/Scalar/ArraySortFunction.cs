using DatumIngest.Execution.Operators;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns a sorted copy of an <see cref="DataKind.Array"/>.
/// <c>array_sort(arr)</c> sorts elements in ascending order using the same comparison
/// semantics as <c>ORDER BY</c> (nulls sort last). Supports Scalar, UInt8, String,
/// Date, and DateTime element kinds. Unsortable element kinds are returned unchanged.
/// Returns null if the input is null.
/// </summary>
public sealed class ArraySortFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "array_sort";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("array_sort() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_sort() requires an Array argument, got {argumentKinds[0]}.");
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
        DataValue[] sorted = new DataValue[elements.Length];
        Array.Copy(elements, sorted, elements.Length);
        Array.Sort(sorted, OrderByOperator.CompareDataValues);

        return DataValue.FromArray(input.ArrayElementKind, sorted);
    }
}

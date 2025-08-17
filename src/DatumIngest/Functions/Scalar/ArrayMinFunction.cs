using DatumIngest.Execution.Operators;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the minimum element in an <see cref="DataKind.Array"/>, skipping nulls.
/// <c>array_min(arr)</c> uses the same ordering semantics as <c>ORDER BY</c>.
/// Returns null if the array is null or all elements are null.
/// </summary>
/// <remarks>
/// Implements <see cref="IElementKindAwareFunction"/> so that the return type
/// reflects the array's element kind at plan time. When the element kind is unknown
/// at plan time, falls back to <see cref="DataKind.Float32"/>.
/// Supported element kinds: Scalar, UInt8, String, Date, DateTime, Time, Duration.
/// </remarks>
public sealed class ArrayMinFunction : IElementKindAwareFunction
{
    /// <inheritdoc />
    public string Name => "array_min";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        ValidateArgumentCount(argumentKinds);
        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataKind ValidateArgumentsWithElementKinds(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataKind?> arrayElementKinds)
    {
        ValidateArgumentCount(argumentKinds);
        return arrayElementKinds.Length > 0 && arrayElementKinds[0] is DataKind elementKind
            ? elementKind
            : DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        DataKind elementKind = input.ArrayElementKind;
        DataValue? minimum = null;

        foreach (DataValue element in input.AsArray())
        {
            if (element.IsNull) continue;

            if (minimum is null || OrderByOperator.CompareDataValues(element, minimum) < 0)
            {
                minimum = element;
            }
        }

        return minimum ?? DataValue.Null(elementKind);
    }

    private static void ValidateArgumentCount(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("array_min() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_min() requires an Array argument, got {argumentKinds[0]}.");
        }
    }
}

using DatumIngest.Execution.Operators;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the maximum element in an <see cref="DataKind.Array"/>, skipping nulls.
/// <c>array_max(arr)</c> uses the same ordering semantics as <c>ORDER BY</c>.
/// Returns null if the array is null or all elements are null.
/// </summary>
/// <remarks>
/// Implements <see cref="IElementKindAwareFunction"/> so that the return type
/// reflects the array's element kind at plan time. When the element kind is unknown
/// at plan time, falls back to <see cref="DataKind.Float32"/>.
/// Supported element kinds: Scalar, UInt8, String, Date, DateTime, Time, Duration.
/// </remarks>
public sealed class ArrayMaxFunction : IElementKindAwareFunction
{
    /// <inheritdoc />
    public string Name => "array_max";

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
        DataValue? maximum = null;

        foreach (DataValue element in input.AsArray())
        {
            if (element.IsNull) continue;

            if (maximum is null || OrderByOperator.CompareDataValues(element, maximum.Value) > 0)
            {
                maximum = element;
            }
        }

        return maximum ?? DataValue.Null(elementKind);
    }

    private static void ValidateArgumentCount(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("array_max() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_max() requires an Array argument, got {argumentKinds[0]}.");
        }
    }
}

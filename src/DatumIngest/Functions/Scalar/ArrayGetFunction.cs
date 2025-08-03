using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the element at a 1-based index from an <see cref="DataKind.Array"/>.
/// <c>array_get(arr, index)</c> returns the element at position <c>index</c>,
/// where 1 is the first element. Returns null if the array is null, the index is null,
/// or the index is out of bounds.
/// </summary>
/// <remarks>
/// Implements <see cref="IElementKindAwareFunction"/> so that the return type
/// reflects the array's element kind at plan time (e.g. <c>Array&lt;String&gt;</c>
/// yields <see cref="DataKind.String"/>). When the element kind is unknown at plan
/// time, falls back to <see cref="DataKind.Scalar"/>.
/// </remarks>
public sealed class ArrayGetFunction : IElementKindAwareFunction
{
    /// <inheritdoc />
    public string Name => "array_get";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        ValidateArgumentCount(argumentKinds);
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataKind ValidateArgumentsWithElementKinds(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataKind?> arrayElementKinds)
    {
        ValidateArgumentCount(argumentKinds);
        return arrayElementKinds.Length > 0 && arrayElementKinds[0] is DataKind elementKind
            ? elementKind
            : DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue arrayValue = arguments[0];
        DataValue indexValue = arguments[1];

        if (arrayValue.IsNull || indexValue.IsNull)
        {
            return DataValue.Null(arrayValue.IsNull
                ? DataKind.Scalar
                : arrayValue.ArrayElementKind);
        }

        DataValue[] elements = arrayValue.AsArray();
        int index = (int)indexValue.AsScalar() - 1; // 1-based → 0-based

        if (index < 0 || index >= elements.Length)
        {
            return DataValue.Null(arrayValue.ArrayElementKind);
        }

        return elements[index];
    }

    private static void ValidateArgumentCount(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("array_get() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_get() requires an Array as the first argument, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.Scalar)
        {
            throw new ArgumentException(
                $"array_get() requires a Scalar index as the second argument, got {argumentKinds[1]}.");
        }
    }
}

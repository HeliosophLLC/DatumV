using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Concatenates two <see cref="DataKind.Array"/> values into a single array.
/// <c>array_concat(arr1, arr2)</c> requires both arrays to share the same element kind.
/// Returns null if either input is null.
/// </summary>
public sealed class ArrayConcatFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "array_concat";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("array_concat() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_concat() requires an Array as the first argument, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_concat() requires an Array as the second argument, got {argumentKinds[1]}.");
        }

        return DataKind.Array;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue left = arguments[0];
        DataValue right = arguments[1];

        if (left.IsNull || right.IsNull)
        {
            DataKind elementKind = left.IsNull ? right.ArrayElementKind : left.ArrayElementKind;
            return DataValue.NullArray(elementKind);
        }

        DataValue[] leftElements = left.AsArray();
        DataValue[] rightElements = right.AsArray();

        DataValue[] combined = new DataValue[leftElements.Length + rightElements.Length];
        Array.Copy(leftElements, 0, combined, 0, leftElements.Length);
        Array.Copy(rightElements, 0, combined, leftElements.Length, rightElements.Length);

        return DataValue.FromArray(left.ArrayElementKind, combined);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue left = arguments[0];
        DataValue right = arguments[1];

        if (left.IsNull || right.IsNull)
        {
            DataKind elementKind = left.IsNull ? right.ArrayElementKind : left.ArrayElementKind;
            return DataValue.NullArray(elementKind);
        }

        DataValue[] leftElements = left.AsArray(store);
        DataValue[] rightElements = right.AsArray(store);

        DataValue[] combined = new DataValue[leftElements.Length + rightElements.Length];
        Array.Copy(leftElements, 0, combined, 0, leftElements.Length);
        Array.Copy(rightElements, 0, combined, leftElements.Length, rightElements.Length);

        return DataValue.FromArray(left.ArrayElementKind, combined, store);
    }
}

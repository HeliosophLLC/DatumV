using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts a contiguous sub-array from an <see cref="DataKind.Array"/>.
/// <c>array_slice(arr, start, length)</c> uses 1-based indexing consistent with SQL
/// conventions (like <c>SUBSTRING</c>). The start position is clamped to the array
/// bounds, and length is clamped to available elements. Returns an empty array if the
/// slice range is outside bounds. Returns null if the array is null.
/// </summary>
public sealed class ArraySliceFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "array_slice";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
        {
            throw new ArgumentException("array_slice() requires exactly 3 arguments.");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_slice() requires an Array as the first argument, got {argumentKinds[0]}.");
        }

        if (!DataValue.IsIntegerKind(argumentKinds[1]))
        {
            throw new ArgumentException(
                $"array_slice() requires an integer start position as the second argument, got {argumentKinds[1]}.");
        }

        if (!DataValue.IsIntegerKind(argumentKinds[2]))
        {
            throw new ArgumentException(
                $"array_slice() requires an integer length as the third argument, got {argumentKinds[2]}.");
        }

        return DataKind.Array;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue arrayValue = arguments[0];
        DataValue startValue = arguments[1];
        DataValue lengthValue = arguments[2];

        if (arrayValue.IsNull || startValue.IsNull || lengthValue.IsNull)
        {
            return DataValue.NullArray(arrayValue.IsNull
                ? DataKind.Float32
                : arrayValue.ArrayElementKind);
        }

        DataValue[] elements = arrayValue.AsArray();
        DataKind elementKind = arrayValue.ArrayElementKind;

        int start = System.Math.Max(0, startValue.ToInt32() - 1);
        int length = System.Math.Max(0, lengthValue.ToInt32());

        if (start >= elements.Length)
        {
            return DataValue.FromArray(elementKind, []);
        }

        int actualLength = System.Math.Min(length, elements.Length - start);
        DataValue[] slice = new DataValue[actualLength];
        Array.Copy(elements, start, slice, 0, actualLength);

        return DataValue.FromArray(elementKind, slice);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue arrayValue = arguments[0];
        DataValue startValue = arguments[1];
        DataValue lengthValue = arguments[2];

        if (arrayValue.IsNull || startValue.IsNull || lengthValue.IsNull)
        {
            return DataValue.NullArray(arrayValue.IsNull
                ? DataKind.Float32
                : arrayValue.ArrayElementKind);
        }

        DataValue[] elements = arrayValue.AsArray(store);
        DataKind elementKind = arrayValue.ArrayElementKind;

        int start = System.Math.Max(0, startValue.ToInt32() - 1);
        int length = System.Math.Max(0, lengthValue.ToInt32());

        if (start >= elements.Length)
        {
            return DataValue.FromArray(elementKind, (DataValue[])[], store);
        }

        int actualLength = System.Math.Min(length, elements.Length - start);
        DataValue[] slice = new DataValue[actualLength];
        Array.Copy(elements, start, slice, 0, actualLength);

        return DataValue.FromArray(elementKind, slice, store);
    }
}

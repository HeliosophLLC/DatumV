using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns a reversed copy of an <see cref="DataKind.Array"/>.
/// <c>array_reverse(arr)</c> reverses the element order without modifying the original.
/// Returns null if the input is null.
/// </summary>
public sealed class ArrayReverseFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "array_reverse";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("array_reverse() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_reverse() requires an Array argument, got {argumentKinds[0]}.");
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
        DataValue[] reversed = new DataValue[elements.Length];
        Array.Copy(elements, reversed, elements.Length);
        Array.Reverse(reversed);

        return DataValue.FromArray(input.ArrayElementKind, reversed);
    }
}

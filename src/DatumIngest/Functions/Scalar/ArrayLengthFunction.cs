using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the number of elements in an <see cref="DataKind.Array"/>.
/// <c>array_length(arr)</c> returns a <see cref="DataKind.Scalar"/> count.
/// Returns null if the input is null.
/// </summary>
public sealed class ArrayLengthFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "array_length";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("array_length() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_length() requires an Array argument, got {argumentKinds[0]}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        return DataValue.FromScalar(input.AsArray().Length);
    }
}

using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the number of elements in an <see cref="DataKind.Array"/>.
/// <c>array_length(arr)</c> returns a <see cref="DataKind.Float32"/> count.
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

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        return DataValue.FromFloat32(input.AsArray().Length);
    }
}

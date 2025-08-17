using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the zero-based index of the first occurrence of a substring within a string.
/// <c>position(string, substring)</c> returns the index as a Scalar, or -1 if the substring is not found.
/// </summary>
public sealed class PositionFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "position";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("position() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"position() requires a String as the first argument, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException($"position() requires a String as the second argument, got {argumentKinds[1]}.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        DataValue substring = arguments[1];

        if (input.IsNull || substring.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        int index = input.AsString().IndexOf(substring.AsString(), StringComparison.Ordinal);
        return DataValue.FromFloat32(index);
    }
}

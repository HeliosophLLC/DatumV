using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the rightmost characters of a string.
/// <c>right(string, n)</c> returns the last <c>n</c> characters from the input string.
/// </summary>
public sealed class RightFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "right";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("right() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"right() requires a String as the first argument, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.Scalar)
        {
            throw new ArgumentException($"right() requires a Scalar as the second argument, got {argumentKinds[1]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        DataValue countValue = arguments[1];

        if (input.IsNull || countValue.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string inputString = input.AsString();
        int count = (int)countValue.AsScalar();
        string result = inputString[System.Math.Max(0, inputString.Length - count)..];
        return DataValue.FromString(result);
    }
}

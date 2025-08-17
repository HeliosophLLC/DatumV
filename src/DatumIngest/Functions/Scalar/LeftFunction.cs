using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the leftmost characters of a string.
/// <c>left(string, n)</c> returns the first <c>n</c> characters from the input string.
/// </summary>
public sealed class LeftFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "left";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("left() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"left() requires a String as the first argument, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.Float32)
        {
            throw new ArgumentException($"left() requires a Scalar as the second argument, got {argumentKinds[1]}.");
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
        int count = (int)countValue.AsFloat32();
        string result = inputString[..System.Math.Min(count, inputString.Length)];
        return DataValue.FromString(result);
    }
}

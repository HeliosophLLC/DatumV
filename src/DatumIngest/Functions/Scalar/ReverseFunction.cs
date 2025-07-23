using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Reverses the characters in a string.
/// <c>reverse(string)</c> returns the input string with its character order reversed.
/// </summary>
public sealed class ReverseFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "reverse";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("reverse() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"reverse() requires a String argument, got {argumentKinds[0]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string inputString = input.AsString();
        string result = string.Create(inputString.Length, inputString, (span, state) =>
        {
            state.AsSpan().CopyTo(span);
            span.Reverse();
        });

        return DataValue.FromString(result);
    }
}

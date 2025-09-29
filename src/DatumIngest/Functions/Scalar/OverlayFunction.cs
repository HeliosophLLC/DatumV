using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Replaces a substring within a string at a specified position.
/// <c>overlay(string, newsubstring, start [, count])</c>
/// Replaces <c>count</c> characters starting at 1-based <c>start</c> with <c>newsubstring</c>.
/// If <c>count</c> is omitted, defaults to the length of <c>newsubstring</c>.
/// </summary>
public sealed class OverlayFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "overlay";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (3 or 4))
        {
            throw new ArgumentException("overlay() requires 3 or 4 arguments: string, newsubstring, start [, count].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"overlay() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException($"overlay() second argument must be String, got {argumentKinds[1]}.");
        }

        if (argumentKinds[2] != DataKind.Float32)
        {
            throw new ArgumentException($"overlay() third argument must be Scalar, got {argumentKinds[2]}.");
        }

        if (argumentKinds.Length == 4 && argumentKinds[3] != DataKind.Float32)
        {
            throw new ArgumentException($"overlay() fourth argument must be Scalar, got {argumentKinds[3]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        DataValue newSubstring = arguments[1];
        DataValue startValue = arguments[2];

        if (input.IsNull || newSubstring.IsNull || startValue.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string text = input.AsString();
        string replacement = newSubstring.AsString();
        int start = (int)startValue.AsFloat32() - 1; // convert to 0-based

        int count = replacement.Length;
        if (arguments.Length == 4)
        {
            if (arguments[3].IsNull)
            {
                return DataValue.Null(DataKind.String);
            }

            count = (int)arguments[3].AsFloat32();
        }

        if (start < 0)
        {
            start = 0;
        }

        if (start >= text.Length)
        {
            return DataValue.FromString(text + replacement);
        }

        int end = System.Math.Min(start + count, text.Length);
        return DataValue.FromString(string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(end)));
    }
}

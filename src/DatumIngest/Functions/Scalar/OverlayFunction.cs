using System.Buffers;
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

        FunctionArgumentException.ThrowIfArgumentKindMismatch(Name, 0, "string", DataKind.String, argumentKinds[0]);
        FunctionArgumentException.ThrowIfArgumentKindMismatch(Name, 1, "newsubstring", DataKind.String, argumentKinds[1]);
        FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 2, "start", argumentKinds[2]);

        if (argumentKinds.Length == 4)
        {
            FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 3, "count", argumentKinds[3]);
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
        int start = startValue.ToInt32() - 1; // convert to 0-based

        int count = replacement.Length;
        if (arguments.Length == 4)
        {
            if (arguments[3].IsNull)
            {
                return DataValue.Null(DataKind.String);
            }

            count = arguments[3].ToInt32();
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

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        DataValue newSubstring = arguments[1];
        DataValue startValue = arguments[2];

        if (input.IsNull || newSubstring.IsNull || startValue.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        ReadOnlySpan<char> textSpan = input.AsStringSpan(store, out char[] rentedText);
        ReadOnlySpan<char> replacementSpan = newSubstring.AsStringSpan(store, out char[] rentedReplacement);
        int start = startValue.ToInt32() - 1; // convert to 0-based

        int count = replacementSpan.Length;
        if (arguments.Length == 4)
        {
            if (arguments[3].IsNull)
            {
                ArrayPool<char>.Shared.Return(rentedText);
                ArrayPool<char>.Shared.Return(rentedReplacement);
                return DataValue.Null(DataKind.String);
            }

            count = arguments[3].ToInt32();
        }

        if (start < 0)
        {
            start = 0;
        }

        DataValue result;
        if (start >= textSpan.Length)
        {
            // Append replacement after the text.
            int totalLength = textSpan.Length + replacementSpan.Length;
            char[] buf = ArrayPool<char>.Shared.Rent(totalLength);
            textSpan.CopyTo(buf.AsSpan(0));
            replacementSpan.CopyTo(buf.AsSpan(textSpan.Length));
            result = DataValue.FromCharSpan(buf.AsSpan(0, totalLength), store);
            ArrayPool<char>.Shared.Return(buf);
        }
        else
        {
            int end = System.Math.Min(start + count, textSpan.Length);
            ReadOnlySpan<char> prefix = textSpan[..start];
            ReadOnlySpan<char> suffix = textSpan[end..];
            int totalLength = prefix.Length + replacementSpan.Length + suffix.Length;
            char[] buf = ArrayPool<char>.Shared.Rent(totalLength);
            prefix.CopyTo(buf.AsSpan(0));
            replacementSpan.CopyTo(buf.AsSpan(prefix.Length));
            suffix.CopyTo(buf.AsSpan(prefix.Length + replacementSpan.Length));
            result = DataValue.FromCharSpan(buf.AsSpan(0, totalLength), store);
            ArrayPool<char>.Shared.Return(buf);
        }

        ArrayPool<char>.Shared.Return(rentedText);
        ArrayPool<char>.Shared.Return(rentedReplacement);
        return result;
    }
}

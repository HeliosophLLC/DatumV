using System.Text.RegularExpressions;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Checks whether a match of a POSIX regular expression pattern occurs within a string.
/// <c>regexp_like(string, pattern [, flags])</c>
/// <c>flags</c>: <c>'i'</c> for case-insensitive.
/// </summary>
public sealed class RegexpLikeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "regexp_like";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new ArgumentException(
                "regexp_like() requires 2 or 3 arguments: string, pattern [, flags].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_like() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_like() second argument (pattern) must be String, got {argumentKinds[1]}.");
        }

        if (argumentKinds.Length == 3 && argumentKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_like() third argument (flags) must be String, got {argumentKinds[2]}.");
        }

        return DataKind.Boolean;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull || arguments[1].IsNull)
        {
            return DataValue.Null(DataKind.Boolean);
        }

        string input = arguments[0].AsString();
        string pattern = arguments[1].AsString();

        RegexOptions options = RegexOptions.None;
        if (arguments.Length == 3 && !arguments[2].IsNull)
        {
            string flags = arguments[2].AsString();
            if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        }

        return DataValue.FromBoolean(Regex.IsMatch(input, pattern, options));
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull || arguments[1].IsNull)
        {
            return DataValue.Null(DataKind.Boolean);
        }

        ReadOnlySpan<char> input = arguments[0].AsStringSpan(store, out char[] rentedInput);
        string pattern = arguments[1].AsString(store); // Regex pattern must be string

        RegexOptions options = RegexOptions.None;
        if (arguments.Length == 3 && !arguments[2].IsNull)
        {
            ReadOnlySpan<char> flags = arguments[2].AsStringSpan(store, out char[] rentedFlags);
            if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
            System.Buffers.ArrayPool<char>.Shared.Return(rentedFlags);
        }

        bool result = Regex.IsMatch(input, pattern, options);
        System.Buffers.ArrayPool<char>.Shared.Return(rentedInput);
        return DataValue.FromBoolean(result);
    }
}

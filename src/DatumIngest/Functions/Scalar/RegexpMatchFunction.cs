using System.Text.RegularExpressions;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns captured substrings from the first match of a POSIX regular expression.
/// <c>regexp_match(string, pattern [, flags])</c>
/// If the pattern has capture groups, returns an Array of the captured substrings.
/// If the pattern has no capture groups, returns an Array with the whole match.
/// Returns NULL if no match.
/// <c>flags</c>: <c>'i'</c> for case-insensitive.
/// </summary>
public sealed class RegexpMatchFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "regexp_match";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new ArgumentException(
                "regexp_match() requires 2 or 3 arguments: string, pattern [, flags].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_match() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_match() second argument (pattern) must be String, got {argumentKinds[1]}.");
        }

        if (argumentKinds.Length == 3 && argumentKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_match() third argument (flags) must be String, got {argumentKinds[2]}.");
        }

        return DataKind.Array;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull || arguments[1].IsNull)
        {
            return DataValue.NullArray(DataKind.String);
        }

        string input = arguments[0].AsString();
        string pattern = arguments[1].AsString();

        RegexOptions options = RegexOptions.None;
        if (arguments.Length == 3 && !arguments[2].IsNull)
        {
            string flags = arguments[2].AsString();
            if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        }

        Match match = Regex.Match(input, pattern, options);

        if (!match.Success)
        {
            return DataValue.NullArray(DataKind.String);
        }

        // If pattern has capture groups, return them (skip group 0 = full match)
        if (match.Groups.Count > 1)
        {
            DataValue[] elements = new DataValue[match.Groups.Count - 1];
            for (int i = 1; i < match.Groups.Count; i++)
            {
                elements[i - 1] = match.Groups[i].Success
                    ? DataValue.FromString(match.Groups[i].Value)
                    : DataValue.Null(DataKind.String);
            }

            return DataValue.FromArray(DataKind.String, elements);
        }

        // No capture groups — return the whole match
        return DataValue.FromArray(DataKind.String, [DataValue.FromString(match.Value)]);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull || arguments[1].IsNull)
        {
            return DataValue.NullArray(DataKind.String);
        }

        string input = arguments[0].AsString(store);
        string pattern = arguments[1].AsString(store);

        RegexOptions options = RegexOptions.None;
        if (arguments.Length == 3 && !arguments[2].IsNull)
        {
            string flags = arguments[2].AsString(store);
            if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        }

        Match match = Regex.Match(input, pattern, options);

        if (!match.Success)
        {
            return DataValue.NullArray(DataKind.String);
        }

        // If pattern has capture groups, return them (skip group 0 = full match)
        if (match.Groups.Count > 1)
        {
            DataValue[] elements = new DataValue[match.Groups.Count - 1];
            for (int i = 1; i < match.Groups.Count; i++)
            {
                elements[i - 1] = match.Groups[i].Success
                    ? DataValue.FromCharSpan(match.Groups[i].ValueSpan, store)
                    : DataValue.Null(DataKind.String);
            }

            return DataValue.FromArray(DataKind.String, elements);
        }

        // No capture groups — return the whole match
        return DataValue.FromArray(DataKind.String, [DataValue.FromCharSpan(match.ValueSpan, store)]);
    }
}

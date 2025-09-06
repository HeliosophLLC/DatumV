using System.Text.RegularExpressions;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Replaces substrings matching a regular expression pattern.
/// <c>regexp_replace(string, pattern, replacement)</c> replaces all matches (global).
/// <c>regexp_replace(string, pattern, replacement, flags)</c> accepts a flags string:
/// <c>'g'</c> for global (default), <c>'i'</c> for case-insensitive. Without <c>'g'</c>,
/// only the first match is replaced.
/// </summary>
public sealed class RegexpReplaceFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "regexp_replace";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (3 or 4))
        {
            throw new ArgumentException(
                "regexp_replace() requires 3 or 4 arguments: string, pattern, replacement [, flags].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_replace() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_replace() second argument (pattern) must be String, got {argumentKinds[1]}.");
        }

        if (argumentKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_replace() third argument (replacement) must be String, got {argumentKinds[2]}.");
        }

        if (argumentKinds.Length == 4 && argumentKinds[3] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_replace() fourth argument (flags) must be String, got {argumentKinds[3]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull || arguments[1].IsNull || arguments[2].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string input = arguments[0].AsString();
        string pattern = arguments[1].AsString();
        string replacement = arguments[2].AsString();

        bool global = true;
        RegexOptions options = RegexOptions.None;

        if (arguments.Length == 4 && !arguments[3].IsNull)
        {
            string flags = arguments[3].AsString();
            global = flags.Contains('g');
            if (flags.Contains('i'))
            {
                options |= RegexOptions.IgnoreCase;
            }
        }

        if (global)
        {
            return DataValue.FromString(Regex.Replace(input, pattern, replacement, options));
        }

        Regex regex = new(pattern, options);
        return DataValue.FromString(regex.Replace(input, replacement, 1));
    }
}

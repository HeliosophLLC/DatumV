using System.Text.RegularExpressions;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the substring matching the N'th occurrence of a POSIX regular expression.
/// <c>regexp_substr(string, pattern [, start [, N [, flags [, subexpr]]]])</c>
/// <c>start</c> is 1-based (default 1). <c>N</c> is the occurrence number (default 1).
/// <c>flags</c>: <c>'i'</c> for case-insensitive. <c>subexpr</c> selects a capture group (0-based, default 0 = full match).
/// Returns NULL if no match.
/// </summary>
public sealed class RegexpSubstrFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "regexp_substr";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is < 2 or > 6)
        {
            throw new ArgumentException(
                "regexp_substr() requires 2 to 6 arguments: string, pattern [, start [, N [, flags [, subexpr]]]].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_substr() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_substr() second argument (pattern) must be String, got {argumentKinds[1]}.");
        }

        if (argumentKinds.Length >= 3 && !DataValue.IsIntegerKind(argumentKinds[2]))
        {
            throw new ArgumentException(
                $"regexp_substr() third argument (start) must be Scalar, got {argumentKinds[2]}.");
        }

        if (argumentKinds.Length >= 4 && !DataValue.IsIntegerKind(argumentKinds[3]))
        {
            throw new ArgumentException(
                $"regexp_substr() fourth argument (N) must be Scalar, got {argumentKinds[3]}.");
        }

        if (argumentKinds.Length >= 5 && argumentKinds[4] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_substr() fifth argument (flags) must be String, got {argumentKinds[4]}.");
        }

        if (argumentKinds.Length == 6 && !DataValue.IsIntegerKind(argumentKinds[5]))
        {
            throw new ArgumentException(
                $"regexp_substr() sixth argument (subexpr) must be Scalar, got {argumentKinds[5]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull || arguments[1].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string input = arguments[0].AsString();
        string pattern = arguments[1].AsString();

        int start = 0;
        if (arguments.Length >= 3 && !arguments[2].IsNull)
        {
            start = arguments[2].ToInt32() - 1;
            if (start < 0) start = 0;
        }

        int n = 1;
        if (arguments.Length >= 4 && !arguments[3].IsNull)
        {
            n = arguments[3].ToInt32();
        }

        RegexOptions options = RegexOptions.None;
        if (arguments.Length >= 5 && !arguments[4].IsNull)
        {
            string flags = arguments[4].AsString();
            if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        }

        int subexpr = 0;
        if (arguments.Length == 6 && !arguments[5].IsNull)
        {
            subexpr = arguments[5].ToInt32();
        }

        if (start >= input.Length)
        {
            return DataValue.Null(DataKind.String);
        }

        string searchIn = input[start..];
        MatchCollection matches = Regex.Matches(searchIn, pattern, options);

        if (n < 1 || n > matches.Count)
        {
            return DataValue.Null(DataKind.String);
        }

        Match match = matches[n - 1];

        if (subexpr < 0 || subexpr >= match.Groups.Count)
        {
            return DataValue.Null(DataKind.String);
        }

        Group group = match.Groups[subexpr];
        return group.Success
            ? DataValue.FromString(group.Value)
            : DataValue.Null(DataKind.String);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull || arguments[1].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string input = arguments[0].AsString(store);
        string pattern = arguments[1].AsString(store);

        int start = 0;
        if (arguments.Length >= 3 && !arguments[2].IsNull)
        {
            start = arguments[2].ToInt32() - 1;
            if (start < 0) start = 0;
        }

        int n = 1;
        if (arguments.Length >= 4 && !arguments[3].IsNull)
        {
            n = arguments[3].ToInt32();
        }

        RegexOptions options = RegexOptions.None;
        if (arguments.Length >= 5 && !arguments[4].IsNull)
        {
            string flags = arguments[4].AsString(store);
            if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        }

        int subexpr = 0;
        if (arguments.Length == 6 && !arguments[5].IsNull)
        {
            subexpr = arguments[5].ToInt32();
        }

        if (start >= input.Length)
        {
            return DataValue.Null(DataKind.String);
        }

        string searchIn = input[start..];
        MatchCollection matches = Regex.Matches(searchIn, pattern, options);

        if (n < 1 || n > matches.Count)
        {
            return DataValue.Null(DataKind.String);
        }

        Match match = matches[n - 1];

        if (subexpr < 0 || subexpr >= match.Groups.Count)
        {
            return DataValue.Null(DataKind.String);
        }

        Group group = match.Groups[subexpr];
        return group.Success
            ? DataValue.FromString(group.Value, store)
            : DataValue.Null(DataKind.String);
    }
}

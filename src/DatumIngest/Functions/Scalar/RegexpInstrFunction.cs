using System.Text.RegularExpressions;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the position within a string where the N'th match of a POSIX regex occurs.
/// <c>regexp_instr(string, pattern [, start [, N [, endoption [, flags [, subexpr]]]]])</c>
/// <c>start</c> is 1-based (default 1). <c>N</c> is the occurrence number (default 1).
/// <c>endoption</c>: 0 returns start of match (default), 1 returns position after match.
/// <c>flags</c>: <c>'i'</c> for case-insensitive. <c>subexpr</c> selects a capture group (0-based).
/// Returns 0 if no match.
/// </summary>
public sealed class RegexpInstrFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "regexp_instr";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is < 2 or > 7)
        {
            throw new ArgumentException(
                "regexp_instr() requires 2 to 7 arguments: string, pattern [, start [, N [, endoption [, flags [, subexpr]]]]].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_instr() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_instr() second argument (pattern) must be String, got {argumentKinds[1]}.");
        }

        if (argumentKinds.Length >= 3 && !DataValue.IsIntegerKind(argumentKinds[2]))
        {
            throw new ArgumentException(
                $"regexp_instr() third argument (start) must be Scalar, got {argumentKinds[2]}.");
        }

        if (argumentKinds.Length >= 4 && !DataValue.IsIntegerKind(argumentKinds[3]))
        {
            throw new ArgumentException(
                $"regexp_instr() fourth argument (N) must be Scalar, got {argumentKinds[3]}.");
        }

        if (argumentKinds.Length >= 5 && !DataValue.IsIntegerKind(argumentKinds[4]))
        {
            throw new ArgumentException(
                $"regexp_instr() fifth argument (endoption) must be Scalar, got {argumentKinds[4]}.");
        }

        if (argumentKinds.Length >= 6 && argumentKinds[5] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_instr() sixth argument (flags) must be String, got {argumentKinds[5]}.");
        }

        if (argumentKinds.Length == 7 && !DataValue.IsIntegerKind(argumentKinds[6]))
        {
            throw new ArgumentException(
                $"regexp_instr() seventh argument (subexpr) must be Scalar, got {argumentKinds[6]}.");
        }

        return DataKind.Int32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull || arguments[1].IsNull)
        {
            return DataValue.Null(DataKind.Int32);
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

        int endoption = 0;
        if (arguments.Length >= 5 && !arguments[4].IsNull)
        {
            endoption = arguments[4].ToInt32();
        }

        RegexOptions options = RegexOptions.None;
        if (arguments.Length >= 6 && !arguments[5].IsNull)
        {
            string flags = arguments[5].AsString();
            if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        }

        int subexpr = 0;
        if (arguments.Length == 7 && !arguments[6].IsNull)
        {
            subexpr = arguments[6].ToInt32();
        }

        if (start >= input.Length)
        {
            return DataValue.FromInt32(0);
        }

        string searchIn = input[start..];
        MatchCollection matches = Regex.Matches(searchIn, pattern, options);

        if (n < 1 || n > matches.Count)
        {
            return DataValue.FromInt32(0);
        }

        Match match = matches[n - 1];

        if (subexpr > 0)
        {
            if (subexpr >= match.Groups.Count)
            {
                return DataValue.FromInt32(0);
            }

            Group group = match.Groups[subexpr];
            if (!group.Success)
            {
                return DataValue.FromInt32(0);
            }

            // Return 1-based position relative to original string
            int pos = start + group.Index;
            return DataValue.FromInt32(endoption == 0 ? pos + 1 : pos + group.Length + 1);
        }

        int matchPos = start + match.Index;
        return DataValue.FromInt32(endoption == 0 ? matchPos + 1 : matchPos + match.Length + 1);
    }
}

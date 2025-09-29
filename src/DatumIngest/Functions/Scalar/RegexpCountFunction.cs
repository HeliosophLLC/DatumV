using System.Text.RegularExpressions;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the number of times a POSIX regular expression pattern matches in a string.
/// <c>regexp_count(string, pattern [, start [, flags]])</c>
/// <c>start</c> is 1-based (default 1). <c>flags</c>: <c>'i'</c> for case-insensitive.
/// </summary>
public sealed class RegexpCountFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "regexp_count";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is < 2 or > 4)
        {
            throw new ArgumentException(
                "regexp_count() requires 2 to 4 arguments: string, pattern [, start [, flags]].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_count() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_count() second argument (pattern) must be String, got {argumentKinds[1]}.");
        }

        if (argumentKinds.Length >= 3 && argumentKinds[2] != DataKind.Float32)
        {
            throw new ArgumentException(
                $"regexp_count() third argument (start) must be Scalar, got {argumentKinds[2]}.");
        }

        if (argumentKinds.Length == 4 && argumentKinds[3] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_count() fourth argument (flags) must be String, got {argumentKinds[3]}.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull || arguments[1].IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        string input = arguments[0].AsString();
        string pattern = arguments[1].AsString();

        int start = 0;
        if (arguments.Length >= 3 && !arguments[2].IsNull)
        {
            start = (int)arguments[2].AsFloat32() - 1; // 1-based to 0-based
            if (start < 0) start = 0;
        }

        RegexOptions options = RegexOptions.None;
        if (arguments.Length == 4 && !arguments[3].IsNull)
        {
            string flags = arguments[3].AsString();
            if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        }

        if (start >= input.Length)
        {
            return DataValue.FromFloat32(0);
        }

        string searchIn = input[start..];
        int count = Regex.Matches(searchIn, pattern, options).Count;
        return DataValue.FromFloat32(count);
    }
}

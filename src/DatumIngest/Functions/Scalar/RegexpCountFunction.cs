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
            throw new FunctionArgumentException(Name, "requires 2 to 4 arguments: string, pattern [, start [, flags]].");
        }

        FunctionArgumentException.ThrowIfNotStringArgument(Name, 0, "string", argumentKinds[0]);
        FunctionArgumentException.ThrowIfNotStringArgument(Name, 1, "pattern", argumentKinds[1]);


        if (argumentKinds.Length >= 3)
        {
            FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 2, "start", argumentKinds[2]);
        }

        if (argumentKinds.Length == 4)
        {
            FunctionArgumentException.ThrowIfNotStringArgument(Name, 3, "flags", argumentKinds[3]);
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
            start = arguments[2].ToInt32() - 1; // 1-based to 0-based
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
            return DataValue.FromInt32(0);
        }

        string searchIn = input[start..];
        int count = Regex.Matches(searchIn, pattern, options).Count;
        return DataValue.FromInt32(count);
    }
}

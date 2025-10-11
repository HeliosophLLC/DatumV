using System.Text.RegularExpressions;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Splits a string using a POSIX regular expression as the delimiter, producing an array.
/// <c>regexp_split_to_array(string, pattern [, flags])</c>
/// <c>flags</c>: <c>'i'</c> for case-insensitive.
/// </summary>
public sealed class RegexpSplitToArrayFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "regexp_split_to_array";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new ArgumentException(
                "regexp_split_to_array() requires 2 or 3 arguments: string, pattern [, flags].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_split_to_array() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_split_to_array() second argument (pattern) must be String, got {argumentKinds[1]}.");
        }

        if (argumentKinds.Length == 3 && argumentKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_split_to_array() third argument (flags) must be String, got {argumentKinds[2]}.");
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

        string[] parts = Regex.Split(input, pattern, options);

        DataValue[] elements = new DataValue[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            elements[i] = DataValue.FromString(parts[i]);
        }

        return DataValue.FromArray(DataKind.String, elements);
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

        string[] parts = Regex.Split(input, pattern, options);

        DataValue[] elements = new DataValue[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            elements[i] = DataValue.FromString(parts[i], store);
        }

        return DataValue.FromArray(DataKind.String, elements);
    }
}

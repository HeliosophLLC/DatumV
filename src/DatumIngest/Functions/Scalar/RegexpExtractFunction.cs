using System.Text.RegularExpressions;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts a substring matching a regular expression pattern, optionally returning
/// a specific capture group.
/// <c>regexp_extract(input, pattern)</c> returns the full match.
/// <c>regexp_extract(input, pattern, group_index)</c> returns the specified capture group (1-based).
/// </summary>
public sealed class RegexpExtractFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "regexp_extract";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new ArgumentException("regexp_extract() requires 2 or 3 arguments.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_extract() requires a String as the first argument, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"regexp_extract() requires a String pattern as the second argument, got {argumentKinds[1]}.");
        }

        if (argumentKinds.Length == 3 && argumentKinds[2] != DataKind.Scalar)
        {
            throw new ArgumentException(
                $"regexp_extract() requires a Scalar group index as the third argument, got {argumentKinds[2]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        DataValue pattern = arguments[1];

        if (input.IsNull || pattern.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        int groupIndex = 0;
        if (arguments.Length == 3)
        {
            if (arguments[2].IsNull)
            {
                return DataValue.Null(DataKind.String);
            }

            groupIndex = (int)arguments[2].AsScalar();
        }

        Match match = Regex.Match(input.AsString(), pattern.AsString());

        if (!match.Success)
        {
            return DataValue.Null(DataKind.String);
        }

        if (groupIndex < 0 || groupIndex >= match.Groups.Count)
        {
            throw new InvalidOperationException(
                $"regexp_extract(): group index {groupIndex} is out of range (pattern has {match.Groups.Count - 1} capture groups).");
        }

        Group group = match.Groups[groupIndex];
        return group.Success
            ? DataValue.FromString(group.Value)
            : DataValue.Null(DataKind.String);
    }
}

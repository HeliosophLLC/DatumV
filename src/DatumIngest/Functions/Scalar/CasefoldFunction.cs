using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Performs Unicode case folding for case-insensitive comparison.
/// <c>casefold(text)</c> — PostgreSQL compatible.
/// Uses .NET's ToLowerInvariant which implements Unicode case folding rules.
/// </summary>
public sealed class CasefoldFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "casefold";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("casefold() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"casefold() requires a String argument, got {argumentKinds[0]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        return DataValue.FromString(arguments[0].AsString().ToLowerInvariant());
    }
}

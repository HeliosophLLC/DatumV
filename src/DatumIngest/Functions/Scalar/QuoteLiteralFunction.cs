using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the given string suitably quoted to be used as a SQL string literal.
/// <c>quote_literal(text)</c> — PostgreSQL compatible. Returns NULL on NULL input.
/// </summary>
public sealed class QuoteLiteralFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "quote_literal";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("quote_literal() requires exactly 1 argument.");
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

        string value = arguments[0].Kind == DataKind.String
            ? arguments[0].AsString()
            : arguments[0].ToDisplayString();

        return DataValue.FromString($"'{value.Replace("'", "''")}'");
    }
}

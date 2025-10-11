using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Like quote_literal, but returns the string 'NULL' for NULL input.
/// <c>quote_nullable(text)</c> — PostgreSQL compatible.
/// </summary>
public sealed class QuoteNullableFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "quote_nullable";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("quote_nullable() requires exactly 1 argument.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.FromString("NULL");
        }

        string value = arguments[0].Kind == DataKind.String
            ? arguments[0].AsString()
            : arguments[0].ToDisplayString();

        return DataValue.FromString($"'{value.Replace("'", "''")}'");
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.FromString("NULL", store);
        }

        string value = arguments[0].Kind == DataKind.String
            ? arguments[0].AsString(store)
            : arguments[0].ToDisplayString();

        return DataValue.FromString($"'{value.Replace("'", "''")}'", store);
    }
}

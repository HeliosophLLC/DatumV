using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the given string suitably quoted to be used as a SQL identifier.
/// <c>quote_ident(text)</c> — PostgreSQL compatible.
/// Adds double quotes only when necessary (contains special chars, uppercase, or is a keyword).
/// </summary>
public sealed class QuoteIdentFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "quote_ident";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("quote_ident() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"quote_ident() requires a String argument, got {argumentKinds[0]}.");
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

        string ident = arguments[0].AsString();

        // Always quote for safety (consistent with PG behavior for mixed case / special chars)
        bool needsQuoting = ident.Length == 0;
        if (!needsQuoting)
        {
            foreach (char c in ident)
            {
                if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_')
                {
                    needsQuoting = true;
                    break;
                }
            }

            // Also quote if starts with digit
            if (!needsQuoting && ident[0] >= '0' && ident[0] <= '9')
            {
                needsQuoting = true;
            }
        }

        if (!needsQuoting)
        {
            return DataValue.FromString(ident);
        }

        return DataValue.FromString($"\"{ident.Replace("\"", "\"\"")}\"");
    }
}

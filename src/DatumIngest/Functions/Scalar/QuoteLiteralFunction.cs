using System.Buffers;
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

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        if (arguments[0].Kind != DataKind.String)
        {
            // Non-string kinds produce a display string; quote that without span overhead.
            string display = arguments[0].ToDisplayString();
            string quoted = $"'{display.Replace("'", "''")}'";
            return DataValue.FromCharSpan(quoted.AsSpan(), store);
        }

        ReadOnlySpan<char> input = arguments[0].AsStringSpan(store, out char[] inputRented);

        // Count single quotes in input to size the output buffer:
        // output = 2 (surrounding quotes) + input.Length + number of embedded single quotes
        int singleQuoteCount = 0;
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '\'') singleQuoteCount++;
        }

        int outputLength = 2 + input.Length + singleQuoteCount;
        char[] outputRented = ArrayPool<char>.Shared.Rent(outputLength);

        try
        {
            int pos = 0;
            outputRented[pos++] = '\'';
            for (int i = 0; i < input.Length; i++)
            {
                outputRented[pos++] = input[i];
                if (input[i] == '\'')
                {
                    outputRented[pos++] = '\''; // escape embedded quote
                }
            }
            outputRented[pos++] = '\'';

            return DataValue.FromCharSpan(outputRented.AsSpan(0, pos), store);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(outputRented);
            if (inputRented is not null) ArrayPool<char>.Shared.Return(inputRented);
        }
    }
}

using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Concatenates strings with a separator, skipping null values.
/// <c>concat_ws(separator, str1, str2, ...)</c> requires at least 2 arguments
/// (separator plus one or more values). Null values are omitted entirely;
/// the separator is only inserted between non-null values.
/// </summary>
public sealed class ConcatWsFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "concat_ws";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 2)
        {
            throw new ArgumentException("concat_ws() requires at least 2 arguments: separator and one or more values.");
        }

        for (int i = 0; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] != DataKind.String)
            {
                throw new ArgumentException(
                    $"concat_ws() requires all arguments to be String, but argument {i + 1} is {argumentKinds[i]}.");
            }
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue separatorArgument = arguments[0];
        if (separatorArgument.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string separator = separatorArgument.AsString();
        StringBuilder builder = new();
        bool first = true;

        for (int i = 1; i < arguments.Length; i++)
        {
            if (arguments[i].IsNull)
            {
                continue;
            }

            if (!first)
            {
                builder.Append(separator);
            }

            builder.Append(arguments[i].AsString());
            first = false;
        }

        return DataValue.FromString(builder.ToString());
    }
}

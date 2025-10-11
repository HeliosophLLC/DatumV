using System.Buffers;
using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Concatenates two or more strings into a single string.
/// <c>concat(a, b, ...)</c> accepts a variable number of String arguments (minimum 2).
/// Null arguments are treated as empty strings (SQL concat semantics).
/// </summary>
public sealed class ConcatFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "concat";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 2)
        {
            throw new ArgumentException("concat() requires at least 2 arguments.");
        }

        for (int i = 0; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] != DataKind.String)
            {
                throw new ArgumentException($"concat() requires all arguments to be String, but argument {i} is {argumentKinds[i]}.");
            }
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        StringBuilder builder = new();

        for (int i = 0; i < arguments.Length; i++)
        {
            DataValue argument = arguments[i];
            if (!argument.IsNull)
            {
                builder.Append(argument.AsString());
            }
        }

        return DataValue.FromString(builder.ToString());
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        // Calculate total character count to rent a single buffer.
        int totalLength = 0;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (!arguments[i].IsNull)
            {
                totalLength += arguments[i].StringCharCount(store);
            }
        }

        char[] rented = ArrayPool<char>.Shared.Rent(totalLength);
        int position = 0;

        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].IsNull)
            {
                continue;
            }

            ReadOnlySpan<char> part = arguments[i].AsStringSpan(store, out char[] rentedPart);
            part.CopyTo(rented.AsSpan(position));
            position += part.Length;
            ArrayPool<char>.Shared.Return(rentedPart);
        }

        DataValue result = DataValue.FromCharSpan(rented.AsSpan(0, totalLength), store);
        ArrayPool<char>.Shared.Return(rented);
        return result;
    }
}

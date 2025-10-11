using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the number of bytes in a string when encoded as UTF-8.
/// <c>octet_length(string)</c> — PostgreSQL compatible.
/// </summary>
public sealed class OctetLengthFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "octet_length";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("octet_length() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"octet_length() requires a String argument, got {argumentKinds[0]}.");
        }

        return DataKind.Int32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Int32);
        }

        return DataValue.FromInt32(Encoding.UTF8.GetByteCount(input.AsString()));
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Int32);
        }

        return DataValue.FromInt32(input.AsUtf8Span(store).Length);
    }
}

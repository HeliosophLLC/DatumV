using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Converts a number to its hexadecimal representation.
/// <c>to_hex(integer)</c> — PostgreSQL compatible.
/// </summary>
public sealed class ToHexFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "to_hex";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("to_hex() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Float32)
        {
            throw new ArgumentException(
                $"to_hex() requires a Scalar argument, got {argumentKinds[0]}.");
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

        long value = arguments[0].ToInt64();
        return DataValue.FromString(value.ToString("x"));
    }
}

using System;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Converts a number to its binary representation.
/// <c>to_bin(integer)</c> — PostgreSQL compatible.
/// </summary>
public sealed class ToBinFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "to_bin";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("to_bin() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Float32)
        {
            throw new ArgumentException(
                $"to_bin() requires a Scalar argument, got {argumentKinds[0]}.");
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
        return DataValue.FromString(Convert.ToString(value, 2));
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        long value = arguments[0].ToInt64();
        return DataValue.FromString(Convert.ToString(value, 2), store);
    }
}

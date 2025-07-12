using DatumQuery.Model;

namespace DatumQuery.Functions.Scalar;

/// <summary>
/// Converts a Date or DateTime value to its Unix epoch representation as a Scalar.
/// <c>to_epoch(date)</c> returns epoch days (days since 1970-01-01).
/// <c>to_epoch(datetime)</c> returns epoch seconds (seconds since 1970-01-01T00:00:00Z).
/// </summary>
public sealed class ToEpochFunction : IScalarFunction
{
    private static readonly int UnixEpochDayNumber = DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;

    /// <inheritdoc />
    public string Name => "to_epoch";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("to_epoch() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"to_epoch() requires a Date or DateTime argument, got {argumentKinds[0]}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        return input.Kind switch
        {
            DataKind.Date => DataValue.FromScalar(input.AsDate().DayNumber - UnixEpochDayNumber),
            DataKind.DateTime => DataValue.FromScalar(
                (float)(input.AsDateTime().ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds),
            _ => throw new InvalidOperationException($"to_epoch() does not support {input.Kind}."),
        };
    }
}

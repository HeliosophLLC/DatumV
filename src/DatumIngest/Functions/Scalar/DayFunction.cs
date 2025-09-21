using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts the day of the month (1–31) from a Date or DateTime value as a Scalar.
/// Shorthand for <c>date_part('day', value)</c>.
/// </summary>
public sealed class DayFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "day";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("day() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"day() requires a Date or DateTime argument, got {argumentKinds[0]}.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        int day = input.ToDateTimeOffset().Day;

        return DataValue.FromFloat32(day);
    }
}

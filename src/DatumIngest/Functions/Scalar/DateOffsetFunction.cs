using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Adds a Duration to a Date, DateTime, or Time value.
/// <c>date_offset(date, duration)</c> — returns DateTime for Date/DateTime inputs, Time for Time inputs.
/// </summary>
public sealed class DateOffsetFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "date_offset";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("date_offset() requires exactly 2 arguments: date/datetime and duration.");
        }

        if (argumentKinds[0] is not (DataKind.Date or DataKind.DateTime or DataKind.Time))
        {
            throw new ArgumentException($"date_offset() first argument must be Date, DateTime, or Time, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.Duration)
        {
            throw new ArgumentException($"date_offset() second argument must be Duration, got {argumentKinds[1]}.");
        }

        return argumentKinds[0] == DataKind.Time ? DataKind.Time : DataKind.DateTime;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue dateValue = arguments[0];
        DataValue durationValue = arguments[1];

        if (dateValue.IsNull || durationValue.IsNull)
        {
            DataKind nullKind = dateValue.Kind == DataKind.Time ? DataKind.Time : DataKind.DateTime;
            return DataValue.Null(nullKind);
        }

        TimeSpan duration = durationValue.AsDuration();

        // Time + Duration → Time (wraps within 24 hours).
        if (dateValue.Kind == DataKind.Time)
        {
            TimeOnly baseTime = dateValue.AsTime();
            TimeOnly resultTime = baseTime.Add(duration);
            return DataValue.FromTime(resultTime);
        }

        DateTimeOffset baseDateTime = dateValue.ToDateTimeOffset();

        return DataValue.FromDateTime(baseDateTime + duration);
    }
}

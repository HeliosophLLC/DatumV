using System.Globalization;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Truncates a date or datetime to the specified precision.
/// <c>date_trunc('month', date_col)</c> returns the first day of the month.
/// </summary>
/// <remarks>
/// Returns the same kind as the input: Date input produces Date, DateTime produces DateTime.
/// Week truncation uses ISO 8601 weeks (Monday start).
/// </remarks>
public sealed class DateTruncFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "date_trunc";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("date_trunc() requires exactly 2 arguments: part (String) and date (Date/DateTime).");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException("date_trunc() first argument must be a String (date part name).");
        }

        if (argumentKinds[1] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"date_trunc() second argument must be Date or DateTime, got {argumentKinds[1]}.");
        }

        return argumentKinds[1];
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue partValue = arguments[0];
        DataValue dateValue = arguments[1];

        if (dateValue.IsNull)
        {
            return DataValue.Null(dateValue.Kind);
        }

        DatePartName part = DatePartParser.Parse(partValue.AsString());
        DateTimeOffset original = DateFunctionUtilities.ToDateTimeOffset(dateValue);

        DateTimeOffset result = part switch
        {
            DatePartName.Year => new DateTimeOffset(original.Year, 1, 1, 0, 0, 0, original.Offset),
            DatePartName.Quarter => new DateTimeOffset(original.Year, ((original.Month - 1) / 3) * 3 + 1, 1, 0, 0, 0, original.Offset),
            DatePartName.Month => new DateTimeOffset(original.Year, original.Month, 1, 0, 0, 0, original.Offset),
            DatePartName.Week => TruncateToWeek(original),
            DatePartName.Day => new DateTimeOffset(original.Year, original.Month, original.Day, 0, 0, 0, original.Offset),
            DatePartName.Hour => new DateTimeOffset(original.Year, original.Month, original.Day, original.Hour, 0, 0, original.Offset),
            DatePartName.Minute => new DateTimeOffset(original.Year, original.Month, original.Day, original.Hour, original.Minute, 0, original.Offset),
            DatePartName.Second => new DateTimeOffset(original.Year, original.Month, original.Day, original.Hour, original.Minute, original.Second, original.Offset),
            DatePartName.Millisecond => new DateTimeOffset(original.Year, original.Month, original.Day, original.Hour, original.Minute, original.Second, original.Millisecond, original.Offset),
            _ => throw new ArgumentException($"Unsupported date part for date_trunc: {part}."),
        };

        return DateFunctionUtilities.WrapResult(result, dateValue.Kind);
    }

    /// <summary>
    /// Truncates to the ISO 8601 week start (Monday).
    /// </summary>
    private static DateTimeOffset TruncateToWeek(DateTimeOffset value)
    {
        int daysSinceMonday = ((int)value.DayOfWeek + 6) % 7;
        DateTimeOffset monday = value.AddDays(-daysSinceMonday);
        return new DateTimeOffset(monday.Year, monday.Month, monday.Day, 0, 0, 0, value.Offset);
    }
}

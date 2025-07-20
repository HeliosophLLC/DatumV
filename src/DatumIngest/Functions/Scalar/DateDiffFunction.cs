using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Computes the difference between two dates in the specified date part units.
/// <c>date_diff('day', start, end)</c> returns the number of day boundaries crossed.
/// </summary>
/// <remarks>
/// Follows DuckDB semantics: returns the count of part boundaries crossed, not fractional.
/// <c>date_diff('month', '2024-01-31', '2024-02-01')</c> returns 1.
/// </remarks>
public sealed class DateDiffFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "date_diff";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
        {
            throw new ArgumentException("date_diff() requires exactly 3 arguments: part (String), start (Date/DateTime), end (Date/DateTime).");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException("date_diff() first argument must be a String (date part name).");
        }

        if (argumentKinds[1] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"date_diff() second argument must be Date or DateTime, got {argumentKinds[1]}.");
        }

        if (argumentKinds[2] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"date_diff() third argument must be Date or DateTime, got {argumentKinds[2]}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue partValue = arguments[0];
        DataValue startValue = arguments[1];
        DataValue endValue = arguments[2];

        if (startValue.IsNull || endValue.IsNull)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        DatePartName part = DatePartParser.Parse(partValue.AsString());
        DateTimeOffset start = DateFunctionUtilities.ToDateTimeOffset(startValue);
        DateTimeOffset end = DateFunctionUtilities.ToDateTimeOffset(endValue);

        float difference = part switch
        {
            DatePartName.Year => end.Year - start.Year,
            DatePartName.Quarter => (end.Year - start.Year) * 4 + ((end.Month - 1) / 3 - (start.Month - 1) / 3),
            DatePartName.Month => (end.Year - start.Year) * 12 + (end.Month - start.Month),
            DatePartName.Week => (float)System.Math.Truncate((end - start).TotalDays / 7),
            DatePartName.Day => (float)System.Math.Truncate((end - start).TotalDays),
            DatePartName.Hour => (float)System.Math.Truncate((end - start).TotalHours),
            DatePartName.Minute => (float)System.Math.Truncate((end - start).TotalMinutes),
            DatePartName.Second => (float)System.Math.Truncate((end - start).TotalSeconds),
            DatePartName.Millisecond => (float)System.Math.Truncate((end - start).TotalMilliseconds),
            _ => throw new ArgumentException($"Unsupported date part for date_diff: {part}."),
        };

        return DataValue.FromScalar(difference);
    }
}

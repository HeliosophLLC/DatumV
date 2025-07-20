using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Buckets a date or datetime into fixed-width intervals of the specified date part.
/// <c>date_bucket('minute', 15, datetime_col)</c> buckets into 15-minute intervals.
/// </summary>
/// <remarks>
/// An optional fourth argument specifies the bucket origin (defaults to 2000-01-01T00:00:00Z
/// for DateTime and 2000-01-01 for Date, matching SQL Server behavior).
/// Returns the same kind as the date input.
/// </remarks>
public sealed class DateBucketFunction : IScalarFunction
{
    private static readonly DateTimeOffset DefaultOrigin = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public string Name => "date_bucket";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is < 3 or > 4)
        {
            throw new ArgumentException("date_bucket() requires 3 or 4 arguments: part (String), width (Scalar), date (Date/DateTime) [, origin (Date/DateTime)].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException("date_bucket() first argument must be a String (date part name).");
        }

        if (argumentKinds[1] != DataKind.Scalar)
        {
            throw new ArgumentException($"date_bucket() second argument must be Scalar (bucket width), got {argumentKinds[1]}.");
        }

        if (argumentKinds[2] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"date_bucket() third argument must be Date or DateTime, got {argumentKinds[2]}.");
        }

        if (argumentKinds.Length == 4 && argumentKinds[3] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"date_bucket() fourth argument (origin) must be Date or DateTime, got {argumentKinds[3]}.");
        }

        return argumentKinds[2];
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue partValue = arguments[0];
        DataValue widthValue = arguments[1];
        DataValue dateValue = arguments[2];

        if (dateValue.IsNull)
        {
            return DataValue.Null(dateValue.Kind);
        }

        DatePartName part = DatePartParser.Parse(partValue.AsString());
        int width = (int)widthValue.AsScalar();

        if (width <= 0)
        {
            throw new ArgumentException("date_bucket() width must be a positive integer.");
        }

        DateTimeOffset date = DateFunctionUtilities.ToDateTimeOffset(dateValue);
        DateTimeOffset origin = arguments.Length == 4 && !arguments[3].IsNull
            ? DateFunctionUtilities.ToDateTimeOffset(arguments[3])
            : DefaultOrigin;

        DateTimeOffset result = part switch
        {
            DatePartName.Year => BucketByMonths(date, origin, width * 12),
            DatePartName.Quarter => BucketByMonths(date, origin, width * 3),
            DatePartName.Month => BucketByMonths(date, origin, width),
            DatePartName.Week => BucketByTicks(date, origin, TimeSpan.FromDays(width * 7).Ticks),
            DatePartName.Day => BucketByTicks(date, origin, TimeSpan.FromDays(width).Ticks),
            DatePartName.Hour => BucketByTicks(date, origin, TimeSpan.FromHours(width).Ticks),
            DatePartName.Minute => BucketByTicks(date, origin, TimeSpan.FromMinutes(width).Ticks),
            DatePartName.Second => BucketByTicks(date, origin, TimeSpan.FromSeconds(width).Ticks),
            DatePartName.Millisecond => BucketByTicks(date, origin, TimeSpan.FromMilliseconds(width).Ticks),
            _ => throw new ArgumentException($"Unsupported date part for date_bucket: {part}."),
        };

        return DateFunctionUtilities.WrapResult(result, dateValue.Kind);
    }

    /// <summary>
    /// Buckets by calendar months (non-uniform intervals).
    /// </summary>
    private static DateTimeOffset BucketByMonths(DateTimeOffset date, DateTimeOffset origin, int widthMonths)
    {
        int totalMonths = (date.Year - origin.Year) * 12 + (date.Month - origin.Month);

        // Floor toward negative infinity for dates before origin.
        int bucketIndex = totalMonths >= 0
            ? totalMonths / widthMonths
            : (totalMonths - widthMonths + 1) / widthMonths;

        return origin.AddMonths(bucketIndex * widthMonths);
    }

    /// <summary>
    /// Buckets by uniform tick-based intervals.
    /// </summary>
    private static DateTimeOffset BucketByTicks(DateTimeOffset date, DateTimeOffset origin, long widthTicks)
    {
        long ticksDelta = date.Ticks - origin.Ticks;

        // Floor toward negative infinity for dates before origin.
        long bucketIndex = ticksDelta >= 0
            ? ticksDelta / widthTicks
            : (ticksDelta - widthTicks + 1) / widthTicks;

        return new DateTimeOffset(origin.Ticks + bucketIndex * widthTicks, origin.Offset);
    }
}

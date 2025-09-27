using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// PostgreSQL-compatible <c>date_bin(interval, source, origin)</c> function.
/// Buckets a timestamp into fixed-width intervals aligned to an origin point.
/// </summary>
/// <remarks>
/// <para>
/// The interval is a PostgreSQL-style string such as <c>'15 minutes'</c>,
/// <c>'1 hour'</c>, or <c>'1 day 2 hours'</c>. Only fixed-length intervals
/// are supported — month and year intervals are rejected.
/// </para>
/// <para>
/// This matches PostgreSQL's <c>date_bin</c> semantics: the result is the
/// largest multiple of the interval that is less than or equal to the source,
/// anchored at the origin.
/// </para>
/// </remarks>
public sealed class DateBinFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "date_bin";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
        {
            throw new ArgumentException("date_bin() requires exactly 3 arguments: interval (String), source (Date/DateTime), origin (Date/DateTime).");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException("date_bin() first argument must be a String (interval).");
        }

        if (argumentKinds[1] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"date_bin() second argument must be Date or DateTime, got {argumentKinds[1]}.");
        }

        if (argumentKinds[2] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"date_bin() third argument must be Date or DateTime, got {argumentKinds[2]}.");
        }

        return argumentKinds[1];
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue intervalValue = arguments[0];
        DataValue sourceValue = arguments[1];
        DataValue originValue = arguments[2];

        if (sourceValue.IsNull)
        {
            return DataValue.Null(sourceValue.Kind);
        }

        TimeSpan interval = IntervalParser.Parse(intervalValue.AsString());

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentException("date_bin() interval must be greater than zero.");
        }

        DateTimeOffset source = DateFunctionUtilities.ToDateTimeOffset(sourceValue);
        DateTimeOffset origin = DateFunctionUtilities.ToDateTimeOffset(originValue);

        long widthTicks = interval.Ticks;
        long ticksDelta = source.Ticks - origin.Ticks;

        // Floor toward negative infinity for sources before origin.
        long bucketIndex = ticksDelta >= 0
            ? ticksDelta / widthTicks
            : (ticksDelta - widthTicks + 1) / widthTicks;

        DateTimeOffset result = new(origin.Ticks + bucketIndex * widthTicks, origin.Offset);

        return DateFunctionUtilities.WrapResult(result, sourceValue.Kind);
    }
}

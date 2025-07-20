using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Shared utilities for date function implementations.
/// </summary>
internal static class DateFunctionUtilities
{
    /// <summary>
    /// Extracts a <see cref="DateTimeOffset"/> from a Date or DateTime <see cref="DataValue"/>.
    /// Date values are converted to midnight UTC.
    /// </summary>
    internal static DateTimeOffset ToDateTimeOffset(DataValue value)
    {
        return value.Kind == DataKind.Date
            ? new DateTimeOffset(value.AsDate().ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : value.AsDateTime();
    }

    /// <summary>
    /// Wraps a <see cref="DateTimeOffset"/> result back into the appropriate <see cref="DataValue"/>
    /// based on the original input kind (Date stays Date, DateTime stays DateTime).
    /// </summary>
    internal static DataValue WrapResult(DateTimeOffset result, DataKind originalKind)
    {
        return originalKind == DataKind.Date
            ? DataValue.FromDate(DateOnly.FromDateTime(result.DateTime))
            : DataValue.FromDateTime(result);
    }
}

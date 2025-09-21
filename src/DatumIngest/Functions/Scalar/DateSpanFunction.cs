using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Computes the Duration between two Date or DateTime values.
/// <c>date_span(start, end)</c> — returns the elapsed time span from start to end.
/// Both arguments must be the same temporal kind (Date or DateTime).
/// </summary>
public sealed class DateSpanFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "date_span";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("date_span() requires exactly 2 arguments: start and end.");
        }

        if (argumentKinds[0] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"date_span() first argument must be Date or DateTime, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"date_span() second argument must be Date or DateTime, got {argumentKinds[1]}.");
        }

        return DataKind.Duration;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue start = arguments[0];
        DataValue end = arguments[1];

        if (start.IsNull || end.IsNull)
        {
            return DataValue.Null(DataKind.Duration);
        }

        DateTimeOffset startDateTime = start.ToDateTimeOffset();
        DateTimeOffset endDateTime = end.ToDateTimeOffset();

        return DataValue.FromDuration(endDateTime - startDateTime);
    }
}

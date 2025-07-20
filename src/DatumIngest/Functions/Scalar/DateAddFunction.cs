using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Adds a specified number of date part units to a date or datetime.
/// <c>date_add('month', 3, date_col)</c> adds 3 months.
/// </summary>
/// <remarks>
/// Returns the same kind as the input: Date input produces Date, DateTime produces DateTime.
/// </remarks>
public sealed class DateAddFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "date_add";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
        {
            throw new ArgumentException("date_add() requires exactly 3 arguments: part (String), number (Scalar), date (Date/DateTime).");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException("date_add() first argument must be a String (date part name).");
        }

        if (argumentKinds[1] != DataKind.Scalar)
        {
            throw new ArgumentException($"date_add() second argument must be Scalar (amount to add), got {argumentKinds[1]}.");
        }

        if (argumentKinds[2] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"date_add() third argument must be Date or DateTime, got {argumentKinds[2]}.");
        }

        return argumentKinds[2];
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue partValue = arguments[0];
        DataValue amountValue = arguments[1];
        DataValue dateValue = arguments[2];

        if (dateValue.IsNull || amountValue.IsNull)
        {
            return DataValue.Null(dateValue.Kind);
        }

        DatePartName part = DatePartParser.Parse(partValue.AsString());
        int amount = (int)amountValue.AsScalar();
        DateTimeOffset original = DateFunctionUtilities.ToDateTimeOffset(dateValue);

        DateTimeOffset result = part switch
        {
            DatePartName.Year => original.AddMonths(amount * 12),
            DatePartName.Quarter => original.AddMonths(amount * 3),
            DatePartName.Month => original.AddMonths(amount),
            DatePartName.Week => original.AddDays(amount * 7),
            DatePartName.Day => original.AddDays(amount),
            DatePartName.Hour => original.AddHours(amount),
            DatePartName.Minute => original.AddMinutes(amount),
            DatePartName.Second => original.AddSeconds(amount),
            DatePartName.Millisecond => original.AddMilliseconds(amount),
            _ => throw new ArgumentException($"Unsupported date part for date_add: {part}."),
        };

        return DateFunctionUtilities.WrapResult(result, dateValue.Kind);
    }
}

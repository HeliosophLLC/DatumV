using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the ISO day of the week (1=Monday through 7=Sunday) from a Date or DateTime value as a Scalar.
/// Uses ISO 8601 convention where Monday is 1.
/// </summary>
public sealed class DayOfWeekFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "dayofweek";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("dayofweek() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"dayofweek() requires a Date or DateTime argument, got {argumentKinds[0]}.");
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

        DayOfWeek dotnetDayOfWeek = input.Kind == DataKind.Date
            ? input.AsDate().DayOfWeek
            : input.AsDateTime().DayOfWeek;

        // Convert from .NET convention (0=Sunday) to ISO 8601 (1=Monday, 7=Sunday).
        int isoDayOfWeek = dotnetDayOfWeek == System.DayOfWeek.Sunday ? 7 : (int)dotnetDayOfWeek;
        return DataValue.FromScalar(isoDayOfWeek);
    }
}

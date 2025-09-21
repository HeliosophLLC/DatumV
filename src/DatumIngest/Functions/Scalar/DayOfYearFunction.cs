using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the day of the year (1–366) from a Date or DateTime value as a Scalar.
/// </summary>
public sealed class DayOfYearFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "dayofyear";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("dayofyear() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"dayofyear() requires a Date or DateTime argument, got {argumentKinds[0]}.");
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

        int dayOfYear = input.ToDateTimeOffset().DayOfYear;

        return DataValue.FromFloat32(dayOfYear);
    }
}

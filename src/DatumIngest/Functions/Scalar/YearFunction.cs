using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts the year from a Date or DateTime value as a Scalar.
/// Shorthand for <c>date_part('year', value)</c>.
/// </summary>
public sealed class YearFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "year";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("year() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"year() requires a Date or DateTime argument, got {argumentKinds[0]}.");
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

        int year = input.ToDateTimeOffset().Year;

        return DataValue.FromFloat32(year);
    }
}

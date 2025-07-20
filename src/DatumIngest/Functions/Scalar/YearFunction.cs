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

        int year = input.Kind == DataKind.Date
            ? input.AsDate().Year
            : input.AsDateTime().Year;

        return DataValue.FromScalar(year);
    }
}

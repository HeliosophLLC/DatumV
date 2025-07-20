using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts the quarter (1–4) from a Date or DateTime value as a Scalar.
/// Shorthand for <c>date_part('quarter', value)</c>.
/// </summary>
public sealed class QuarterFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "quarter";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("quarter() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"quarter() requires a Date or DateTime argument, got {argumentKinds[0]}.");
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

        int month = input.Kind == DataKind.Date
            ? input.AsDate().Month
            : input.AsDateTime().Month;

        int quarter = (month - 1) / 3 + 1;
        return DataValue.FromScalar(quarter);
    }
}

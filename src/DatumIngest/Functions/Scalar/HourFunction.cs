using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts the hour (0–23) from a DateTime value as a Scalar.
/// For Date inputs, always returns 0.
/// </summary>
public sealed class HourFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "hour";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("hour() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"hour() requires a Date or DateTime argument, got {argumentKinds[0]}.");
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

        int hour = input.Kind == DataKind.Date
            ? 0
            : input.AsDateTime().Hour;

        return DataValue.FromScalar(hour);
    }
}

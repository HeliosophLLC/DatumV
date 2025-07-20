using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts the minute (0–59) from a DateTime value as a Scalar.
/// For Date inputs, always returns 0.
/// </summary>
public sealed class MinuteFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "minute";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("minute() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"minute() requires a Date or DateTime argument, got {argumentKinds[0]}.");
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

        int minute = input.Kind == DataKind.Date
            ? 0
            : input.AsDateTime().Minute;

        return DataValue.FromScalar(minute);
    }
}

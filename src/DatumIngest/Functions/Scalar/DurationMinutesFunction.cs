using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the total minutes of a Duration as a Scalar.
/// <c>duration_minutes(duration)</c>
/// </summary>
public sealed class DurationMinutesFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "duration_minutes";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("duration_minutes() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Duration)
        {
            throw new ArgumentException($"duration_minutes() requires a Duration argument, got {argumentKinds[0]}.");
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

        return DataValue.FromScalar((float)input.AsDuration().TotalMinutes);
    }
}

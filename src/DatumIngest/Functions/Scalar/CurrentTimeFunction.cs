using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the current UTC time-of-day.
/// <c>current_time()</c> — takes no arguments.
/// </summary>
public sealed class CurrentTimeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "current_time";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new ArgumentException("current_time() takes no arguments.");
        }

        return DataKind.Time;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        return DataValue.FromTime(TimeOnly.FromTimeSpan(DateTimeOffset.UtcNow.TimeOfDay));
    }
}

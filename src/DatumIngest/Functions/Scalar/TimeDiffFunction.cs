using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Computes the Duration between two Time values.
/// <c>time_diff(start_time, end_time)</c> — returns the elapsed time from start to end.
/// </summary>
public sealed class TimeDiffFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "time_diff";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("time_diff() requires exactly 2 arguments: start_time and end_time.");
        }

        if (argumentKinds[0] != DataKind.Time)
        {
            throw new ArgumentException($"time_diff() first argument must be Time, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.Time)
        {
            throw new ArgumentException($"time_diff() second argument must be Time, got {argumentKinds[1]}.");
        }

        return DataKind.Duration;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue startValue = arguments[0];
        DataValue endValue = arguments[1];

        if (startValue.IsNull || endValue.IsNull)
        {
            return DataValue.Null(DataKind.Duration);
        }

        TimeOnly startTime = startValue.AsTime();
        TimeOnly endTime = endValue.AsTime();

        TimeSpan elapsed = endTime - startTime;

        return DataValue.FromDuration(elapsed);
    }
}

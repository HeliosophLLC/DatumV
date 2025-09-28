using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the actual current time, which changes even within a single SQL statement.
/// This is the PostgreSQL <c>clock_timestamp()</c> function.
/// Unlike <c>now()</c> and <c>CURRENT_TIMESTAMP</c>, this is NOT transaction/batch-stable.
/// </summary>
public sealed class ClockTimestampFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "clock_timestamp";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new ArgumentException("clock_timestamp() takes no arguments.");
        }

        return DataKind.DateTime;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        return DataValue.FromDateTime(DateTimeOffset.UtcNow);
    }
}

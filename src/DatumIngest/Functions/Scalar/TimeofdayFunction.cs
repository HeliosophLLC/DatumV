using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the actual current time as a formatted text string.
/// This is the historical PostgreSQL <c>timeofday()</c> function.
/// Like <c>clock_timestamp()</c>, the value changes even within a single SQL statement.
/// </summary>
/// <remarks>
/// PostgreSQL format: <c>Thu Jan 01 00:00:00.000000 1970 UTC</c>.
/// DatumIngest uses ISO 8601: <c>2026-04-15T14:30:45.1234567+00:00</c>.
/// </remarks>
public sealed class TimeofdayFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "timeofday";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new ArgumentException("timeofday() takes no arguments.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        return DataValue.FromString(DateTimeOffset.UtcNow.ToString("O"));
    }
}

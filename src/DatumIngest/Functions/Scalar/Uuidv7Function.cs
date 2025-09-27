using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Generates a time-ordered version-7 UUID (RFC 9562).
/// <c>uuidv7()</c> — returns a new time-sorted UUID each invocation.
/// <c>uuidv7(shift)</c> — shifts the embedded timestamp by the given Duration.
/// PostgreSQL 18 compatible. Version-7 UUIDs embed a Unix timestamp in milliseconds,
/// making them monotonically increasing and suitable for use as database primary keys.
/// </summary>
public sealed class Uuidv7Function : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "uuidv7";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length > 1)
        {
            throw new ArgumentException("uuidv7() takes zero or one argument.");
        }

        if (argumentKinds.Length == 1 && argumentKinds[0] != DataKind.Duration)
        {
            throw new ArgumentException("uuidv7() optional argument must be Duration.");
        }

        return DataKind.Uuid;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments.Length == 0 || arguments[0].IsNull)
        {
            return DataValue.FromUuid(Guid.CreateVersion7());
        }

        TimeSpan shift = arguments[0].AsDuration();
        DateTimeOffset shiftedTime = DateTimeOffset.UtcNow + shift;
        return DataValue.FromUuid(Guid.CreateVersion7(shiftedTime));
    }
}

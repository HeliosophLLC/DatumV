using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Generates a time-ordered version-7 UUID (RFC 9562).
/// <c>uuid7()</c> — takes no arguments, returns a new time-sorted UUID each invocation.
/// Version-7 UUIDs embed a Unix timestamp in milliseconds, making them monotonically
/// increasing and suitable for use as database primary keys.
/// </summary>
public sealed class Uuid7Function : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "uuid7";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new ArgumentException("uuid7() takes no arguments.");
        }

        return DataKind.Uuid;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        return DataValue.FromUuid(Guid.CreateVersion7());
    }
}

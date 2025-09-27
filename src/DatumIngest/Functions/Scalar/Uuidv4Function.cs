using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Generates a random version-4 UUID (RFC 9562).
/// <c>uuidv4()</c> — takes no arguments, returns a new random UUID each invocation.
/// PostgreSQL 18 compatible. Also registered as <c>gen_random_uuid()</c>.
/// </summary>
public sealed class Uuidv4Function : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "uuidv4";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new ArgumentException("uuidv4() takes no arguments.");
        }

        return DataKind.Uuid;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        return DataValue.FromUuid(Guid.NewGuid());
    }
}

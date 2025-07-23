using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Generates a random version-4 UUID (RFC 9562).
/// <c>uuid4()</c> — takes no arguments, returns a new random UUID each invocation.
/// </summary>
public sealed class Uuid4Function : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "uuid4";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new ArgumentException("uuid4() takes no arguments.");
        }

        return DataKind.Uuid;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        return DataValue.FromUuid(Guid.NewGuid());
    }
}

using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts the version number from a UUID.
/// <c>uuid_version(uuid)</c> — returns the version (4 for random, 7 for time-ordered, etc.) as a <see cref="DataKind.Float32"/>.
/// </summary>
public sealed class UuidVersionFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "uuid_version";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("uuid_version() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Uuid)
        {
            throw new ArgumentException("uuid_version() argument must be Uuid.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        Guid uuid = input.AsUuid();
        int version = uuid.Version;
        return DataValue.FromFloat32(version);
    }
}

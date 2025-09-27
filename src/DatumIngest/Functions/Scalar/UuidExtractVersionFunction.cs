using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts the version number from a UUID of variants described by RFC 9562.
/// <c>uuid_extract_version(uuid)</c> — returns the version as a <see cref="DataKind.Int16"/>.
/// For non-RFC 9562 variant UUIDs, returns null.
/// PostgreSQL 18 compatible.
/// </summary>
public sealed class UuidExtractVersionFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "uuid_extract_version";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("uuid_extract_version() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Uuid)
        {
            throw new ArgumentException("uuid_extract_version() argument must be Uuid.");
        }

        return DataKind.Int16;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Int16);
        }

        Guid uuid = input.AsUuid();

        // Check for RFC 9562 variant (variant bits = 10xx in byte 8 of big-endian layout).
        byte[] bytes = uuid.ToByteArray(bigEndian: true);
        byte variantByte = bytes[8];
        bool isRfc9562Variant = (variantByte & 0xC0) == 0x80;

        if (!isRfc9562Variant)
        {
            return DataValue.Null(DataKind.Int16);
        }

        return DataValue.FromInt16((short)uuid.Version);
    }
}

using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts the raw bytes of a UUID as a 16-element <see cref="DataKind.UInt8Array"/>.
/// <c>uuid_bytes(uuid)</c> — returns the big-endian byte representation.
/// </summary>
public sealed class UuidBytesFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "uuid_bytes";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("uuid_bytes() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Uuid)
        {
            throw new ArgumentException("uuid_bytes() argument must be Uuid.");
        }

        return DataKind.UInt8Array;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.UInt8Array);
        }

        byte[] bytes = input.AsUuid().ToByteArray(bigEndian: true);
        return DataValue.FromUInt8Array(bytes);
    }
}

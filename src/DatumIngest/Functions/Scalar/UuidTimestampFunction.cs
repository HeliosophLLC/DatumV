using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts the embedded timestamp from a version 7 (time-ordered) UUID.
/// <c>uuid_timestamp(uuid)</c> — returns the millisecond-precision creation time as a <see cref="DataKind.DateTime"/>.
/// For non-v7 UUIDs, returns null.
/// </summary>
public sealed class UuidTimestampFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "uuid_timestamp";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("uuid_timestamp() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Uuid)
        {
            throw new ArgumentException("uuid_timestamp() argument must be Uuid.");
        }

        return DataKind.DateTime;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.DateTime);
        }

        Guid uuid = input.AsUuid();

        if (uuid.Version != 7)
        {
            return DataValue.Null(DataKind.DateTime);
        }

        // UUID v7 stores a Unix timestamp in milliseconds in the most significant 48 bits.
        byte[] bytes = uuid.ToByteArray(bigEndian: true);

        long timestampMilliseconds = ((long)bytes[0] << 40)
                                   | ((long)bytes[1] << 32)
                                   | ((long)bytes[2] << 24)
                                   | ((long)bytes[3] << 16)
                                   | ((long)bytes[4] << 8)
                                   | bytes[5];

        DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMilliseconds);
        return DataValue.FromDateTime(timestamp.UtcDateTime);
    }
}

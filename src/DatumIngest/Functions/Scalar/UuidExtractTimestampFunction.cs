using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts the embedded timestamp from a version 1 or version 7 UUID.
/// <c>uuid_extract_timestamp(uuid)</c> — returns the creation time as a <see cref="DataKind.DateTime"/>.
/// For other UUID versions, returns null.
/// PostgreSQL 18 compatible.
/// </summary>
public sealed class UuidExtractTimestampFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "uuid_extract_timestamp";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("uuid_extract_timestamp() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Uuid)
        {
            throw new ArgumentException("uuid_extract_timestamp() argument must be Uuid.");
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

        if (uuid.Version == 7)
        {
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

        if (uuid.Version == 1)
        {
            // UUID v1 stores a 60-bit timestamp as 100-nanosecond intervals since 1582-10-15.
            byte[] bytes = uuid.ToByteArray(bigEndian: true);

            long timeLow = ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3];
            long timeMid = ((long)bytes[4] << 8) | bytes[5];
            long timeHi = ((long)(bytes[6] & 0x0F) << 8) | bytes[7]; // mask off version bits

            long timestamp100Ns = (timeHi << 48) | (timeMid << 32) | timeLow;

            // UUID v1 epoch is 1582-10-15T00:00:00Z; offset to Unix epoch.
            const long gregorianToUnixOffset = 122192928000000000L; // 100-ns intervals
            long unixTicks = timestamp100Ns - gregorianToUnixOffset;
            DateTimeOffset timestamp = new(unixTicks + DateTimeOffset.UnixEpoch.Ticks, TimeSpan.Zero);
            return DataValue.FromDateTime(timestamp.UtcDateTime);
        }

        return DataValue.Null(DataKind.DateTime);
    }
}

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Uuid;

/// <summary>
/// Returns the embedded timestamp from an RFC 9562 UUID of version 1, 6, or 7.
/// Returns NULL for other versions or non-RFC 9562 variants.
/// </summary>
public sealed class UuidExtractTimestampFunction : IFunction, IScalarFunction
{
    private static readonly DateTimeOffset GregorianEpoch = new(1582, 10, 15, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public static string Name => "uuid_extract_timestamp";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Uuid;

    /// <inheritdoc />
    public static string Description =>
        "Returns the embedded timestamp from a version 1, 6, or 7 UUID. NULL for other versions or non-RFC 9562 variants.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("value", DataKindMatcher.Exact(DataKind.Uuid))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.DateTime)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<UuidExtractTimestampFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef input = args[0];
        if (input.IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.DateTime));

        Span<byte> bytes = stackalloc byte[16];
        input.AsUuid().TryWriteBytes(bytes, bigEndian: true, out _);

        if ((bytes[8] & 0xC0) != 0x80)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.DateTime));

        int version = bytes[6] >> 4;
        return version switch
        {
            1 => new(ValueRef.FromDateTime(ExtractV1Timestamp(bytes))),
            6 => new(ValueRef.FromDateTime(ExtractV6Timestamp(bytes))),
            7 => new(ValueRef.FromDateTime(ExtractV7Timestamp(bytes))),
            _ => new(ValueRef.Null(DataKind.DateTime)),
        };
    }

    private static DateTimeOffset ExtractV1Timestamp(ReadOnlySpan<byte> bytes)
    {
        long timeLow = ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3];
        long timeMid = ((long)bytes[4] << 8) | bytes[5];
        long timeHi = ((long)(bytes[6] & 0x0F) << 8) | bytes[7];
        long ticks100ns = (timeHi << 48) | (timeMid << 32) | timeLow;
        return GregorianEpoch.AddTicks(ticks100ns);
    }

    private static DateTimeOffset ExtractV6Timestamp(ReadOnlySpan<byte> bytes)
    {
        long hi = ((long)bytes[0] << 52) | ((long)bytes[1] << 44) | ((long)bytes[2] << 36)
                | ((long)bytes[3] << 28) | ((long)bytes[4] << 20) | ((long)bytes[5] << 12);
        long lo = ((long)(bytes[6] & 0x0F) << 8) | bytes[7];
        return GregorianEpoch.AddTicks(hi | lo);
    }

    private static DateTimeOffset ExtractV7Timestamp(ReadOnlySpan<byte> bytes)
    {
        long unixMs = ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24)
                    | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }
}

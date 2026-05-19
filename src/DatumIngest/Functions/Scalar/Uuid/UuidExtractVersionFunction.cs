using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Uuid;

/// <summary>
/// Returns the version number of an RFC 9562 UUID, or NULL for non-RFC 9562 variants.
/// </summary>
public sealed class UuidExtractVersionFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "uuid_extract_version";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Uuid;

    /// <inheritdoc />
    public static string Description =>
        "Returns the version number of an RFC 9562 UUID, or NULL for non-RFC 9562 variants.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("value", DataKindMatcher.Exact(DataKind.Uuid))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int16)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<UuidExtractVersionFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef input = args[0];
        if (input.IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int16));

        Span<byte> bytes = stackalloc byte[16];
        input.AsUuid().TryWriteBytes(bytes, bigEndian: true, out _);

        if ((bytes[8] & 0xC0) != 0x80)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int16));

        short version = (short)(bytes[6] >> 4);
        return new ValueTask<ValueRef>(ValueRef.FromInt16(version));
    }
}

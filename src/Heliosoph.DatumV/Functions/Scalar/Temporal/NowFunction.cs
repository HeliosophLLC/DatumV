using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// Returns the current UTC timestamp as a <see cref="DataKind.TimestampTz"/> (PG <c>now()</c>).
/// </summary>
public sealed class NowFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "now";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description => "Returns the current UTC timestamp.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.TimestampTz)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<NowFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ValueRef.FromTimestampTz(DateTimeOffset.UtcNow));

    // CSE in this codebase is per-row, so "pure" here means "row-local pure":
    // multiple now() references within one row's evaluation collapse to one
    // call — desirable for consistency within a row, and the sub-microsecond
    // drift between separate calls would be a worse outcome than folding.
    /// <inheritdoc />
    public bool IsPure => true;
}

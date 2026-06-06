using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Uuid;

/// <summary>
/// Returns a new version-7 UUID (time-ordered, monotonic within a millisecond).
/// Non-deterministic: each call returns a distinct value. The optional
/// <c>shift</c> argument offsets the embedded timestamp by the given
/// <see cref="DataKind.Interval"/>, matching the PG-18 signature — useful
/// for backfill, time-travel testing, and any workload that wants UUIDs
/// sorted into a non-current window.
/// </summary>
public sealed class UuidV7Function : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "uuidv7";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Uuid;

    /// <inheritdoc />
    public static string Description =>
        "Returns a new version-7 UUID (time-ordered); accepts an optional " +
        "interval to shift the embedded timestamp.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Uuid)),
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("shift", DataKindMatcher.Exact(DataKind.Interval))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Uuid)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<UuidV7Function>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args.Length == 0)
        {
            return new(ValueRef.FromUuid(Guid.CreateVersion7()));
        }
        if (args[0].IsNull)
        {
            return new(ValueRef.Null(DataKind.Uuid));
        }

        Interval shift = args[0].AsInterval();
        DateTimeOffset shifted = shift.AddTo(DateTimeOffset.UtcNow);
        return new(ValueRef.FromUuid(Guid.CreateVersion7(shifted)));
    }

    /// <inheritdoc />
    public bool IsPure => false;
}

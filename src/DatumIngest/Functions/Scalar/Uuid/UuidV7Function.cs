using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Uuid;

/// <summary>
/// Returns a new version-7 UUID (time-ordered, monotonic within a millisecond).
/// Non-deterministic: each call returns a distinct value.
/// </summary>
public sealed class UuidV7Function : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "uuidv7";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Uuid;

    /// <inheritdoc />
    public static string Description => "Returns a new version-7 UUID (time-ordered).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Uuid)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<UuidV7Function>(argumentKinds);

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame) =>
        ValueRef.FromUuid(Guid.CreateVersion7());

    /// <inheritdoc />
    public bool IsPure => false;
}

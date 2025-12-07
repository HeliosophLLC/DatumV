using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Uuid;

/// <summary>
/// Returns a new version-4 UUID (random).
/// Non-deterministic: each call returns a distinct value.
/// </summary>
public sealed class UuidV4Function : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "uuidv4";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Uuid;

    /// <inheritdoc />
    public static string Description => "Returns a new version-4 UUID (random).";

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
        FunctionMetadata.Validate<UuidV4Function>(argumentKinds);

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame) =>
        ValueRef.FromUuid(Guid.NewGuid());

    /// <inheritdoc />
    public bool IsPure => false;
}

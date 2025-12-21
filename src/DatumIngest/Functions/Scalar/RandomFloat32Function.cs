using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns a uniformly-distributed <see cref="DataKind.Float32"/> in the
/// half-open range <c>[min, max)</c>. Either argument null yields a null
/// result. Use <c>random_float32_from_seed</c> for a deterministic variant.
/// </summary>
/// <remarks>
/// <para>
/// Non-deterministic: <see cref="IsPure"/> is <see langword="false"/>, so
/// common-subexpression elimination will not collapse repeated calls.
/// Inputs accept any kind in <see cref="DataKindFamily.NumericScalar"/> and
/// are converted to <see cref="float"/> via <see cref="ValueRef.TryToDouble"/>.
/// </para>
/// </remarks>
public sealed class RandomFloat32Function : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random_float32";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns a uniformly-distributed Float32 in the half-open range [min, max).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("min", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("max", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomFloat32Function>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        }

        args[0].TryToDouble(out double minD);
        args[1].TryToDouble(out double maxD);

        float min = (float)minD;
        float max = (float)maxD;
        float r = Random.Shared.NextSingle();
        return new ValueTask<ValueRef>(ValueRef.FromFloat32(min + (max - min) * r));
    }

    /// <inheritdoc />
    public bool IsPure => false;
}

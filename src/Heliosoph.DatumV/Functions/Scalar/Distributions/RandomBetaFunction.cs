using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Distributions;

/// <summary>
/// Samples from a Beta distribution: <c>random_beta(alpha, beta[, seed])</c>.
/// Uses the gamma-ratio method (Marsaglia-Tsang for each gamma draw, with
/// shape-boost when either parameter is less than one). Common for priors
/// over probabilities and for mixup augmentation.
/// </summary>
public sealed class RandomBetaFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random_beta";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Samples Beta(alpha, beta). Accepts an optional integer seed for determinism.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("alpha", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("beta", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("alpha", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("beta", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomBetaFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;

        if (!RandomDistributionsCore.TryGetRng(args, seedIndex: 2, out Random rng))
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));

        if (args[0].IsNull || args[1].IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));

        args[0].TryToDouble(out double alpha);
        args[1].TryToDouble(out double beta);

        if (alpha <= 0)
            throw new FunctionArgumentException(Name, $"alpha must be positive, got {alpha}.");
        if (beta <= 0)
            throw new FunctionArgumentException(Name, $"beta must be positive, got {beta}.");

        double x = RandomDistributionsCore.SampleGamma(rng, alpha);
        double y = RandomDistributionsCore.SampleGamma(rng, beta);
        return new ValueTask<ValueRef>(ValueRef.FromFloat32((float)(x / (x + y))));
    }
}

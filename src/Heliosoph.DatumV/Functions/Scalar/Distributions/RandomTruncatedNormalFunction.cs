using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Distributions;

/// <summary>
/// Samples from a truncated normal distribution:
/// <c>random_truncated_normal(mean, stddev, min, max[, seed])</c>.
/// Rejection-samples from N(mean, stddev) until the value falls within
/// <c>[min, max]</c>; falls back to a clamped sample if 1000 rejections are
/// exhausted. Common in ML weight initialisation (cf. TensorFlow's
/// <c>tf.random.truncated_normal</c>).
/// </summary>
public sealed class RandomTruncatedNormalFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random_truncated_normal";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Samples N(mean, stddev) rejection-truncated to [min, max]. Accepts an optional integer seed.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("mean", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("stddev", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("min", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("max", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("mean", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("stddev", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("min", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("max", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomTruncatedNormalFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;

        if (!RandomDistributionsCore.TryGetRng(args, seedIndex: 4, out Random rng))
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));

        for (int i = 0; i < 4; i++)
        {
            if (args[i].IsNull)
                return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        }

        args[0].TryToDouble(out double mean);
        args[1].TryToDouble(out double stddev);
        args[2].TryToDouble(out double min);
        args[3].TryToDouble(out double max);

        if (stddev < 0)
            throw new FunctionArgumentException(Name, $"stddev must be non-negative, got {stddev}.");
        if (min >= max)
            throw new FunctionArgumentException(Name, $"min ({min}) must be < max ({max}).");

        const int maximumAttempts = 1000;
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            double sample = mean + stddev * RandomDistributionsCore.SampleStandardNormal(rng);
            if (sample >= min && sample <= max)
                return new ValueTask<ValueRef>(ValueRef.FromFloat32((float)sample));
        }

        double fallback = mean + stddev * RandomDistributionsCore.SampleStandardNormal(rng);
        return new ValueTask<ValueRef>(
            ValueRef.FromFloat32((float)System.Math.Clamp(fallback, min, max)));
    }
}

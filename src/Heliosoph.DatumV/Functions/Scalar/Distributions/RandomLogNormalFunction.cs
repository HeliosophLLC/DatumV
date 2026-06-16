using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Distributions;

/// <summary>
/// Samples from a log-normal distribution:
/// <c>random_log_normal(mean, stddev[, seed])</c>. Returns <c>exp(N(mean, stddev))</c>.
/// Common for modelling durations, prices, and file sizes.
/// </summary>
public sealed class RandomLogNormalFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random_log_normal";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Samples exp(N(mean, stddev)). Accepts an optional integer seed for determinism.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("mean", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("stddev", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("mean", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("stddev", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomLogNormalFunction>(argumentKinds);

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

        args[0].TryToDouble(out double mean);
        args[1].TryToDouble(out double stddev);

        if (stddev < 0)
            throw new FunctionArgumentException(Name, $"stddev must be non-negative, got {stddev}.");

        double normalSample = mean + stddev * RandomDistributionsCore.SampleStandardNormal(rng);
        return new ValueTask<ValueRef>(ValueRef.FromFloat32((float)System.Math.Exp(normalSample)));
    }
}

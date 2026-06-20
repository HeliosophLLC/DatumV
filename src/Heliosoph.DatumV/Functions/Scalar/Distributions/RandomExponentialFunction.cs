using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Distributions;

/// <summary>
/// Samples from an exponential distribution:
/// <c>random_exponential(rate[, seed])</c>. Returns <c>-ln(U) / rate</c> where
/// <c>U</c> is uniform in <c>(0, 1]</c>. Common for inter-arrival times and
/// decay processes.
/// </summary>
public sealed class RandomExponentialFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random_exponential";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Samples Exp(rate). Accepts an optional integer seed for determinism.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("rate", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("rate", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomExponentialFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;

        if (!RandomDistributionsCore.TryGetRng(args, seedIndex: 1, out Random rng))
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));

        if (args[0].IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));

        args[0].TryToDouble(out double rate);

        if (rate <= 0)
            throw new FunctionArgumentException(Name, $"rate must be positive, got {rate}.");

        // 1 - NextDouble() avoids log(0).
        double sample = -System.Math.Log(1.0 - rng.NextDouble()) / rate;
        return new ValueTask<ValueRef>(ValueRef.FromFloat32((float)sample));
    }
}

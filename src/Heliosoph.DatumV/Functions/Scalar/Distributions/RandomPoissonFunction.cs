using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Distributions;

/// <summary>
/// Samples from a Poisson distribution: <c>random_poisson(lambda[, seed])</c>.
/// Returns an <see cref="DataKind.Int32"/> non-negative count. Uses Knuth's
/// algorithm for <c>λ ≤ 30</c> and the normal approximation for larger λ.
/// </summary>
public sealed class RandomPoissonFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random_poisson";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Samples Poisson(lambda) as an Int32 count. Accepts an optional integer seed.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("lambda", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("lambda", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomPoissonFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;

        if (!RandomDistributionsCore.TryGetRng(args, seedIndex: 1, out Random rng))
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));

        if (args[0].IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));

        args[0].TryToDouble(out double lambda);

        if (lambda < 0)
            throw new FunctionArgumentException(Name, $"lambda must be non-negative, got {lambda}.");
        if (lambda == 0)
            return new ValueTask<ValueRef>(ValueRef.FromInt32(0));

        int count;
        if (lambda <= 30)
        {
            // Knuth's algorithm for small lambda.
            double limit = System.Math.Exp(-lambda);
            count = -1;
            double product = 1.0;
            do
            {
                count++;
                product *= rng.NextDouble();
            } while (product > limit);
        }
        else
        {
            // Normal approximation for large lambda.
            double sample = lambda + System.Math.Sqrt(lambda) * RandomDistributionsCore.SampleStandardNormal(rng);
            count = System.Math.Max(0, (int)System.Math.Round(sample));
        }

        return new ValueTask<ValueRef>(ValueRef.FromInt32(count));
    }
}

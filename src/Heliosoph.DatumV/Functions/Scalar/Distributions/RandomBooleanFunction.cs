using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Distributions;

/// <summary>
/// Bernoulli trial: <c>random_boolean(probability[, seed])</c> returns
/// <see langword="true"/> with probability <c>p</c> and <see langword="false"/>
/// otherwise. The probability must lie in <c>[0, 1]</c>. Common for dropout
/// masks and synthetic-label generation.
/// </summary>
public sealed class RandomBooleanFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random_boolean";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Bernoulli trial — returns true with probability p in [0, 1]. Accepts an optional integer seed.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("probability", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("probability", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
    ];

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomBooleanFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;

        if (!RandomDistributionsCore.TryGetRng(args, seedIndex: 1, out Random rng))
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Boolean));

        if (args[0].IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Boolean));

        args[0].TryToDouble(out double probability);

        if (probability < 0 || probability > 1)
            throw new FunctionArgumentException(Name, $"probability must be in [0, 1], got {probability}.");

        bool result = rng.NextDouble() < probability;
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(result));
    }
}

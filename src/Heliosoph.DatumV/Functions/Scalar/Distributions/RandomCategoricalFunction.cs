using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Distributions;

/// <summary>
/// Draws a zero-based category index from weighted probabilities:
/// <c>random_categorical(weights[, seed])</c>. Weights are a flat
/// <c>Float32[]</c> of non-negative values; they need not sum to one and
/// are normalised internally. Useful for synthetic-label generation and
/// weighted random selection.
/// </summary>
/// <remarks>
/// <see cref="IsPure"/> is <see langword="false"/>: even seeded calls
/// re-evaluate per reference rather than being collapsed by CSE.
/// </remarks>
public sealed class RandomCategoricalFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random_categorical";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Draws a 0-based index from a categorical distribution defined by a Float32[] of "
        + "non-negative weights. Accepts an optional integer seed for determinism.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("weights", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("weights", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomCategoricalFunction>(argumentKinds);

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

        float[] weights = ActivationOps.ReadFloat32Array(args[0]);
        if (weights.Length == 0)
            throw new FunctionArgumentException(Name, "weights must not be empty.");

        double total = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] < 0)
                throw new FunctionArgumentException(Name,
                    $"weights must be non-negative, got {weights[i]} at index {i}.");
            total += weights[i];
        }

        if (total <= 0)
            throw new FunctionArgumentException(Name, "weights must sum to a positive value.");

        double threshold = rng.NextDouble() * total;
        double cumulative = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            cumulative += weights[i];
            if (threshold < cumulative)
                return new ValueTask<ValueRef>(ValueRef.FromInt32(i));
        }

        // Floating-point edge case — return the last category.
        return new ValueTask<ValueRef>(ValueRef.FromInt32(weights.Length - 1));
    }
}

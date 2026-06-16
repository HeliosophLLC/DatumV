using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Distributions;

/// <summary>
/// Generates a flat <c>Float32[]</c> of Gaussian samples N(mean, stddev):
/// <c>random_normal_vector(length, mean, stddev[, seed])</c>. Useful for
/// noise injection into embeddings and feature augmentation.
/// </summary>
public sealed class RandomNormalVectorFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random_normal_vector";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Float32[] of length `length` filled with samples from N(mean, stddev). "
        + "Accepts an optional integer seed for determinism.";

    private static readonly ReturnTypeRule Float32ArrayReturn =
        ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32));

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("length", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("mean", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("stddev", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: Float32ArrayReturn),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("length", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("mean", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("stddev", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: Float32ArrayReturn),
    ];

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomNormalVectorFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;

        if (!RandomDistributionsCore.TryGetRng(args, seedIndex: 3, out Random rng))
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));

        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));

        long lengthLong = RandomDistributionsCore.ReadInteger(args[0]);
        if (lengthLong < 0)
            throw new FunctionArgumentException(Name, $"length must be non-negative, got {lengthLong}.");
        if (lengthLong > int.MaxValue)
            throw new FunctionArgumentException(Name, $"length {lengthLong} exceeds Int32.MaxValue.");

        args[1].TryToDouble(out double mean);
        args[2].TryToDouble(out double stddev);

        if (stddev < 0)
            throw new FunctionArgumentException(Name, $"stddev must be non-negative, got {stddev}.");

        int length = (int)lengthLong;
        float[] result = new float[length];
        for (int i = 0; i < length; i++)
        {
            double sample = mean + stddev * RandomDistributionsCore.SampleStandardNormal(rng);
            result[i] = (float)sample;
        }

        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }
}

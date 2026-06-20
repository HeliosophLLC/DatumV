using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Distributions;

/// <summary>
/// Generates a flat <c>Float32[]</c> of uniform random values in
/// <c>[0, 1)</c>: <c>random_vector(length[, seed])</c>. The two-argument
/// form is deterministic for the given integer seed.
/// </summary>
public sealed class RandomVectorFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random_vector";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Float32[] of length `length`, uniform random in [0, 1). Accepts an optional integer seed.";

    private static readonly ReturnTypeRule Float32ArrayReturn =
        ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32));

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("length", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: Float32ArrayReturn),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("length", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: Float32ArrayReturn),
    ];

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomVectorFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;

        if (!RandomDistributionsCore.TryGetRng(args, seedIndex: 1, out Random rng))
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));

        if (args[0].IsNull)
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));

        long lengthLong = RandomDistributionsCore.ReadInteger(args[0]);
        if (lengthLong < 0)
            throw new FunctionArgumentException(Name, $"length must be non-negative, got {lengthLong}.");
        if (lengthLong > int.MaxValue)
            throw new FunctionArgumentException(Name, $"length {lengthLong} exceeds Int32.MaxValue.");

        int length = (int)lengthLong;
        float[] result = new float[length];
        for (int i = 0; i < length; i++)
            result[i] = (float)rng.NextDouble();

        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }
}

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns a <see cref="DataKind.Float32"/> in the half-open range
/// <c>[min, max)</c>, selected deterministically by the seed. Same seed +
/// same min/max always yields the same result.
/// </summary>
/// <remarks>
/// <para>
/// A fresh <see cref="Random"/> is constructed per call, so the function is
/// pure and eligible for common-subexpression elimination. The seed accepts
/// any kind in <see cref="DataKindFamily.IntegerFamily"/>; values wider
/// than <see cref="int"/> are deterministically truncated to fit
/// <see cref="Random"/>'s constructor. Min/max accept any numeric scalar
/// kind. A null seed, min, or max yields a null result.
/// </para>
/// </remarks>
public sealed class RandomFloat32FromSeedFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random_float32_from_seed";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns a Float32 in the half-open range [min, max), selected deterministically by the seed.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("min", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("max", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomFloat32FromSeedFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef seedArg = args[0];
        ValueRef minArg = args[1];
        ValueRef maxArg = args[2];

        if (seedArg.IsNull || minArg.IsNull || maxArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        }

        int seed = unchecked((int)ReadSeed(seedArg));
        Random rng = new(seed);

        minArg.TryToDouble(out double minD);
        maxArg.TryToDouble(out double maxD);

        float min = (float)minD;
        float max = (float)maxD;
        float r = rng.NextSingle();
        return new ValueTask<ValueRef>(ValueRef.FromFloat32(min + (max - min) * r));
    }

    private static long ReadSeed(ValueRef v) => v.Kind switch
    {
        DataKind.Int8 => v.AsInt8(),
        DataKind.UInt8 => v.AsUInt8(),
        DataKind.Int16 => v.AsInt16(),
        DataKind.UInt16 => v.AsUInt16(),
        DataKind.Int32 => v.AsInt32(),
        DataKind.UInt32 => v.AsUInt32(),
        DataKind.Int64 => v.AsInt64(),
        DataKind.UInt64 => unchecked((long)v.AsUInt64()),
        _ => throw new FunctionArgumentException(
            Name,
            $"unsupported seed kind {v.Kind}."),
    };
}

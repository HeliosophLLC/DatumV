using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar;

/// <summary>
/// Returns a uniformly-distributed random number.
/// <list type="bullet">
///   <item><c>random()</c> — Float64 in <c>[0, 1)</c>.</item>
///   <item><c>random(min, max)</c> — Int64 in <c>[min, max)</c> when both
///   bounds are integer-family kinds; otherwise Float64 in <c>[min, max)</c>.</item>
///   <item><c>random(min, max, seed)</c> — same as above but deterministic for
///   the given integer seed.</item>
/// </list>
/// 128-bit numeric kinds are not supported. Null arguments propagate to a null
/// result. <see cref="IsPure"/> is <see langword="false"/>: even seeded calls
/// re-evaluate per reference rather than being collapsed by CSE.
/// </summary>
public sealed class RandomFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns a random number. random() yields Float64 in [0,1); random(min, max[, seed]) " +
        "yields Int64 when both bounds are integer-family kinds, otherwise Float64.";

    private static readonly ReturnTypeRule BoundedReturnRule = ReturnTypeRule.Custom(
        kinds => BothInteger(kinds[0], kinds[1]) ? DataKind.Int64 : DataKind.Float64,
        "Int64 when both bounds are integer family, else Float64");

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("min", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("max", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: BoundedReturnRule),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("min", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("max", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: BoundedReturnRule),
    ];

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;

        if (args.Length == 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromFloat64(Random.Shared.NextDouble()));
        }

        ValueRef minArg = args[0];
        ValueRef maxArg = args[1];
        Reject128Bit(minArg.Kind);
        Reject128Bit(maxArg.Kind);

        bool integerPath = BothInteger(minArg.Kind, maxArg.Kind);
        DataKind resultKind = integerPath ? DataKind.Int64 : DataKind.Float64;

        Random rng;
        if (args.Length >= 3)
        {
            ValueRef seedArg = args[2];
            Reject128Bit(seedArg.Kind);
            if (seedArg.IsNull)
            {
                return new ValueTask<ValueRef>(ValueRef.Null(resultKind));
            }
            int seed = unchecked((int)ReadInteger(seedArg));
            rng = new Random(seed);
        }
        else
        {
            rng = Random.Shared;
        }

        if (minArg.IsNull || maxArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(resultKind));
        }

        if (integerPath)
        {
            long min = ReadInteger(minArg);
            long max = ReadInteger(maxArg);
            return new ValueTask<ValueRef>(ValueRef.FromInt64(rng.NextInt64(min, max)));
        }

        minArg.TryToDouble(out double minD);
        maxArg.TryToDouble(out double maxD);
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(minD + (maxD - minD) * rng.NextDouble()));
    }

    private static bool BothInteger(DataKind a, DataKind b) =>
        IsIntegerFamily(a) && IsIntegerFamily(b);

    private static bool IsIntegerFamily(DataKind k) => k switch
    {
        DataKind.Int8 or DataKind.UInt8 or
        DataKind.Int16 or DataKind.UInt16 or
        DataKind.Int32 or DataKind.UInt32 or
        DataKind.Int64 or DataKind.UInt64 => true,
        _ => false,
    };

    private static void Reject128Bit(DataKind kind)
    {
        if (kind == DataKind.Int128 || kind == DataKind.UInt128)
        {
            throw new FunctionArgumentException(Name, $"128-bit numeric kind {kind} is not supported.");
        }
    }

    private static long ReadInteger(ValueRef v) => v.Kind switch
    {
        DataKind.Int8 => v.AsInt8(),
        DataKind.UInt8 => v.AsUInt8(),
        DataKind.Int16 => v.AsInt16(),
        DataKind.UInt16 => v.AsUInt16(),
        DataKind.Int32 => v.AsInt32(),
        DataKind.UInt32 => v.AsUInt32(),
        DataKind.Int64 => v.AsInt64(),
        DataKind.UInt64 => unchecked((long)v.AsUInt64()),
        _ => throw new FunctionArgumentException(Name, $"unsupported integer kind {v.Kind}."),
    };
}

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns one of the supplied strings, selected deterministically by the
/// seed. Same seed + same argument list always yields the same result.
/// Requires a seed and two or more strings.
/// </summary>
/// <remarks>
/// <para>
/// A fresh <see cref="Random"/> is constructed per call from the seed, so
/// repeated calls with identical arguments produce identical results — the
/// function is pure and eligible for common-subexpression elimination. A
/// null seed yields a null result; null variadic arguments are eligible
/// for selection.
/// </para>
/// <para>
/// The seed accepts any kind in <see cref="DataKindFamily.IntegerFamily"/>;
/// values wider than <see cref="int"/> are deterministically truncated to
/// fit <see cref="Random"/>'s constructor. The mapping is stable across
/// calls so determinism still holds.
/// </para>
/// </remarks>
public sealed class RandomStringFromSeedFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random_string_from_seed";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns one of the supplied strings selected deterministically by the seed. "
        + "Requires an integer seed followed by two or more string arguments.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: new VariadicSpec(
                "values",
                DataKindMatcher.Exact(DataKind.String),
                MinOccurrences: 2),
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomStringFromSeedFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        ValueRef seedArg = arguments[0];
        if (seedArg.IsNull)
        {
            return ValueRef.Null(DataKind.String);
        }

        int seed = unchecked((int)ReadSeed(seedArg));
        Random rng = new(seed);

        // arguments[0] is the seed; the variadic strings start at index 1.
        int valueCount = arguments.Length - 1;
        int pick = rng.Next(valueCount);
        return arguments[1 + pick];
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

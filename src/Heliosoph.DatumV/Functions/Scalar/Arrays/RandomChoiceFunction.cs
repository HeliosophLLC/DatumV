using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// Returns one element of an array selected uniformly at random. The element
/// kind matches the array's element kind. An optional integer seed makes the
/// selection deterministic.
/// </summary>
/// <remarks>
/// <para>
/// <c>random_choice(array)</c> uses <see cref="Random.Shared"/>;
/// <c>random_choice(array, seed)</c> constructs a fresh <see cref="Random"/>
/// from the seed so identical seeds reproduce identical picks. A null array,
/// null seed, or empty array yields a null result of the array's element kind.
/// Null elements remain eligible for selection.
/// </para>
/// <para>
/// <see cref="IsPure"/> is <see langword="false"/>: even seeded calls
/// re-evaluate per reference rather than being collapsed by CSE.
/// </para>
/// </remarks>
public sealed class RandomChoiceFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random_choice";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns one element of an array selected uniformly at random. " +
        "Accepts an optional integer seed for deterministic selection.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Any),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Any),
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomChoiceFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef arrayArg = args[0];

        if (!arrayArg.IsArray)
            throw new FunctionArgumentException(Name, "first argument must be an array.");

        if (arrayArg.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(arrayArg.Kind));

        Random rng;
        if (args.Length >= 2)
        {
            ValueRef seedArg = args[1];
            if (seedArg.IsNull)
                return new ValueTask<ValueRef>(ValueRef.Null(arrayArg.Kind));
            int seed = unchecked((int)ReadInteger(seedArg));
            rng = new Random(seed);
        }
        else
        {
            rng = Random.Shared;
        }

        ReadOnlySpan<ValueRef> elements = arrayArg.GetArrayElements();
        if (elements.Length == 0)
            return new ValueTask<ValueRef>(ValueRef.Null(arrayArg.Kind));

        int index = rng.Next(elements.Length);
        return new ValueTask<ValueRef>(elements[index]);
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

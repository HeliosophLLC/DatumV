using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Math;

/// <summary>
/// Returns the smallest non-null argument. Nulls are skipped; if every
/// argument is null the result is null. Matches PostgreSQL's <c>LEAST()</c>.
/// </summary>
/// <remarks>
/// Mirrors <see cref="CoalesceFunction"/>'s two-variant shape: a mixed-numeric
/// variant that promotes to the widest result kind, plus a same-kind variant
/// for the remaining comparable scalars (decimal, the wider integer kinds,
/// string, date/time/duration, uuid, boolean).
/// </remarks>
public sealed class LeastFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "least";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the smallest of the given arguments. Null arguments are skipped; " +
        "the result is null only if every argument is null.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: new VariadicSpec(
                "values",
                DataKindMatcher.Family(DataKindFamily.NumericScalar),
                MinOccurrences: 2),
            ReturnType: ReturnTypeRule.Custom(
                static argKinds => MinMaxComparison.PromoteAll(argKinds),
                "widest numeric kind among arguments")),

        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: new VariadicSpec(
                "values",
                DataKindMatcher.OneOf(
                    DataKind.Decimal, DataKind.Float16, DataKind.Int128, DataKind.UInt128,
                    DataKind.String, DataKind.Date, DataKind.DateTime, DataKind.Time,
                    DataKind.Duration, DataKind.Uuid, DataKind.Boolean),
                MinOccurrences: 2,
                RequireSameKindAcrossArgs: true),
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<LeastFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(MinMaxComparison.Execute(arguments.Span, pickSmaller: true));
}

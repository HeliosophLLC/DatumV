using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the first non-null argument, or null if every argument is null.
/// </summary>
/// <remarks>
/// Two accepted shapes:
/// <list type="bullet">
///   <item>All-numeric (potentially mixed kinds): the result kind is the
///         widest numeric kind among the arguments. Lets
///         <c>coalesce(max(x), 12)</c> compose without forcing the literal
///         to the aggregate's kind, which is the common failure mode under
///         literal narrowing.</item>
///   <item>Any other kinds: every argument must share the same kind
///         (strings with strings, dates with dates, etc.).</item>
/// </list>
/// </remarks>
public sealed class CoalesceFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "coalesce";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Utility;

    /// <inheritdoc />
    public static string Description =>
        "Returns the first non-null argument, or null if every argument is null. " +
        "Numeric arguments may be of mixed kinds; the result is the widest kind.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        // Numeric variant: mixed Int/Float kinds permitted; the return kind
        // is the widest via arithmetic-add promotion folded across args.
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: new VariadicSpec(
                "values",
                DataKindMatcher.Family(DataKindFamily.NumericScalar),
                MinOccurrences: 2),
            ReturnType: ReturnTypeRule.Custom(
                static argKinds => PromoteAll(argKinds),
                "widest numeric kind among arguments")),

        // Catch-all variant: every other family must share a single kind
        // across all arguments.
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: new VariadicSpec(
                "values",
                DataKindMatcher.Any,
                MinOccurrences: 2,
                RequireSameKindAcrossArgs: true),
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<CoalesceFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        DataKind targetKind = ResolveTargetKind(args);

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) continue;
            ValueRef chosen = args[i];
            if (chosen.Kind == targetKind) return new ValueTask<ValueRef>(chosen);
            // Numeric-mixed path: coerce the chosen inline value to the
            // promoted kind so the runtime kind matches the planner-resolved
            // schema kind. CoerceToKind is a no-op for non-numeric values.
            DataValue coerced = chosen.InlineDataValue.CoerceToKind(targetKind);
            return new ValueTask<ValueRef>(ValueRef.FromInline(coerced));
        }
        return new ValueTask<ValueRef>(ValueRef.Null(targetKind));
    }

    private static DataKind ResolveTargetKind(ReadOnlySpan<ValueRef> args)
    {
        DataKind first = args[0].Kind;
        if (!DataKindFamily.NumericScalar.Contains(first))
        {
            return first;
        }
        Span<DataKind> kinds = stackalloc DataKind[args.Length];
        for (int i = 0; i < args.Length; i++) kinds[i] = args[i].Kind;
        return PromoteAll(kinds);
    }

    private static DataKind PromoteAll(ReadOnlySpan<DataKind> kinds)
    {
        DataKind result = kinds[0];
        for (int i = 1; i < kinds.Length; i++)
        {
            result = ExpressionEvaluator.TryPromoteArithmeticKind(
                result, kinds[i], BinaryOperator.Add) ?? result;
        }
        return result;
    }
}

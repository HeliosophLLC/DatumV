using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Math;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Functions.Scalar;

/// <summary>
/// Returns null when the two arguments compare equal, otherwise returns the
/// first argument. PostgreSQL-conformant; the inverse of <c>coalesce</c>.
/// </summary>
/// <remarks>
/// Two accepted shapes mirror <see cref="Math.GreatestFunction"/> and
/// <see cref="CoalesceFunction"/>:
/// <list type="bullet">
///   <item>Mixed-numeric: both arguments are promoted to the widest numeric
///         kind before comparison. Lets <c>nullif(int_col, 0)</c> against a
///         Float column resolve cleanly.</item>
///   <item>Same-kind catch-all over the comparable non-numeric kinds.</item>
/// </list>
/// If the first argument is null the result is null (PG semantics).
/// </remarks>
public sealed class NullifFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "nullif";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Utility;

    /// <inheritdoc />
    public static string Description =>
        "Returns null when the two arguments are equal, otherwise returns the first argument.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        // Two-numeric variant: mixed Int/Float kinds permitted, promote to widest.
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: new VariadicSpec(
                "values",
                DataKindMatcher.Family(DataKindFamily.NumericScalar),
                MinOccurrences: 2),
            ReturnType: ReturnTypeRule.Custom(
                static argKinds => PromoteAll(argKinds),
                "widest numeric kind among arguments")),

        // Same-kind catch-all over the comparable non-numeric kinds. Restricted
        // to what MinMaxComparison.Compare supports — the equality predicate
        // shares that switch.
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: new VariadicSpec(
                "values",
                DataKindMatcher.OneOf(
                    DataKind.Decimal, DataKind.Float16, DataKind.Int128, DataKind.UInt128,
                    DataKind.String, DataKind.Date, DataKind.Timestamp, DataKind.TimestampTz,
                    DataKind.Time, DataKind.Duration, DataKind.Uuid, DataKind.Boolean),
                MinOccurrences: 2,
                RequireSameKindAcrossArgs: true),
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new FunctionArgumentException(
                Name,
                $"expects exactly 2 arguments but got {argumentKinds.Length}.");
        }
        return FunctionMetadata.Validate<NullifFunction>(argumentKinds);
    }

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef left = args[0];
        ValueRef right = args[1];

        DataKind targetKind = ResolveTargetKind(left.Kind, right.Kind);

        // PG semantics: nullif(NULL, *) → NULL of the result kind. A null
        // second argument leaves comparison undefined — PG returns the first
        // argument unchanged.
        if (left.IsNull) return new ValueTask<ValueRef>(ValueRef.Null(targetKind));
        if (right.IsNull) return new ValueTask<ValueRef>(CoerceToTarget(left, targetKind));

        if (MinMaxComparison.Compare(left, right, targetKind) == 0)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(targetKind));
        }
        return new ValueTask<ValueRef>(CoerceToTarget(left, targetKind));
    }

    private static ValueRef CoerceToTarget(ValueRef value, DataKind targetKind)
    {
        if (value.Kind == targetKind) return value;
        // Only reached on the mixed-numeric path; CoerceToKind is a no-op
        // outside numeric kinds and the inline DataValue carries the
        // numeric payload directly.
        DataValue coerced = value.InlineDataValue.CoerceToKind(targetKind);
        return ValueRef.FromInline(coerced);
    }

    private static DataKind ResolveTargetKind(DataKind a, DataKind b)
    {
        if (a == b) return a;
        if (!DataKindFamily.NumericScalar.Contains(a)) return a;
        return ExpressionEvaluator.TryPromoteArithmeticKind(a, b, BinaryOperator.Add) ?? a;
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

using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions.Scalar.Math;

/// <summary>
/// Shared logic for <see cref="LeastFunction"/> and <see cref="GreatestFunction"/>:
/// null-skipping reduction over the argument list, numeric widening to the
/// promoted result kind, and a comparison switch covering every kind the two
/// functions accept.
/// </summary>
internal static class MinMaxComparison
{
    internal static ValueRef Execute(ReadOnlySpan<ValueRef> args, bool pickSmaller)
    {
        DataKind targetKind = ResolveTargetKind(args);

        int bestIndex = -1;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) continue;
            if (bestIndex < 0)
            {
                bestIndex = i;
                continue;
            }
            int cmp = Compare(args[i], args[bestIndex], targetKind);
            if (pickSmaller ? cmp < 0 : cmp > 0)
            {
                bestIndex = i;
            }
        }

        if (bestIndex < 0) return ValueRef.Null(targetKind);

        ValueRef chosen = args[bestIndex];
        if (chosen.Kind == targetKind) return chosen;

        // Mixed-numeric path: coerce the chosen inline value to the promoted
        // kind so the runtime kind matches the planner-resolved schema kind.
        // CoerceToKind is a no-op outside numeric kinds.
        DataValue coerced = chosen.InlineDataValue.CoerceToKind(targetKind);
        return ValueRef.FromInline(coerced);
    }

    internal static DataKind PromoteAll(ReadOnlySpan<DataKind> kinds)
    {
        DataKind result = kinds[0];
        for (int i = 1; i < kinds.Length; i++)
        {
            result = ExpressionEvaluator.TryPromoteArithmeticKind(
                result, kinds[i], BinaryOperator.Add) ?? result;
        }
        return result;
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

    private static int Compare(ValueRef left, ValueRef right, DataKind targetKind)
    {
        // The mixed-numeric variant accepts kinds in DataKindFamily.NumericScalar
        // (signed + unsigned ints, Float32, Float64). For those a double widening
        // is exact (or — for UInt64 — as exact as the engine's other cross-kind
        // numeric paths) and is the same compromise DataValueComparer uses for
        // cross-kind comparison. Every other branch is same-kind by construction
        // (RequireSameKindAcrossArgs on the catch-all variant).
        if (DataKindFamily.NumericScalar.Contains(targetKind))
        {
            left.TryToDouble(out double leftDouble);
            right.TryToDouble(out double rightDouble);
            return leftDouble.CompareTo(rightDouble);
        }

        return targetKind switch
        {
            DataKind.Decimal  => left.AsDecimal().CompareTo(right.AsDecimal()),
            DataKind.Float16  => left.AsFloat16().CompareTo(right.AsFloat16()),
            DataKind.Int128   => left.AsInt128().CompareTo(right.AsInt128()),
            DataKind.UInt128  => left.AsUInt128().CompareTo(right.AsUInt128()),
            DataKind.String   => string.CompareOrdinal(left.AsString(), right.AsString()),
            DataKind.Date     => left.AsDate().CompareTo(right.AsDate()),
            DataKind.DateTime => left.AsDateTime().CompareTo(right.AsDateTime()),
            DataKind.Time     => left.AsTime().CompareTo(right.AsTime()),
            DataKind.Duration => left.AsDuration().CompareTo(right.AsDuration()),
            DataKind.Uuid     => left.AsUuid().CompareTo(right.AsUuid()),
            DataKind.Boolean  => left.AsBoolean().CompareTo(right.AsBoolean()),
            _ => throw new FunctionArgumentException(
                "least/greatest",
                $"does not support kind {targetKind}."),
        };
    }
}

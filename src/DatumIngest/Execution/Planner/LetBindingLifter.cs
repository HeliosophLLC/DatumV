using DatumIngest.Execution.Operators;
using DatumIngest.Models;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Helpers for the "lift LET bindings below WHERE" planner pass that surfaces
/// LET-bound names as physical rungs so a residual WHERE predicate referencing
/// them can evaluate against the row.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BuildSingleMioForLiftedLet"/> turns a LET binding whose body is
/// exactly a <c>models.*</c> call into a <see cref="ModelInvocationOperator"/>
/// rung. Mirrors the catalog/arity validation in <c>ModelInvocationHoister</c>
/// so the same plan-time error messages surface from both paths.
/// </para>
/// <para>
/// <see cref="ReplaceLetNameRefs"/> rewrites an expression tree so unqualified
/// column references to lifted LET names point at the synthetic column the
/// physical rung emits. Walks the full composite shape (binary / unary / cast /
/// IN / BETWEEN / IS NULL / LIKE / function call / CASE) so any reference,
/// however deeply nested, gets remapped.
/// </para>
/// </remarks>
internal static class LetBindingLifter
{
    /// <summary>
    /// Builds a <see cref="ModelInvocationOperator"/> for a lifted LET binding
    /// whose body is exactly a <c>models.*</c> call. Splits the call's arguments
    /// into required + optional buckets per the entry's
    /// <see cref="ModelCatalogEntry.InputKinds"/> /
    /// <see cref="ModelCatalogEntry.OptionalArgKinds"/> and throws with the
    /// canonical "model not registered" / "arity mismatch" diagnostics when the
    /// catalog disagrees.
    /// </summary>
    public static QueryOperator BuildSingleMioForLiftedLet(
        QueryOperator source,
        FunctionCallExpression call,
        string outputColumn,
        ModelCatalog catalog)
    {
        // Post-S7b: parser splits schema and function name. The call's FunctionName
        // is already the bare model name when SchemaName == "models".
        string modelName = call.FunctionName;

        ModelCatalogEntry? entry = catalog.TryGetEntry(modelName)
            ?? throw new InvalidOperationException(
                $"Model '{modelName}' is not registered in the catalog. Reference '{call.CallName}' " +
                $"requires a matching ModelCatalog entry — register it via ModelCatalog.Register before planning.");

        int requiredCount = entry.InputKinds.Count;
        int maxOptional = entry.OptionalArgKinds?.Count ?? 0;
        int suppliedCount = call.Arguments.Count;

        if (suppliedCount < requiredCount || suppliedCount > requiredCount + maxOptional)
        {
            throw new InvalidOperationException(
                $"Model '{modelName}' arity mismatch: expected {requiredCount}–{requiredCount + maxOptional} " +
                $"arguments, got {suppliedCount}.");
        }

        Expression[] requiredArgs = new Expression[requiredCount];
        for (int i = 0; i < requiredCount; i++)
        {
            requiredArgs[i] = call.Arguments[i];
        }
        Expression[] optionalArgs = new Expression[suppliedCount - requiredCount];
        for (int i = 0; i < optionalArgs.Length; i++)
        {
            optionalArgs[i] = call.Arguments[requiredCount + i];
        }

        return new ModelInvocationOperator(source, modelName, requiredArgs, optionalArgs, outputColumn);
    }

    /// <summary>
    /// Recursively rewrites <paramref name="expression"/> by substituting any
    /// unqualified <see cref="ColumnReference"/> whose name is a key in
    /// <paramref name="nameMap"/> with a column reference to the mapped
    /// synthetic name. Qualified refs (<c>t.x</c>) and refs to names not in the
    /// map pass through unchanged. Re-allocates AST nodes only when an
    /// underlying child changed, so unaffected sub-trees keep their identity.
    /// </summary>
    public static Expression ReplaceLetNameRefs(
        Expression expression, IReadOnlyDictionary<string, string> nameMap)
    {
        switch (expression)
        {
            case ColumnReference col when col.TableName is null
                && nameMap.TryGetValue(col.ColumnName, out string? synth):
                return new ColumnReference(TableName: null, ColumnName: synth);

            case FunctionCallExpression fn:
                Expression[] rewrittenArgs = new Expression[fn.Arguments.Count];
                bool argsChanged = false;
                for (int i = 0; i < fn.Arguments.Count; i++)
                {
                    rewrittenArgs[i] = ReplaceLetNameRefs(fn.Arguments[i], nameMap);
                    if (!ReferenceEquals(rewrittenArgs[i], fn.Arguments[i])) argsChanged = true;
                }
                return argsChanged ? fn with { Arguments = rewrittenArgs } : fn;

            case BinaryExpression b:
                Expression left = ReplaceLetNameRefs(b.Left, nameMap);
                Expression right = ReplaceLetNameRefs(b.Right, nameMap);
                return ReferenceEquals(left, b.Left) && ReferenceEquals(right, b.Right)
                    ? b : b with { Left = left, Right = right };

            case UnaryExpression u:
                Expression operand = ReplaceLetNameRefs(u.Operand, nameMap);
                return ReferenceEquals(operand, u.Operand) ? u : u with { Operand = operand };

            case CastExpression c:
                Expression cExpr = ReplaceLetNameRefs(c.Expression, nameMap);
                return ReferenceEquals(cExpr, c.Expression) ? c : c with { Expression = cExpr };

            case IsNullExpression isNull:
                Expression isNullExpr = ReplaceLetNameRefs(isNull.Expression, nameMap);
                return ReferenceEquals(isNullExpr, isNull.Expression)
                    ? isNull : isNull with { Expression = isNullExpr };

            case BetweenExpression bt:
                Expression btE = ReplaceLetNameRefs(bt.Expression, nameMap);
                Expression btL = ReplaceLetNameRefs(bt.Low, nameMap);
                Expression btH = ReplaceLetNameRefs(bt.High, nameMap);
                return ReferenceEquals(btE, bt.Expression) && ReferenceEquals(btL, bt.Low) && ReferenceEquals(btH, bt.High)
                    ? bt : bt with { Expression = btE, Low = btL, High = btH };

            case InExpression i:
                Expression iE = ReplaceLetNameRefs(i.Expression, nameMap);
                Expression[] iVals = new Expression[i.Values.Count];
                bool iValsChanged = false;
                for (int j = 0; j < i.Values.Count; j++)
                {
                    iVals[j] = ReplaceLetNameRefs(i.Values[j], nameMap);
                    if (!ReferenceEquals(iVals[j], i.Values[j])) iValsChanged = true;
                }
                return ReferenceEquals(iE, i.Expression) && !iValsChanged
                    ? i : i with { Expression = iE, Values = iVals };

            case LikeExpression like:
                Expression lE = ReplaceLetNameRefs(like.Expression, nameMap);
                Expression lP = ReplaceLetNameRefs(like.Pattern, nameMap);
                Expression lEsc = ReplaceLetNameRefs(like.EscapeCharacter, nameMap);
                return ReferenceEquals(lE, like.Expression) && ReferenceEquals(lP, like.Pattern) && ReferenceEquals(lEsc, like.EscapeCharacter)
                    ? like : like with { Expression = lE, Pattern = lP, EscapeCharacter = lEsc };

            case CaseExpression ce:
                Expression? ceOp = ce.Operand is null ? null : ReplaceLetNameRefs(ce.Operand, nameMap);
                List<WhenClause> ceWhens = ce.WhenClauses
                    .Select(w => new WhenClause(
                        ReplaceLetNameRefs(w.Condition, nameMap),
                        ReplaceLetNameRefs(w.Result, nameMap)))
                    .ToList();
                Expression? ceElse = ce.ElseResult is null ? null : ReplaceLetNameRefs(ce.ElseResult, nameMap);
                return ce with { Operand = ceOp, WhenClauses = ceWhens, ElseResult = ceElse };

            default:
                return expression;
        }
    }
}

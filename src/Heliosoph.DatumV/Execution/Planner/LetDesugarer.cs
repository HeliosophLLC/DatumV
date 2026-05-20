using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Planner;

/// <summary>
/// Plan-time desugaring of LET-binding-related syntax sugar plus the LET-aware
/// aggregate/window detection used by <see cref="QueryPlanner"/> to gate the
/// aggregate / window pipeline rungs.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="DesugarCrossValidate"/> rewrites a <see cref="CrossValidateClause"/>
/// into a synthetic <see cref="LetBinding"/> that computes the fold index from a
/// <c>hash_split</c> over the validate key.</item>
/// <item><see cref="DesugarDestructuredLetBindings"/> expands <c>LET (a, b) = expr</c>
/// and <c>LET {x, y} = expr</c> into one memoizing hidden binding plus one plain
/// binding per extracted name, so downstream passes only see the plain form.</item>
/// <item><see cref="HasLetAggregateFunction"/> / <see cref="HasLetWindowFunction"/>
/// gate the LET-bindings-can-contain-aggregates / -window-functions rungs in
/// PlanCore, so a binding like <c>LET avg_price = AVG(price)</c> routes through
/// the same pipeline as <c>SELECT AVG(price)</c>.</item>
/// </list>
/// </remarks>
internal static class LetDesugarer
{
    /// <summary>
    /// Desugars a <see cref="CrossValidateClause"/> into a synthetic
    /// <see cref="LetBinding"/> that computes the fold index:
    /// <c>CAST(FLOOR(hash_split(key, seed) * k) AS Int32)</c>. For composite keys
    /// the key is <c>concat_ws('|', CAST(k1 AS String), ...)</c>. When GROUP BY
    /// columns are provided they replace the ON-clause key.
    /// </summary>
    public static LetBinding DesugarCrossValidate(CrossValidateClause cv)
    {
        double k = LiteralFolding.EvaluateConstantDouble(cv.FoldCount);
        if (k < 2 || k != Math.Floor(k))
        {
            throw new InvalidOperationException(
                $"CROSS VALIDATE k must be an integer >= 2, got {k}.");
        }

        double seed = cv.Seed is not null ? LiteralFolding.EvaluateConstantDouble(cv.Seed) : 0;

        // Determine the hash key expression — GROUP BY key overrides ON key.
        IReadOnlyList<Expression> keyColumns = cv.GroupColumns ?? cv.KeyColumns;

        // Build the hash key expression: single column or composite via concat_ws.
        Expression hashKeyExpr;
        if (keyColumns.Count == 1)
        {
            hashKeyExpr = keyColumns[0];
        }
        else
        {
            // concat_ws('|', CAST(k1 AS String), CAST(k2 AS String), ...)
            List<Expression> concatArgs = [new LiteralExpression("|")];
            foreach (Expression col in keyColumns)
            {
                concatArgs.Add(new CastExpression(col, "String"));
            }

            hashKeyExpr = new FunctionCallExpression("concat_ws", concatArgs);
        }

        // hash_split(key, seed)
        Expression hashSplitCall = new FunctionCallExpression("hash_split",
            [hashKeyExpr, new LiteralExpression(seed)]);

        // hash_split(...) * k
        Expression multiply = new BinaryExpression(
            hashSplitCall, BinaryOperator.Multiply, new LiteralExpression(k));

        // FLOOR(hash_split(...) * k)
        Expression floor = new FunctionCallExpression("floor", [multiply]);

        // CAST(FLOOR(...) AS Int32)
        Expression cast = new CastExpression(floor, "Int32");

        return new LetBinding(cv.OutputAlias, cast, OutputAlias: cv.OutputAlias);
    }

    /// <summary>
    /// Expands destructured LET bindings (<c>LET (a, b) = expr</c>,
    /// <c>LET {x, y} = expr</c>) into plain nodes before any rewriting passes run.
    /// Each destructured binding becomes one hidden memoizing binding (named
    /// <c>__destructure_N</c>) plus one plain binding per extracted name. Plain
    /// bindings are passed through unchanged.
    /// </summary>
    public static IReadOnlyList<LetBinding>? DesugarDestructuredLetBindings(
        IReadOnlyList<LetBinding>? letBindings)
    {
        if (letBindings is null)
        {
            return null;
        }

        // Fast path: skip allocation when no destructuring is present.
        bool hasDestructure = false;
        foreach (LetBinding binding in letBindings)
        {
            if (binding.Destructure is not null)
            {
                hasDestructure = true;
                break;
            }
        }

        if (!hasDestructure)
        {
            return letBindings;
        }

        List<LetBinding> expanded = new(letBindings.Count + 4);
        int counter = 0;

        foreach (LetBinding binding in letBindings)
        {
            if (binding.Destructure is null)
            {
                expanded.Add(binding);
                continue;
            }

            DestructurePattern pattern = binding.Destructure;
            string hiddenName = $"__destructure_{counter++}";

            // Hidden binding memoizes the RHS expression once per row.
            expanded.Add(new LetBinding(hiddenName, binding.Expression, OutputAlias: null, Span: binding.Span));

            Expression hiddenRef = new ColumnReference(null, hiddenName);

            if (pattern.Mode == DestructureMode.Positional)
            {
                for (int i = 0; i < pattern.Names.Count; i++)
                {
                    // IndexAccessExpression uses 1-based indices to match PG;
                    // shift from the 0-based loop counter at desugar time.
                    Expression index = new LiteralExpression((float)(i + 1));
                    expanded.Add(new LetBinding(
                        pattern.Names[i],
                        new IndexAccessExpression(hiddenRef, new[] { index }),
                        OutputAlias: null,
                        Span: binding.Span));
                }
            }
            else
            {
                // Named: extract each field by its string key.
                foreach (string fieldName in pattern.Names)
                {
                    Expression index = new LiteralExpression(fieldName);
                    expanded.Add(new LetBinding(
                        fieldName,
                        new IndexAccessExpression(hiddenRef, new[] { index }),
                        OutputAlias: null,
                        Span: binding.Span));
                }
            }
        }

        return expanded;
    }

    /// <summary>
    /// Returns <see langword="true"/> if any LET binding expression contains an
    /// aggregate function call, requiring the GROUP BY rewriting path.
    /// </summary>
    public static bool HasLetAggregateFunction(
        IReadOnlyList<LetBinding>? letBindings, FunctionRegistry functionRegistry)
    {
        if (letBindings is null)
        {
            return false;
        }

        foreach (LetBinding binding in letBindings)
        {
            if (PredicateUtilities.ExpressionContainsAggregate(binding.Expression, functionRegistry))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if any LET binding expression contains a
    /// window function call, requiring the window function rewriting path.
    /// </summary>
    public static bool HasLetWindowFunction(IReadOnlyList<LetBinding>? letBindings)
    {
        if (letBindings is null)
        {
            return false;
        }

        foreach (LetBinding binding in letBindings)
        {
            if (WindowRewriter.ExpressionContainsWindowFunction(binding.Expression))
            {
                return true;
            }
        }

        return false;
    }
}

using DatumIngest.Functions;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Plan-time AST rewrite that replaces <see cref="FunctionCallExpression"/>
/// calls to <see cref="IInlineMetadataAccessor"/>-marked scalar functions
/// (<c>image_width</c>, <c>video_height</c>, <c>point_cloud_count</c>, …)
/// with <see cref="InlineAccessorExpression"/> nodes the evaluator handles
/// via direct <see cref="DatumIngest.Model.DataValue"/> payload reads.
/// </summary>
/// <remarks>
/// <para>
/// Eliminates <see cref="IScalarFunction.ExecuteAsync"/> dispatch
/// (<see cref="ValueTask{T}"/> allocation, argument <see cref="System.Buffers.ArrayPool{T}"/>
/// rent, per-call activity span, registry lookup) on the common (stamped)
/// path. The evaluator still delegates to the original function when the
/// inline metadata reads as the zero sentinel — preserving the slow-path
/// decode fallback bit-for-bit.
/// </para>
/// <para>
/// <strong>Pipeline placement.</strong> Runs <em>after</em>
/// <see cref="UdfInliner"/> (so UDF bodies that wrap an accessor get their
/// inlined call sites elided too) and <em>before</em>
/// <see cref="CommonSubexpressionEliminator"/> (so repeated accessor calls
/// in <c>WHERE</c> + <c>SELECT</c> + <c>ORDER BY</c> collapse on the new
/// node's record equality).
/// </para>
/// <para>
/// <strong>Eligibility gate.</strong> A call is elided only when the
/// resolved scalar function implements
/// <see cref="IInlineMetadataAccessor"/>. The check goes through the live
/// <see cref="FunctionRegistry"/> with the catalog search path, so a
/// procedural UDF or sql-defined model that shadows a built-in accessor's
/// bare name does not get elided (the resolved instance won't implement the
/// marker).
/// </para>
/// </remarks>
public static class InlineAccessorElider
{
    /// <summary>
    /// Walks the operator tree and rewrites every eligible accessor call
    /// in every contained expression. Calls without a matching registry
    /// entry, calls with non-default function syntax (DISTINCT, ORDER BY,
    /// WITHIN GROUP), and calls to non-accessor functions pass through
    /// unchanged.
    /// </summary>
    /// <param name="op">Operator tree to rewrite.</param>
    /// <param name="functions">Live function registry for resolution.</param>
    /// <param name="searchPath">
    /// Schema search path used to resolve unqualified function names —
    /// mirrors the path threaded into <see cref="UdfInliner"/> so
    /// resolution decisions stay consistent across passes.
    /// </param>
    public static QueryOperator Elide(
        QueryOperator op, FunctionRegistry functions, IReadOnlyList<string> searchPath)
    {
        // Per-operator RewriteExpressions already recurses into the child
        // operator (every operator that holds expressions also forwards
        // through its source). One call at the root rewrites the whole tree.
        return op.RewriteExpressions(expr => RewriteExpression(expr, functions, searchPath));
    }

    /// <summary>
    /// Children-first recursive rewrite over an expression tree.
    /// Matches function-call eligibility at the current node only after
    /// the arguments have been rewritten — preserves the invariant that
    /// the <see cref="InlineAccessorExpression"/>'s argument carries the
    /// already-elided sub-tree (relevant if accessors ever take an
    /// accessor sub-call as an argument, though none do today).
    /// </summary>
    private static Expression RewriteExpression(
        Expression expression, FunctionRegistry functions, IReadOnlyList<string> searchPath)
    {
        Expression rewritten = RewriteChildren(expression, functions, searchPath);

        if (rewritten is FunctionCallExpression call &&
            IsElidableShape(call) &&
            functions.TryGetScalar(call.SchemaName, call.FunctionName, searchPath)
                is IInlineMetadataAccessor accessor)
        {
            return new InlineAccessorExpression(call.Arguments[0], accessor.Field);
        }

        return rewritten;
    }

    /// <summary>
    /// Gates which call shapes the elider considers — guards against
    /// nonsense like <c>image_width(DISTINCT x)</c> or hypothetical future
    /// signatures that take additional arguments. All nine currently-marked
    /// accessors are unary with no modifier clauses.
    /// </summary>
    private static bool IsElidableShape(FunctionCallExpression call)
        => call.Arguments.Count == 1
        && call.OrderBy is null or { Count: 0 }
        && call.WithinGroupOrderBy is null or { Count: 0 }
        && !call.Distinct;

    /// <summary>
    /// Returns a copy of <paramref name="expression"/> with children
    /// recursively rewritten. Mirrors <see cref="UdfInliner"/>'s
    /// children-rewriter so the two passes walk the same set of nodes —
    /// any subtree that <see cref="UdfInliner"/> inlines into is also a
    /// subtree this pass can elide within.
    /// </summary>
    private static Expression RewriteChildren(
        Expression expression, FunctionRegistry functions, IReadOnlyList<string> searchPath)
    {
        Expression Rec(Expression e) => RewriteExpression(e, functions, searchPath);

        return expression switch
        {
            FunctionCallExpression f => f with
            {
                Arguments = f.Arguments.Select(Rec).ToList(),
                OrderBy = f.OrderBy?.Select(i => new OrderByItem(Rec(i.Expression), i.Direction)).ToList(),
                WithinGroupOrderBy = f.WithinGroupOrderBy?.Select(i => new OrderByItem(Rec(i.Expression), i.Direction)).ToList(),
            },
            BinaryExpression b => new BinaryExpression(Rec(b.Left), b.Operator, Rec(b.Right)),
            UnaryExpression u => new UnaryExpression(u.Operator, Rec(u.Operand)),
            CastExpression c => c with { Expression = Rec(c.Expression) },
            CaseExpression ce => new CaseExpression(
                ce.Operand is { } o ? Rec(o) : null,
                ce.WhenClauses.Select(w => new WhenClause(Rec(w.Condition), Rec(w.Result))).ToList(),
                ce.ElseResult is { } er ? Rec(er) : null,
                ce.Span),
            InExpression ie => new InExpression(Rec(ie.Expression), ie.Values.Select(Rec).ToList(), ie.Negated),
            BetweenExpression be => new BetweenExpression(Rec(be.Expression), Rec(be.Low), Rec(be.High), be.Negated),
            IsNullExpression isn => new IsNullExpression(Rec(isn.Expression), isn.Negated),
            LikeExpression lk => new LikeExpression(Rec(lk.Expression), Rec(lk.Pattern), Rec(lk.EscapeCharacter), lk.CaseInsensitive),
            AtTimeZoneExpression atz => atz with { Expression = Rec(atz.Expression), TimeZone = Rec(atz.TimeZone) },
            LambdaExpression lam => lam with { Body = Rec(lam.Body) },
            IndexAccessExpression ix => ix with { Source = Rec(ix.Source), Indices = ix.Indices.Select(Rec).ToArray() },
            StructLiteralExpression sl => sl with
            {
                Fields = sl.Fields.Select(f => new StructField(f.Name, Rec(f.Value))).ToList(),
            },
            InlineAccessorExpression iax => iax with { Argument = Rec(iax.Argument) },
            _ => expression,
        };
    }
}

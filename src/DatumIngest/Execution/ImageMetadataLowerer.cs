using DatumIngest.Functions;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Plan-time AST rewrite that decomposes the composite image-metadata
/// functions <c>pixel_count(img)</c> and <c>dimensions(img, format)</c>
/// into compositions of the elidable accessors
/// <see cref="DatumIngest.Functions.Scalar.Image.ImageWidthFunction"/>,
/// <see cref="DatumIngest.Functions.Scalar.Image.ImageHeightFunction"/>, and
/// <see cref="DatumIngest.Functions.Scalar.Image.ImageChannelsFunction"/>.
/// </summary>
/// <remarks>
/// <para>
/// The decomposition lets the subsequent
/// <see cref="InlineAccessorElider"/> pass turn each inner accessor call
/// into a struct-byte read, and lets CSE collapse sibling width / height /
/// channels references that already appear in the same query — so
/// <c>SELECT pixel_count(img) FROM t WHERE image_width(img) &gt; 100</c>
/// resolves <c>image_width(img)</c> once instead of twice.
/// </para>
/// <para>
/// <strong>Pipeline placement.</strong> Runs <em>before</em>
/// <see cref="InlineAccessorElider"/> so the freshly-inserted accessor
/// calls reach the elider in their canonical
/// <see cref="FunctionCallExpression"/> shape.
/// </para>
/// <para>
/// <strong>What lowers and what doesn't.</strong>
/// <list type="bullet">
///   <item><c>pixel_count(img)</c> always lowers to
///   <c>image_width(img) * image_height(img)</c>.</item>
///   <item><c>dimensions(img, literal)</c> lowers to
///   <c>array(...)</c> of the appropriate accessor calls when the
///   <c>format</c> argument is a string literal. Non-literal format
///   (e.g. a column reference) passes through and is handled by the
///   runtime <see cref="DatumIngest.Functions.Scalar.Image.ImageDimensionsFunction"/>
///   body.</item>
/// </list>
/// </para>
/// <para>
/// The rewrite consults the live <see cref="FunctionRegistry"/> with the
/// catalog search path so a user UDF shadowing one of these names blocks
/// the lowering — the resolved function must be the built-in for the
/// rewrite to fire.
/// </para>
/// </remarks>
public static class ImageMetadataLowerer
{
    /// <summary>
    /// Walks the operator tree's expressions and rewrites every eligible
    /// call site. Mirrors the
    /// <see cref="InlineAccessorElider"/> shape — a single
    /// <see cref="QueryOperator.RewriteExpressions"/> call recurses
    /// through every operator with expression slots.
    /// </summary>
    public static QueryOperator Lower(
        QueryOperator op, FunctionRegistry functions, IReadOnlyList<string> searchPath)
        => op.RewriteExpressions(expr => RewriteExpression(expr, functions, searchPath));

    private static Expression RewriteExpression(
        Expression expression, FunctionRegistry functions, IReadOnlyList<string> searchPath)
    {
        Expression rewritten = RewriteChildren(expression, functions, searchPath);

        if (rewritten is not FunctionCallExpression call)
        {
            return rewritten;
        }

        // Only built-ins resolve; a shadowing UDF won't match the lowering target.
        Functions.IScalarFunction? resolved = functions.TryGetScalar(call.SchemaName, call.FunctionName, searchPath);

        if (resolved is Functions.Scalar.Image.ImagePixelCountFunction && IsUnaryShape(call))
        {
            return LowerPixelCount(call.Arguments[0]);
        }

        if (resolved is Functions.Scalar.Image.ImageDimensionsFunction &&
            IsBinaryShape(call) &&
            call.Arguments[1] is LiteralExpression { Value: string literal })
        {
            return LowerDimensions(call.Arguments[0], literal);
        }

        return rewritten;
    }

    /// <summary>
    /// <c>pixel_count(img)</c> →
    /// <c>image_width(img) * image_height(img)</c>. The element argument is
    /// emitted as-is twice; CSE deduplicates if the subtree is non-trivial.
    /// </summary>
    private static Expression LowerPixelCount(Expression imageArg) =>
        new BinaryExpression(
            CallAccessor("image_width", imageArg),
            BinaryOperator.Multiply,
            CallAccessor("image_height", imageArg));

    /// <summary>
    /// <c>dimensions(img, 'HWC')</c> →
    /// <c>array(image_height(img), image_width(img), image_channels(img))</c>.
    /// Unknown formats are left for the runtime function body to reject —
    /// keeps error reporting consistent regardless of which path fires.
    /// </summary>
    private static Expression LowerDimensions(Expression imageArg, string format)
    {
        string upper = format.ToUpperInvariant();
        Expression w() => CallAccessor("image_width", imageArg);
        Expression h() => CallAccessor("image_height", imageArg);
        Expression c() => CallAccessor("image_channels", imageArg);

        List<Expression>? elements = upper switch
        {
            "WH"  => [w(), h()],
            "WHC" => [w(), h(), c()],
            "HWC" => [h(), w(), c()],
            "CHW" => [c(), h(), w()],
            _ => null,
        };

        if (elements is null)
        {
            // Leave the original call in place — the runtime body will surface
            // the unsupported-format error from its own validation.
            return new FunctionCallExpression(
                SchemaName: null,
                FunctionName: "dimensions",
                Arguments: [imageArg, new LiteralExpression(format)]);
        }

        return new FunctionCallExpression(
            SchemaName: null,
            FunctionName: "array",
            Arguments: elements);
    }

    private static FunctionCallExpression CallAccessor(string name, Expression argument) =>
        new(SchemaName: null, FunctionName: name, Arguments: [argument]);

    private static bool IsUnaryShape(FunctionCallExpression call)
        => call.Arguments.Count == 1
        && call.OrderBy is null or { Count: 0 }
        && call.WithinGroupOrderBy is null or { Count: 0 }
        && !call.Distinct;

    private static bool IsBinaryShape(FunctionCallExpression call)
        => call.Arguments.Count == 2
        && call.OrderBy is null or { Count: 0 }
        && call.WithinGroupOrderBy is null or { Count: 0 }
        && !call.Distinct;

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

using DatumIngest.Catalog;
using DatumIngest.Catalog.Registries;
using DatumIngest.Execution.Operators;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Lowers a SQL-defined model's procedural body into a column pipeline
/// (chain of <see cref="ProjectOperator"/> and <see cref="InferOperator"/>
/// nodes) that replaces the body's per-row interpretation with batched
/// dispatch. The architectural payoff: a <c>CREATE MODEL</c> body
/// produces the same physical plan a hand-written C# <c>IModel</c> would,
/// so engine-baked built-ins become deletable as their SQL counterparts ship.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Straight-line only.</strong> v1 lowers bodies that are a
/// sequence of <c>DECLARE @v = expr</c> statements followed by a single
/// terminating <c>RETURN expr</c>. Anything else (<c>IF</c>, <c>WHILE</c>,
/// <c>SET</c> after <c>DECLARE</c>, <c>BREAK</c>/<c>CONTINUE</c>,
/// nested blocks, typed-null DECLAREs without an initializer) falls
/// through to the row-at-a-time <c>ProceduralModelAdapter</c> path that
/// step 2 wired into MIO.
/// </para>
/// <para>
/// <strong>Substitution semantics.</strong> Each body-parameter reference
/// <c>@p</c> is replaced with the corresponding call-site argument
/// expression — inlined directly, no intermediate hidden column. Each
/// declared body-variable reference <c>@v</c> is replaced with a
/// <see cref="ColumnReference"/> to the synthesised column the DECLARE
/// produced. The lowered plan therefore has no procedural variable scope —
/// variables become columns.
/// </para>
/// <para>
/// <strong>infer() lowers to InferOperator.</strong> Every <c>infer(x)</c>
/// call in a DECLARE or RETURN is split: the argument expression is
/// projected into a hidden column, then an <see cref="InferOperator"/>
/// reads from that column and writes the dispatch result to the
/// statement's output column. The body's <c>infer()</c> scalar function
/// is never reached at runtime in a lowered plan — InferOperator owns
/// the session dispatch directly.
/// </para>
/// </remarks>
public static class ModelBodyLowerer
{
    /// <summary>
    /// Post-pass over a hoisted operator tree: replaces every
    /// <see cref="ModelInvocationOperator"/> that targets a SQL-defined
    /// model with a lowered sub-plan (chain of
    /// <see cref="ProjectOperator"/> + <see cref="InferOperator"/>),
    /// when the model's body satisfies <see cref="BodyIsStraightLine"/>.
    /// Built-in models and non-straight-line bodies are left alone — they
    /// keep dispatching through MIO.
    /// </summary>
    /// <param name="op">Operator tree after hoisting.</param>
    /// <param name="declaredModels">
    /// Registry of SQL-defined models. <see langword="null"/> short-
    /// circuits to identity (no lowering attempted).
    /// </param>
    public static IQueryOperator LowerSqlDefinedBodies(
        IQueryOperator op,
        ModelRegistry? declaredModels)
    {
        if (declaredModels is null) return op;
        return LowerRecursive(op, declaredModels);
    }

    private static IQueryOperator LowerRecursive(IQueryOperator op, ModelRegistry declaredModels)
    {
        // Pre-order: descend into children first, then try to lower this
        // node. That way nested MIOs (a model whose argument is itself a
        // model call) lower bottom-up correctly.
        IQueryOperator rewritten = ModelInvocationHoister.RewriteChildren(
            op, child => LowerRecursive(child, declaredModels));

        if (rewritten is not ModelInvocationOperator mio) return rewritten;

        // Only SQL-defined models live in DeclaredModels under the `models` schema.
        if (!declaredModels.TryGet(new QualifiedName(ModelsSchema, mio.ModelName), out ModelDescriptor? descriptor))
        {
            return rewritten; // built-in IModel — stays on MIO path
        }

        if (!BodyIsStraightLine(descriptor.StatementBody)) return rewritten;

        // The lowerer needs every parameter supplied (no default-expansion in
        // v1). MIO splits args into Input + Optional based on the catalog
        // entry's OptionalArgKinds; for SQL-defined models the union of
        // those lists is the full parameter list in declaration order.
        IReadOnlyList<Expression> allArgs =
            mio.OptionalExpressions.Count == 0
                ? mio.InputExpressions
                : (IReadOnlyList<Expression>)mio.InputExpressions.Concat(mio.OptionalExpressions).ToList();
        if (allArgs.Count != descriptor.Parameters.Count) return rewritten;

        IQueryOperator? lowered = TryLower(descriptor, mio.Source, allArgs, mio.OutputColumnName);
        return lowered ?? rewritten;
    }

    private const string ModelsSchema = "models";

    /// <summary>
    /// Returns whether <paramref name="body"/> is a straight-line shape
    /// the lowerer can handle. Bodies that fail this predicate fall
    /// through to the row-at-a-time <c>ProceduralModelAdapter</c> path.
    /// </summary>
    public static bool BodyIsStraightLine(IReadOnlyList<Statement> body)
    {
        if (body.Count == 0) return false;
        for (int i = 0; i < body.Count; i++)
        {
            switch (body[i])
            {
                case DeclareStatement decl:
                    // DECLARE must have an initializer — typed-null DECLAREs
                    // would need a separate "introduce a literal NULL
                    // column" path the lowerer doesn't bother with.
                    if (decl.Initializer is null) return false;
                    break;
                case ReturnStatement when i == body.Count - 1:
                    // Tail RETURN is the only valid terminator.
                    break;
                default:
                    // SET, IF, WHILE, BLOCK, BREAK, CONTINUE, mid-body RETURN,
                    // CALL, etc. — anything else fails straight-line.
                    return false;
            }
        }
        return body[^1] is ReturnStatement;
    }

    /// <summary>
    /// Lowers <paramref name="descriptor"/>'s straight-line body into a
    /// chain of operators rooted at <paramref name="source"/>. Returns
    /// <see langword="null"/> when the body isn't lowerable (caller
    /// should fall back to the row-at-a-time adapter path).
    /// </summary>
    /// <param name="descriptor">SQL-defined model descriptor.</param>
    /// <param name="source">Upstream operator providing the call-site rows.</param>
    /// <param name="argExpressions">
    /// One expression per body parameter, in declaration order. Defaults
    /// are not expanded here — callers supply explicit expressions for
    /// every parameter (the hoister already enforces this).
    /// </param>
    /// <param name="outputColumnName">
    /// Name the final synthesised column will carry. Matches what the
    /// hoister's outer rewriter expects to <c>ColumnReference</c> to in
    /// the caller's expression.
    /// </param>
    public static IQueryOperator? TryLower(
        ModelDescriptor descriptor,
        IQueryOperator source,
        IReadOnlyList<Expression> argExpressions,
        string outputColumnName)
    {
        if (!BodyIsStraightLine(descriptor.StatementBody)) return null;
        // v1: require exact arity. Defaults turn the call into a
        // not-straight-line shape (BindParameters would need to evaluate
        // the default expression). Defer.
        if (argExpressions.Count != descriptor.Parameters.Count) return null;

        Dictionary<string, Expression> paramSubst = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < descriptor.Parameters.Count; i++)
        {
            paramSubst[descriptor.Parameters[i].Name] = argExpressions[i];
        }

        // Body-variable name → the synthesised column name carrying its value.
        Dictionary<string, string> declaredVars = new(StringComparer.OrdinalIgnoreCase);

        IQueryOperator current = source;
        int stmtIdx = 0;

        foreach (Statement stmt in descriptor.StatementBody)
        {
            if (stmt is DeclareStatement decl)
            {
                Expression rhs = SubstituteRefs(decl.Initializer!, paramSubst, declaredVars);
                string colName = $"__mb_{descriptor.Name}_{stmtIdx}_{decl.VariableName}";
                current = AppendStatement(current, descriptor, rhs, colName);
                declaredVars[decl.VariableName] = colName;
            }
            else if (stmt is ReturnStatement ret)
            {
                Expression returnExpr = SubstituteRefs(ret.Value, paramSubst, declaredVars);
                current = AppendStatement(current, descriptor, returnExpr, outputColumnName);
                return current;
            }
            stmtIdx++;
        }
        // BodyIsStraightLine guarantees a tail RETURN, so we should never
        // fall through. Defensive throw in case the predicate ever drifts.
        throw new InvalidOperationException(
            $"Model '{descriptor.QualifiedName}': body lowered without a RETURN. " +
            "BodyIsStraightLine should have rejected this body before lowering.");
    }

    /// <summary>
    /// Emits the operator(s) for one body statement's right-hand side.
    /// When the RHS is an <c>infer()</c> call we split it: project the
    /// argument expression into a hidden column, then an InferOperator
    /// reads from that column. Otherwise just project the expression as
    /// the named output column.
    /// </summary>
    private static IQueryOperator AppendStatement(
        IQueryOperator source,
        ModelDescriptor descriptor,
        Expression expr,
        string outputColumn)
    {
        if (expr is FunctionCallExpression fc && IsInferCall(fc) && fc.Arguments.Count == 1)
        {
            string inputCol = $"{outputColumn}__in";
            IQueryOperator projected = AddDerivedColumn(source, inputCol, fc.Arguments[0]);
            return new InferOperator(
                projected,
                descriptor,
                sessionName: "default",
                inputColumnName: inputCol,
                outputColumnName: outputColumn);
        }
        return AddDerivedColumn(source, outputColumn, expr);
    }

    /// <summary>
    /// Returns a <see cref="ProjectOperator"/> that passes every source
    /// column through and appends one new column named
    /// <paramref name="colName"/> carrying <paramref name="expr"/>. Used
    /// for both DECLARE-as-column and the final RETURN projection.
    /// </summary>
    private static IQueryOperator AddDerivedColumn(
        IQueryOperator source,
        string colName,
        Expression expr)
    {
        IReadOnlyList<SelectColumn> columns =
        [
            new SelectAllColumns(),
            new SelectColumn(expr, Alias: colName),
        ];
        return new ProjectOperator(source, columns);
    }

    /// <summary>
    /// Returns whether <paramref name="fn"/> is a call to the built-in
    /// <c>infer()</c> function. Matches by FunctionName only; <c>infer</c>
    /// is unqualified (no schema prefix) at the SQL surface.
    /// </summary>
    private static bool IsInferCall(FunctionCallExpression fn) =>
        fn.SchemaName is null
        && string.Equals(fn.FunctionName, "infer", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Walks <paramref name="expression"/> and rewrites every unqualified
    /// <see cref="ColumnReference"/> that matches a body parameter or a
    /// previously-declared variable. Parameter references inline the
    /// caller's argument expression directly; variable references become
    /// <c>ColumnReference</c>s to the synthesised intermediate column.
    /// </summary>
    private static Expression SubstituteRefs(
        Expression expression,
        IReadOnlyDictionary<string, Expression> paramSubst,
        IReadOnlyDictionary<string, string> declaredVars)
    {
        return expression switch
        {
            ColumnReference cr when cr.TableName is null
                    && paramSubst.TryGetValue(cr.ColumnName, out Expression? argExpr) =>
                argExpr,

            ColumnReference cr when cr.TableName is null
                    && declaredVars.TryGetValue(cr.ColumnName, out string? colName) =>
                new ColumnReference(TableName: null, ColumnName: colName),

            FunctionCallExpression fn => fn with
            {
                Arguments = fn.Arguments
                    .Select(a => SubstituteRefs(a, paramSubst, declaredVars))
                    .ToList(),
            },

            BinaryExpression b => b with
            {
                Left = SubstituteRefs(b.Left, paramSubst, declaredVars),
                Right = SubstituteRefs(b.Right, paramSubst, declaredVars),
            },

            UnaryExpression u => u with { Operand = SubstituteRefs(u.Operand, paramSubst, declaredVars) },
            CastExpression c => c with { Expression = SubstituteRefs(c.Expression, paramSubst, declaredVars) },
            IsNullExpression n => n with { Expression = SubstituteRefs(n.Expression, paramSubst, declaredVars) },

            InExpression i => i with
            {
                Expression = SubstituteRefs(i.Expression, paramSubst, declaredVars),
                Values = i.Values.Select(v => SubstituteRefs(v, paramSubst, declaredVars)).ToList(),
            },

            BetweenExpression bt => bt with
            {
                Expression = SubstituteRefs(bt.Expression, paramSubst, declaredVars),
                Low = SubstituteRefs(bt.Low, paramSubst, declaredVars),
                High = SubstituteRefs(bt.High, paramSubst, declaredVars),
            },

            CaseExpression ce => ce with
            {
                Operand = ce.Operand is null ? null : SubstituteRefs(ce.Operand, paramSubst, declaredVars),
                WhenClauses = ce.WhenClauses
                    .Select(w => new WhenClause(
                        SubstituteRefs(w.Condition, paramSubst, declaredVars),
                        SubstituteRefs(w.Result, paramSubst, declaredVars)))
                    .ToList(),
                ElseResult = ce.ElseResult is null ? null : SubstituteRefs(ce.ElseResult, paramSubst, declaredVars),
            },

            LikeExpression like => like with
            {
                Expression = SubstituteRefs(like.Expression, paramSubst, declaredVars),
                Pattern = SubstituteRefs(like.Pattern, paramSubst, declaredVars),
                EscapeCharacter = SubstituteRefs(like.EscapeCharacter, paramSubst, declaredVars),
            },

            StructLiteralExpression sl => sl with
            {
                Fields = sl.Fields
                    .Select(f => new StructField(f.Name, SubstituteRefs(f.Value, paramSubst, declaredVars)))
                    .ToList(),
            },

            IndexAccessExpression ia => ia with
            {
                Source = SubstituteRefs(ia.Source, paramSubst, declaredVars),
                Index = SubstituteRefs(ia.Index, paramSubst, declaredVars),
            },

            AtTimeZoneExpression atz => atz with
            {
                Expression = SubstituteRefs(atz.Expression, paramSubst, declaredVars),
                TimeZone = SubstituteRefs(atz.TimeZone, paramSubst, declaredVars),
            },

            LambdaExpression lam => lam with
            {
                Body = SubstituteRefs(lam.Body, paramSubst, declaredVars),
            },

            // Literals, plain column refs that aren't params/vars,
            // and everything else pass through unchanged.
            _ => expression,
        };
    }
}

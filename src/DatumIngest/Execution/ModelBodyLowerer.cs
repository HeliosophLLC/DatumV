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
    public static QueryOperator LowerSqlDefinedBodies(
        QueryOperator op,
        ModelRegistry? declaredModels)
    {
        if (declaredModels is null) return op;
        return LowerRecursive(op, declaredModels);
    }

    private static QueryOperator LowerRecursive(QueryOperator op, ModelRegistry declaredModels)
    {
        // Pre-order: descend into children first, then try to lower this
        // node. That way nested MIOs (a model whose argument is itself a
        // model call) lower bottom-up correctly.
        QueryOperator rewritten = ModelInvocationHoister.RewriteChildren(
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

        QueryOperator? lowered = TryLower(descriptor, mio.Source, allArgs, mio.OutputColumnName);
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
                    if (ContainsMultiInputInfer(decl.Initializer)) return false;
                    // Struct-typed locals push the body off the lowerer's
                    // happy path: downstream `infer()` may reference the
                    // struct by column-ref (no struct literal at the call
                    // site) and InferOperator can't unpack a struct column
                    // into per-input tensors. The MIO + adapter path
                    // handles struct locals natively via VariableScope.
                    if (IsStructTypeName(decl.TypeName)) return false;
                    // Catalog-relative-path functions read state off
                    // `frame.CurrentModel`, which the lowered operator
                    // pipeline doesn't carry today (operators run outside
                    // the model body's frame). Bail so the MIO + adapter
                    // path takes the body — that path runs the body
                    // through `ProceduralModelFunction` which constructs
                    // a frame with `currentModel` set. Long-term fix:
                    // plumb the descriptor into lowered operators.
                    if (ContainsCatalogRelativeCall(decl.Initializer)) return false;
                    break;
                case ReturnStatement ret when i == body.Count - 1:
                    // Tail RETURN is the only valid terminator.
                    if (ContainsMultiInputInfer(ret.Value)) return false;
                    if (ContainsCatalogRelativeCall(ret.Value)) return false;
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
    /// Cheap textual check for a struct typename annotation. Catches the
    /// common surface forms (<c>Struct</c>, <c>STRUCT</c>, <c>Struct&lt;...&gt;</c>).
    /// False negatives only occur when the body's struct local lacks a
    /// type annotation entirely — caller's response is "lower this and
    /// let the runtime decide", which is fine since unconstrained
    /// inference still flows through MIO when the lowerer bails for a
    /// different reason.
    /// </summary>
    private static bool IsStructTypeName(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        return typeName.StartsWith("Struct", StringComparison.OrdinalIgnoreCase)
            || typeName.StartsWith("STRUCT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns whether <paramref name="expr"/> (or any sub-expression)
    /// is a multi-input <c>infer()</c> call — i.e. an infer whose first
    /// argument is a struct literal. The lowerer's InferOperator path
    /// only handles single-tensor inputs (with an optional shape array);
    /// multi-input bodies fall back to the row-at-a-time
    /// <c>ProceduralModelAdapter</c> path where the scalar
    /// <c>InferFunction</c> handles the struct unpack natively.
    /// </summary>
    private static bool ContainsMultiInputInfer(Expression? expr)
    {
        if (expr is null) return false;
        switch (expr)
        {
            case FunctionCallExpression fc:
                if (IsInferCall(fc))
                {
                    // Any struct-literal argument to infer() flags multi-
                    // input dispatch — covers infer({...}) (1-arg, value
                    // struct) and infer(value, {...}) (2-arg, shape struct
                    // for either the single-input or multi-input form).
                    for (int i = 0; i < fc.Arguments.Count; i++)
                    {
                        if (fc.Arguments[i] is StructLiteralExpression) return true;
                    }
                }
                for (int i = 0; i < fc.Arguments.Count; i++)
                {
                    if (ContainsMultiInputInfer(fc.Arguments[i])) return true;
                }
                return false;
            case BinaryExpression b:
                return ContainsMultiInputInfer(b.Left) || ContainsMultiInputInfer(b.Right);
            case UnaryExpression u:
                return ContainsMultiInputInfer(u.Operand);
            case CastExpression c:
                return ContainsMultiInputInfer(c.Expression);
            case IsNullExpression n:
                return ContainsMultiInputInfer(n.Expression);
            case InExpression i:
                if (ContainsMultiInputInfer(i.Expression)) return true;
                for (int k = 0; k < i.Values.Count; k++)
                {
                    if (ContainsMultiInputInfer(i.Values[k])) return true;
                }
                return false;
            case BetweenExpression bt:
                return ContainsMultiInputInfer(bt.Expression)
                    || ContainsMultiInputInfer(bt.Low)
                    || ContainsMultiInputInfer(bt.High);
            case CaseExpression ce:
                if (ce.Operand is not null && ContainsMultiInputInfer(ce.Operand)) return true;
                for (int k = 0; k < ce.WhenClauses.Count; k++)
                {
                    if (ContainsMultiInputInfer(ce.WhenClauses[k].Condition)) return true;
                    if (ContainsMultiInputInfer(ce.WhenClauses[k].Result)) return true;
                }
                return ce.ElseResult is not null && ContainsMultiInputInfer(ce.ElseResult);
            case LikeExpression like:
                return ContainsMultiInputInfer(like.Expression)
                    || ContainsMultiInputInfer(like.Pattern)
                    || ContainsMultiInputInfer(like.EscapeCharacter);
            case StructLiteralExpression sl:
                for (int k = 0; k < sl.Fields.Count; k++)
                {
                    if (ContainsMultiInputInfer(sl.Fields[k].Value)) return true;
                }
                return false;
            case IndexAccessExpression ia:
                return ContainsMultiInputInfer(ia.Source) || ContainsMultiInputInfer(ia.Index);
            case AtTimeZoneExpression atz:
                return ContainsMultiInputInfer(atz.Expression) || ContainsMultiInputInfer(atz.TimeZone);
            case LambdaExpression lam:
                return ContainsMultiInputInfer(lam.Body);
            default:
                return false;
        }
    }

    /// <summary>
    /// Functions that resolve catalog-relative paths (or otherwise read
    /// state off <c>frame.CurrentModel</c>) and therefore can't run
    /// through the lowered operator pipeline, which constructs frames
    /// without a <c>currentModel</c> binding. Bodies that call any of
    /// these fall through to the MIO + <c>ProceduralModelAdapter</c>
    /// path, which runs the body through <c>ProceduralModelFunction</c>
    /// and builds a frame with <c>currentModel</c> set.
    /// </summary>
    /// <remarks>
    /// Long-term: plumb the descriptor into the lowered
    /// <see cref="Operators.ProjectOperator"/> / <see cref="Operators.InferOperator"/>
    /// so they construct frames with <c>currentModel</c>. Until then,
    /// the bail list is the safe path.
    /// </remarks>
    private static readonly HashSet<string> CatalogRelativeFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        // system schema
        "read_string_list",
        // tokenizer schema — relative paths resolve against the model's USING dir
        "tokenizer.encode_bert",
        "tokenizer.encode_bert_pair",
        "tokenizer.encode_roberta",
    };

    /// <summary>
    /// True when <paramref name="expr"/> (or any sub-expression) calls a
    /// catalog-relative-path function — see
    /// <see cref="CatalogRelativeFunctions"/> for the bail list.
    /// </summary>
    private static bool ContainsCatalogRelativeCall(Expression? expr)
    {
        if (expr is null) return false;
        switch (expr)
        {
            case FunctionCallExpression fc:
                if (CatalogRelativeFunctions.Contains(fc.CallName)) return true;
                for (int i = 0; i < fc.Arguments.Count; i++)
                {
                    if (ContainsCatalogRelativeCall(fc.Arguments[i])) return true;
                }
                return false;
            case BinaryExpression b:
                return ContainsCatalogRelativeCall(b.Left) || ContainsCatalogRelativeCall(b.Right);
            case UnaryExpression u:
                return ContainsCatalogRelativeCall(u.Operand);
            case CastExpression c:
                return ContainsCatalogRelativeCall(c.Expression);
            case IndexAccessExpression ia:
                return ContainsCatalogRelativeCall(ia.Source) || ContainsCatalogRelativeCall(ia.Index);
            case StructLiteralExpression sl:
                for (int k = 0; k < sl.Fields.Count; k++)
                {
                    if (ContainsCatalogRelativeCall(sl.Fields[k].Value)) return true;
                }
                return false;
            case CaseExpression ce:
                if (ce.Operand is not null && ContainsCatalogRelativeCall(ce.Operand)) return true;
                for (int k = 0; k < ce.WhenClauses.Count; k++)
                {
                    if (ContainsCatalogRelativeCall(ce.WhenClauses[k].Condition)) return true;
                    if (ContainsCatalogRelativeCall(ce.WhenClauses[k].Result)) return true;
                }
                return ce.ElseResult is not null && ContainsCatalogRelativeCall(ce.ElseResult);
            default:
                return false;
        }
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
    public static QueryOperator? TryLower(
        ModelDescriptor descriptor,
        QueryOperator source,
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

        QueryOperator current = source;
        int stmtIdx = 0;

        foreach (Statement stmt in descriptor.StatementBody)
        {
            if (stmt is DeclareStatement decl)
            {
                Expression rhs = SubstituteRefs(decl.Initializer!, paramSubst, declaredVars);
                string colName = $"__mb_{descriptor.Name}_{stmtIdx}_{decl.VariableName}";
                // Snapshot the prior synth column names BEFORE adding this one
                // so each new Project explicitly preserves the previous
                // hidden columns. SelectAllColumns filters out `__`-prefixed
                // names; without explicit passthrough, the chain loses each
                // synthesized column on the next step.
                string[] priorSynth = declaredVars.Values.ToArray();
                current = AppendStatement(current, descriptor, rhs, colName, priorSynth);
                declaredVars[decl.VariableName] = colName;
            }
            else if (stmt is ReturnStatement ret)
            {
                Expression returnExpr = SubstituteRefs(ret.Value, paramSubst, declaredVars);
                string[] priorSynth = declaredVars.Values.ToArray();
                current = AppendStatement(current, descriptor, returnExpr, outputColumnName, priorSynth);
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
    /// the named output column. <paramref name="priorSynthColumns"/>
    /// lists the synthesized hidden column names from earlier DECLAREs;
    /// each Project explicitly passes them through alongside
    /// <c>SELECT *</c> so the chain doesn't lose them to the
    /// hidden-name filter.
    /// </summary>
    private static QueryOperator AppendStatement(
        QueryOperator source,
        ModelDescriptor descriptor,
        Expression expr,
        string outputColumn,
        IReadOnlyList<string> priorSynthColumns)
    {
        if (expr is FunctionCallExpression fc && IsInferCall(fc))
        {
            // 1-arg form: infer(value). Shape comes from the session's
            // input spec — works for ≤1 dynamic dim.
            if (fc.Arguments.Count == 1)
            {
                string inputCol = $"{outputColumn}__in";
                QueryOperator projected = AddDerivedColumn(source, inputCol, fc.Arguments[0], priorSynthColumns);
                return new InferOperator(
                    projected,
                    descriptor,
                    sessionName: "default",
                    inputColumnName: inputCol,
                    outputColumnName: outputColumn);
            }
            // 2-arg form: infer(value, shape Int32[]). Project both the
            // input and the shape into hidden columns, then InferOperator
            // reads the shape per row. Required for multi-dynamic-dim
            // sessions (PP-OCR-det's [-1, 3, -1, -1] is the canonical case).
            if (fc.Arguments.Count == 2)
            {
                string inputCol = $"{outputColumn}__in";
                string shapeCol = $"{outputColumn}__shape";
                // Two Projects so each new column lands on a stable
                // upstream batch. The shape Project also passes the input
                // column through (it's part of priorSynthColumns at that
                // point) so InferOperator sees both on its source row.
                QueryOperator afterInput = AddDerivedColumn(source, inputCol, fc.Arguments[0], priorSynthColumns);
                IReadOnlyList<string> priorPlusInput = AppendOne(priorSynthColumns, inputCol);
                QueryOperator afterShape = AddDerivedColumn(afterInput, shapeCol, fc.Arguments[1], priorPlusInput);
                return new InferOperator(
                    afterShape,
                    descriptor,
                    sessionName: "default",
                    inputColumnName: inputCol,
                    outputColumnName: outputColumn,
                    shapeColumnName: shapeCol);
            }
            // 3+ args: not a known infer() shape; fall through to the
            // generic AddDerivedColumn path. The scalar dispatch will
            // throw a clean arity error at evaluation time.
        }
        return AddDerivedColumn(source, outputColumn, expr, priorSynthColumns);
    }

    /// <summary>
    /// Appends one name to a read-only list and returns the result.
    /// Used by the 2-arg <c>infer(value, shape)</c> lowering path: the
    /// second Project (which produces the shape column) needs to see the
    /// just-added input column among its passthrough set so both columns
    /// reach the downstream InferOperator on the same row.
    /// </summary>
    private static IReadOnlyList<string> AppendOne(IReadOnlyList<string> source, string extra)
    {
        string[] result = new string[source.Count + 1];
        for (int i = 0; i < source.Count; i++) result[i] = source[i];
        result[^1] = extra;
        return result;
    }

    /// <summary>
    /// Returns a <see cref="ProjectOperator"/> that passes every source
    /// column through and appends one new column named
    /// <paramref name="colName"/> carrying <paramref name="expr"/>. Used
    /// for both DECLARE-as-column and the final RETURN projection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Hidden column passthrough.</strong> <c>SelectAllColumns</c>
    /// expansion explicitly skips <c>__</c>-prefixed names (so user
    /// queries' <c>SELECT *</c> doesn't leak planner-internal columns).
    /// The lowerer's synth columns use that prefix on purpose — they're
    /// hidden from end users — but each Project in the chain needs the
    /// previous ones to survive so subsequent DECLAREs can reference them.
    /// We explicitly list each prior synth column as a passthrough
    /// <see cref="SelectColumn"/> alongside <c>SelectAllColumns</c>.
    /// </para>
    /// </remarks>
    private static QueryOperator AddDerivedColumn(
        QueryOperator source,
        string colName,
        Expression expr,
        IReadOnlyList<string> priorSynthColumns)
    {
        List<SelectColumn> columns = new(capacity: priorSynthColumns.Count + 2)
        {
            // User-visible columns from upstream (img, etc.). The hidden-
            // name filter inside SelectAllColumns ensures __mb_* / __cse_*
            // / other planner-synthetic names don't surface twice — they
            // come through the explicit entries below instead.
            new SelectAllColumns(),
        };
        // Explicit passthrough of every prior synthesized hidden column.
        // Without these, the chain loses each __mb_* column at the next
        // step's SelectAllColumns expansion.
        for (int i = 0; i < priorSynthColumns.Count; i++)
        {
            string prior = priorSynthColumns[i];
            columns.Add(new SelectColumn(
                new ColumnReference(TableName: null, ColumnName: prior),
                Alias: prior));
        }
        // And the new derived column for this statement.
        columns.Add(new SelectColumn(expr, Alias: colName));
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

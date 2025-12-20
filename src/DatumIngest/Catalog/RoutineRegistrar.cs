using DatumIngest.Execution;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog;

/// <summary>
/// Owns the routine-DDL side of a <see cref="TableCatalog"/>: registering and
/// unregistering UDFs and procedures, validating their bodies against the
/// inliner, and writing the result through to the optional
/// <see cref="CatalogStore"/>. Extracted from <see cref="TableCatalog"/> so the
/// catalog stays focused on table/provider concerns and the routine-management
/// surface lives in one place.
/// </summary>
/// <remarks>
/// All four <c>Apply</c> entry points map 1:1 to a SQL statement:
/// <c>CREATE FUNCTION</c>, <c>DROP FUNCTION</c>, <c>CREATE PROCEDURE</c>,
/// <c>DROP PROCEDURE</c>. Each method mutates the registries and, when a
/// <see cref="CatalogStore"/> is configured, persists the resulting state
/// atomically. The dispatch logic itself stays in
/// <see cref="TableCatalog.Plan(string)"/> / <see cref="TableCatalog.Plan(Statement)"/>;
/// callers reach this class only through the catalog.
/// </remarks>
internal sealed class RoutineRegistrar
{
    private readonly UdfRegistry _udfs;
    private readonly ProcedureRegistry _procedures;
    private readonly CatalogStore? _catalogStore;

    /// <summary>
    /// Wires the registrar to the registries and (optional) persistent store
    /// it operates on. The instances are held by reference — every mutation
    /// goes through the same UDF / procedure registries the catalog exposes
    /// publicly, and every save targets the same file.
    /// </summary>
    public RoutineRegistrar(
        UdfRegistry udfs,
        ProcedureRegistry procedures,
        CatalogStore? catalogStore)
    {
        _udfs = udfs;
        _procedures = procedures;
        _catalogStore = catalogStore;
    }

    // ───────────────────── Functions ─────────────────────

    /// <summary>
    /// Applies a <c>CREATE FUNCTION</c> statement: validates the body,
    /// builds the descriptor, and registers it (replacing any existing
    /// entry when <see cref="CreateFunctionStatement.OrReplace"/> is set).
    /// <paramref name="sourceText"/> is the verbatim SQL captured by the
    /// catalog's <c>Plan(string)</c> dispatch — required for procedural
    /// UDFs (it round-trips through the parser on rehydrate) and ignored
    /// for macros.
    /// </summary>
    public void ApplyCreateFunction(CreateFunctionStatement create, string? sourceText = null)
    {
        // Defaults must be contiguous at the tail of the parameter list so
        // call-site arity stays unambiguous (positional matching only).
        ValidateDefaultsContiguous(create.Parameters, $"CREATE FUNCTION {create.Name}");

        UdfDescriptor descriptor = create.StatementBody is not null
            ? BuildProceduralDescriptor(create, sourceText)
            : BuildMacroDescriptor(create);

        if (create.IfNotExists && _udfs.TryGet(create.Name, out _))
        {
            return;
        }

        _udfs.Register(descriptor, replace: create.OrReplace);
        _catalogStore?.Save(_udfs, _procedures);
    }

    /// <summary>
    /// Applies a <c>DROP FUNCTION</c> statement. Throws when the named UDF
    /// isn't registered unless the statement carries <c>IF EXISTS</c>.
    /// </summary>
    public void ApplyDropFunction(DropFunctionStatement drop)
    {
        bool removed = _udfs.Unregister(drop.Name);
        if (!removed && !drop.IfExists)
        {
            throw new InvalidOperationException(
                $"UDF '{drop.Name}' is not registered. Use DROP FUNCTION IF EXISTS to make this a no-op.");
        }

        if (removed) _catalogStore?.Save(_udfs, _procedures);
    }

    /// <summary>
    /// Builds the descriptor for a macro UDF (<c>AS expression</c> body). Runs
    /// the inliner against the partially-built registry so references to
    /// undefined UDFs and direct cycles (A → A) surface immediately rather
    /// than at the first call site. Indirect cycles closed by a later
    /// registration are caught at the call site that closes the loop because
    /// the visibility needed to detect them isn't available here.
    /// </summary>
    private UdfDescriptor BuildMacroDescriptor(CreateFunctionStatement create)
    {
        if (create.ExpressionBody is null)
        {
            // Defensive: parser invariant guarantees one of ExpressionBody /
            // StatementBody is non-null. Reachable only via a programmatically
            // constructed CreateFunctionStatement that violates that invariant.
            throw new InvalidOperationException(
                $"CREATE FUNCTION {create.Name}: function body is missing.");
        }

        try
        {
            UdfInliner.Inline(create.ExpressionBody, _udfs);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"CREATE FUNCTION {create.Name}: {ex.Message}", ex);
        }

        return new UdfDescriptor(
            create.Name,
            create.Parameters,
            create.ReturnTypeName,
            create.ExpressionBody,
            create.ReturnIsNotNull);
    }

    /// <summary>
    /// Builds the descriptor for a procedural UDF (<c>BEGIN…END</c> body).
    /// Validates references to other UDFs by running the inliner over every
    /// expression node reachable from the body's statements; the body itself
    /// is opaque to the planner (the executor walks it per call) but the
    /// expressions inside <c>DECLARE</c> initialisers, <c>RETURN</c> values,
    /// and predicates are subject to the same name-resolution rules as a
    /// macro UDF body.
    /// </summary>
    private UdfDescriptor BuildProceduralDescriptor(CreateFunctionStatement create, string? sourceText)
    {
        // Source text fallback: when the descriptor is built from an AST-only
        // path (e.g. a programmatic catalog mutation), synthesise a minimal
        // CREATE FUNCTION header so introspection and persistence still work.
        // Round-tripping a synthesised text through the parser would lose the
        // body's formatting, so we accept that the system_udfs.body column may
        // show a placeholder for those entries until an explicit source text
        // is supplied.
        string text = sourceText ?? $"CREATE FUNCTION {create.Name}";

        try
        {
            ValidateProceduralBodyExpressions(create.StatementBody!);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"CREATE FUNCTION {create.Name}: {ex.Message}", ex);
        }

        return new UdfDescriptor(
            create.Name,
            create.Parameters,
            create.ReturnTypeName,
            ExpressionBody: null,
            create.ReturnIsNotNull,
            StatementBody: create.StatementBody,
            IsPure: create.IsPure,
            SourceText: text);
    }

    /// <summary>
    /// Walks every expression embedded in a procedural body and runs the
    /// inliner over it so unresolved <c>udf.X</c> references and direct
    /// macro cycles surface at registration time. Statement-level shapes are
    /// already validated by the parser's <c>ValidateProceduralBody</c> pass.
    /// </summary>
    private void ValidateProceduralBodyExpressions(IReadOnlyList<Statement> body)
    {
        foreach (Statement stmt in body)
        {
            ValidateExpressionsInStatement(stmt);
        }
    }

    private void ValidateExpressionsInStatement(Statement stmt)
    {
        switch (stmt)
        {
            case ReturnStatement ret:
                UdfInliner.Inline(ret.Value, _udfs);
                break;
            case DeclareStatement decl:
                if (decl.Initializer is not null)
                {
                    UdfInliner.Inline(decl.Initializer, _udfs);
                }
                break;
            case SetStatement set:
                UdfInliner.Inline(set.Value, _udfs);
                break;
            case IfStatement ifStmt:
                UdfInliner.Inline(ifStmt.Predicate, _udfs);
                ValidateExpressionsInStatement(ifStmt.Then);
                if (ifStmt.Else is not null)
                {
                    ValidateExpressionsInStatement(ifStmt.Else);
                }
                break;
            case WhileStatement whileStmt:
                UdfInliner.Inline(whileStmt.Predicate, _udfs);
                ValidateExpressionsInStatement(whileStmt.Body);
                break;
            case BlockStatement block:
                foreach (Statement inner in block.Statements)
                {
                    ValidateExpressionsInStatement(inner);
                }
                break;
        }
    }

    // ───────────────────── Procedures ─────────────────────

    /// <summary>
    /// Applies a <c>CREATE PROCEDURE</c> statement. The procedural body is
    /// validated against the registry (every embedded <c>udf.X</c> call
    /// must resolve) and the verbatim source text is captured so
    /// <c>system_procedures.source_text</c> can show the user's exact
    /// formatting and a round-trip through <see cref="CatalogStore"/>
    /// reparses the same SQL.
    /// </summary>
    public void ApplyCreateProcedure(CreateProcedureStatement create, string? sourceText)
    {
        ValidateDefaultsContiguous(create.Parameters, $"CREATE PROCEDURE {create.Name}");

        try
        {
            ValidateProcedureBody(create.Body);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"CREATE PROCEDURE {create.Name}: {ex.Message}", ex);
        }

        // When the source text isn't available (e.g. registered via the AST-only
        // BatchExecutor path), store a placeholder so the procedure can still run
        // and persist. The display in system_procedures.source_text will show this
        // synthetic text rather than the user's original formatting.
        string text = sourceText ?? $"CREATE PROCEDURE {create.Name}";

        ProcedureDescriptor descriptor = new(
            create.Name,
            create.Parameters,
            create.Body,
            text);

        if (create.IfNotExists && _procedures.TryGet(create.Name, out _))
        {
            return;
        }

        _procedures.Register(descriptor, replace: create.OrReplace);
        _catalogStore?.Save(_udfs, _procedures);
    }

    /// <summary>
    /// Applies a <c>DROP PROCEDURE</c> statement. Throws when the named
    /// procedure isn't registered unless the statement carries <c>IF EXISTS</c>.
    /// </summary>
    public void ApplyDropProcedure(DropProcedureStatement drop)
    {
        bool removed = _procedures.Unregister(drop.Name);
        if (!removed && !drop.IfExists)
        {
            throw new InvalidOperationException(
                $"Procedure '{drop.Name}' is not registered. " +
                "Use DROP PROCEDURE IF EXISTS to make this a no-op.");
        }

        if (removed) _catalogStore?.Save(_udfs, _procedures);
    }

    /// <summary>
    /// Walks every expression in a procedure body's statement tree and
    /// runs the UDF inliner against it, so unresolved <c>udf.X(...)</c>
    /// references surface at <c>CREATE PROCEDURE</c> time rather than at
    /// the first <c>EXEC</c>. Doesn't substitute parameters — those are
    /// resolved at runtime when the procedure is invoked.
    /// </summary>
    private void ValidateProcedureBody(Statement statement)
    {
        switch (statement)
        {
            case BlockStatement block:
                foreach (Statement child in block.Statements) ValidateProcedureBody(child);
                break;
            case IfStatement ifs:
                _ = UdfInliner.Inline(ifs.Predicate, _udfs);
                ValidateProcedureBody(ifs.Then);
                if (ifs.Else is not null) ValidateProcedureBody(ifs.Else);
                break;
            case WhileStatement loop:
                _ = UdfInliner.Inline(loop.Predicate, _udfs);
                ValidateProcedureBody(loop.Body);
                break;
            case ForCounterStatement forC:
                _ = UdfInliner.Inline(forC.Start, _udfs);
                _ = UdfInliner.Inline(forC.End, _udfs);
                if (forC.Step is not null) _ = UdfInliner.Inline(forC.Step, _udfs);
                ValidateProcedureBody(forC.Body);
                break;
            case ForInStatement forIn:
                _ = UdfInliner.Inline(forIn.Source, _udfs);
                ValidateProcedureBody(forIn.Body);
                break;
            case DeclareStatement decl:
                if (decl.Initializer is not null) _ = UdfInliner.Inline(decl.Initializer, _udfs);
                break;
            case SetStatement set:
                _ = UdfInliner.Inline(set.Value, _udfs);
                break;
            case QueryStatement q:
                _ = UdfInliner.Inline(q.Query, _udfs);
                break;
            case ExecStatement exec:
                _ = UdfInliner.Inline(exec.Call, _udfs);
                break;
            case BreakStatement:
            case ContinueStatement:
                // No expressions to validate; legality (must sit inside a
                // loop) is enforced at invocation time by the executor.
                break;
            // Nested routine DDL inside a procedure body is rejected here so
            // the user sees the error at CREATE PROCEDURE rather than at the
            // first EXEC. Nested DML and table DDL are intentionally allowed
            // — procedures should be able to mutate data and shape temp
            // tables.
            case CreateFunctionStatement createFn:
                throw new InvalidOperationException(
                    $"Nested CREATE FUNCTION '{createFn.Name}' is not allowed inside a " +
                    "procedure body. Define UDFs at the top level before the procedure.");
            case CreateProcedureStatement createProc:
                throw new InvalidOperationException(
                    $"Nested CREATE PROCEDURE '{createProc.Name}' is not allowed inside a " +
                    "procedure body.");
            case DropFunctionStatement dropFn:
                throw new InvalidOperationException(
                    $"Nested DROP FUNCTION '{dropFn.Name}' is not allowed inside a procedure body.");
            case DropProcedureStatement dropProc:
                throw new InvalidOperationException(
                    $"Nested DROP PROCEDURE '{dropProc.Name}' is not allowed inside a procedure body.");
            default:
                break;
        }
    }

    // ───────────────────── Shared validation ─────────────────────

    /// <summary>
    /// Enforces that any parameters with <see cref="UdfParameter.Default"/>
    /// values appear contiguously at the tail of the parameter list. Without
    /// this constraint a call site like <c>foo(1, 2)</c> against
    /// <c>foo(@a, @b = 0, @c)</c> would be ambiguous — does the second
    /// argument bind to <c>@b</c> or (with default <c>@b</c>) to <c>@c</c>?
    /// Disallowing the shape removes the ambiguity at registration time.
    /// </summary>
    private static void ValidateDefaultsContiguous(
        IReadOnlyList<UdfParameter> parameters, string contextLabel)
    {
        bool sawDefault = false;
        foreach (UdfParameter p in parameters)
        {
            if (p.Default is not null)
            {
                sawDefault = true;
            }
            else if (sawDefault)
            {
                throw new InvalidOperationException(
                    $"{contextLabel}: parameter '@{p.Name}' has no default but follows a parameter " +
                    "with a default. Defaults must be contiguous at the end of the parameter list.");
            }
        }
    }
}

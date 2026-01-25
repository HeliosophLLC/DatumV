using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Manifest;
using DatumIngest.Model;
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
/// <see cref="TableCatalog.PlanAsync(string)"/> / <see cref="TableCatalog.PlanAsync(Statement)"/>;
/// callers reach this class only through the catalog.
/// </remarks>
internal sealed class RoutineRegistrar
{
    private readonly UdfRegistry _udfs;
    private readonly ProcedureRegistry _procedures;
    private readonly FunctionRegistry _functions;
    private readonly CatalogStore? _catalogStore;

    /// <summary>
    /// Wires the registrar to the registries and (optional) persistent store
    /// it operates on. The instances are held by reference — every mutation
    /// goes through the same UDF / procedure / function registries the
    /// catalog exposes publicly, and every save targets the same file.
    /// </summary>
    /// <param name="udfs">The catalog's UDF registry — descriptors for both macros and procedurals.</param>
    /// <param name="procedures">The catalog's procedure registry.</param>
    /// <param name="functions">
    /// The catalog's scalar-function registry. Procedural UDFs are mirrored
    /// here as <see cref="ProceduralUdfFunction"/> adapters under their
    /// <c>udf.X</c> name so the standard scalar dispatch path can resolve
    /// them at evaluation time without a separate code path. Macros stay out
    /// of this registry — they're inlined before evaluation.
    /// </param>
    /// <param name="catalogStore">Optional persistent catalog file.</param>
    public RoutineRegistrar(
        UdfRegistry udfs,
        ProcedureRegistry procedures,
        FunctionRegistry functions,
        CatalogStore? catalogStore)
    {
        _udfs = udfs;
        _procedures = procedures;
        _functions = functions;
        _catalogStore = catalogStore;
    }

    /// <summary>
    /// Reconciles every procedural UDF loaded from the catalog file. Two
    /// things happen per descriptor:
    /// <list type="bullet">
    ///   <item><description>Macro references inside the body are inlined
    ///   against the now-fully-loaded registry, mirroring what the
    ///   register-time pass does for fresh CREATE FUNCTION calls.</description></item>
    ///   <item><description>A <see cref="ProceduralUdfFunction"/> adapter is
    ///   wired into the scalar-function registry so call sites can dispatch.
    ///   </description></item>
    /// </list>
    /// Order matters: macro UDFs the procedural body depends on must be
    /// loaded first. The catalog file persists entries alphabetically, so
    /// users who name their procedurals after their dependencies (the
    /// existing macro chain rule) get correct ordering for free.
    /// </summary>
    public void SyncProceduralAdaptersFromRegistry()
    {
        // Snapshot before mutating so we don't iterate a collection that
        // we replace entries in.
        UdfDescriptor[] proceduralEntries = _udfs.Entries.Values
            .Where(d => d.IsProcedural)
            .ToArray();

        foreach (UdfDescriptor descriptor in proceduralEntries)
        {
            IReadOnlyList<Statement> rewrittenBody = RewriteBodyWithInlinedMacros(
                descriptor.StatementBody!);
            UdfDescriptor finalDescriptor = descriptor with { StatementBody = rewrittenBody };
            _udfs.Register(finalDescriptor, replace: true);
            RegisterProceduralAdapter(finalDescriptor, replace: true);
        }
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

        if (create.IfNotExists && _udfs.TryGet(create.Name, out _))
        {
            return;
        }

        if (create.StatementBody is not null)
        {
            ApplyCreateProceduralFunction(create, sourceText);
        }
        else
        {
            ApplyCreateMacroFunction(create);
        }

        _catalogStore?.Save(_udfs, _procedures);
    }

    private void ApplyCreateMacroFunction(CreateFunctionStatement create)
    {
        UdfDescriptor descriptor = BuildMacroDescriptor(create);
        _udfs.Register(descriptor, replace: create.OrReplace);
        // OR REPLACE may have swapped a previous procedural for this macro;
        // drop any stale adapter so dispatch falls through to the inliner.
        UnregisterProceduralAdapter(descriptor.Name);
    }

    /// <summary>
    /// Registers a procedural UDF in two phases:
    /// <list type="number">
    ///   <item><description>Insert the descriptor with the body's original AST so
    ///   self-references inside the body resolve once the inliner walks it
    ///   (the inliner now treats procedurals as runtime-dispatched and
    ///   leaves their call sites alone, but it still requires a registered
    ///   descriptor to make that decision).</description></item>
    ///   <item><description>Walk the body, inline every <c>udf.X</c> macro call,
    ///   and re-register with the rewritten body. The runtime adapter
    ///   walks <see cref="UdfDescriptor.StatementBody"/> as-is — no
    ///   plan-time inlining at call sites — so macro substitution must
    ///   happen here.</description></item>
    /// </list>
    /// On failure (typically an undefined <c>udf.Y</c> reference) the partial
    /// registration is rolled back so the catalog can't observe the broken
    /// intermediate state.
    /// </summary>
    private void ApplyCreateProceduralFunction(CreateFunctionStatement create, string? sourceText)
    {
        UdfDescriptor initial = BuildProceduralDescriptor(create, sourceText);

        _udfs.Register(initial, replace: create.OrReplace);

        UdfDescriptor finalDescriptor;
        // Procedural UDFs allow forward references — `a` can call `udf.b`
        // even before `b` is registered, since the call dispatches at
        // runtime through the FunctionRegistry adapter. To make the inliner
        // treat unresolved names as "leave alone" (rather than throwing
        // "not registered"), pre-register a stub procedural descriptor for
        // each missing referenced name. The stubs satisfy the inliner's
        // lookup; we remove them immediately after the rewrite so the
        // catalog only commits to the final state.
        List<string> stubNames = RegisterStubsForUnresolvedReferences(create.StatementBody!, create.Name);
        try
        {
            IReadOnlyList<Statement> rewrittenBody = RewriteBodyWithInlinedMacros(
                create.StatementBody!);
            finalDescriptor = initial with { StatementBody = rewrittenBody };
            _udfs.Register(finalDescriptor, replace: true);
        }
        catch (Exception)
        {
            // Roll the partial registration back so a failed rewrite leaves
            // the catalog in the same state the caller observed before
            // CREATE FUNCTION started.
            _udfs.Unregister(create.Name);
            UnregisterProceduralAdapter(create.Name);
            throw;
        }
        finally
        {
            foreach (string stubName in stubNames)
            {
                _udfs.Unregister(stubName);
            }
        }

        // Mirror the (final) descriptor into the scalar registry so the
        // standard scalar dispatch path can resolve udf.X at evaluation time.
        RegisterProceduralAdapter(finalDescriptor, replace: create.OrReplace);
    }

    /// <summary>
    /// Pre-scans <paramref name="body"/> for <c>udf.X</c> calls whose target
    /// isn't yet in the registry and inserts a placeholder procedural
    /// descriptor for each missing name. This makes the subsequent
    /// <see cref="UdfInliner"/> pass treat the call as "leave alone" instead
    /// of throwing — which lets a procedural UDF reference another that
    /// will be defined later (or itself, transitively). Returns the names
    /// of every stub registered so the caller can unregister them once the
    /// rewrite is done.
    /// </summary>
    /// <param name="body">The procedural body whose expressions are about to be rewritten.</param>
    /// <param name="selfName">The UDF currently being defined; the stub for self is unnecessary because the caller has already registered the real descriptor.</param>
    private List<string> RegisterStubsForUnresolvedReferences(IReadOnlyList<Statement> body, string selfName)
    {
        HashSet<string> referencedNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (Statement stmt in body)
        {
            CollectUdfReferences(stmt, referencedNames);
        }

        List<string> stubsRegistered = new();
        foreach (string name in referencedNames)
        {
            if (string.Equals(name, selfName, StringComparison.OrdinalIgnoreCase)) continue;
            if (_udfs.TryGet(name, out _)) continue;

            UdfDescriptor stub = new(
                name,
                Parameters: Array.Empty<UdfParameter>(),
                ReturnTypeName: null,
                ExpressionBody: null,
                ReturnIsNotNull: false,
                StatementBody: Array.Empty<Statement>(),
                IsPure: false,
                SourceText: null);
            try
            {
                _udfs.Register(stub, replace: false);
                stubsRegistered.Add(name);
            }
            catch (InvalidOperationException)
            {
                // A concurrent registration may have raced us — that's
                // fine, the registry already has a real descriptor we can
                // dispatch through.
            }
        }
        return stubsRegistered;
    }

    private static void CollectUdfReferences(Statement stmt, HashSet<string> referencedNames)
    {
        switch (stmt)
        {
            case ReturnStatement ret:
                CollectUdfReferencesInExpression(ret.Value, referencedNames);
                break;
            case DeclareStatement decl:
                if (decl.Initializer is not null)
                {
                    CollectUdfReferencesInExpression(decl.Initializer, referencedNames);
                }
                break;
            case SetStatement set:
                CollectUdfReferencesInExpression(set.Value, referencedNames);
                break;
            case IfStatement ifStmt:
                CollectUdfReferencesInExpression(ifStmt.Predicate, referencedNames);
                CollectUdfReferences(ifStmt.Then, referencedNames);
                if (ifStmt.Else is not null) CollectUdfReferences(ifStmt.Else, referencedNames);
                break;
            case WhileStatement whileStmt:
                CollectUdfReferencesInExpression(whileStmt.Predicate, referencedNames);
                CollectUdfReferences(whileStmt.Body, referencedNames);
                break;
            case BlockStatement block:
                foreach (Statement inner in block.Statements)
                {
                    CollectUdfReferences(inner, referencedNames);
                }
                break;
        }
    }

    private static void CollectUdfReferencesInExpression(Expression expression, HashSet<string> referencedNames)
    {
        switch (expression)
        {
            case FunctionCallExpression fn:
                if (fn.FunctionName.StartsWith(UdfInliner.UdfNamespacePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    referencedNames.Add(fn.FunctionName[UdfInliner.UdfNamespacePrefix.Length..]);
                }
                foreach (Expression arg in fn.Arguments)
                {
                    CollectUdfReferencesInExpression(arg, referencedNames);
                }
                break;
            case BinaryExpression b:
                CollectUdfReferencesInExpression(b.Left, referencedNames);
                CollectUdfReferencesInExpression(b.Right, referencedNames);
                break;
            case UnaryExpression u:
                CollectUdfReferencesInExpression(u.Operand, referencedNames);
                break;
            case CastExpression c:
                CollectUdfReferencesInExpression(c.Expression, referencedNames);
                break;
            case CaseExpression ce:
                if (ce.Operand is not null) CollectUdfReferencesInExpression(ce.Operand, referencedNames);
                foreach (WhenClause w in ce.WhenClauses)
                {
                    CollectUdfReferencesInExpression(w.Condition, referencedNames);
                    CollectUdfReferencesInExpression(w.Result, referencedNames);
                }
                if (ce.ElseResult is not null) CollectUdfReferencesInExpression(ce.ElseResult, referencedNames);
                break;
            case InExpression ie:
                CollectUdfReferencesInExpression(ie.Expression, referencedNames);
                foreach (Expression v in ie.Values) CollectUdfReferencesInExpression(v, referencedNames);
                break;
            case BetweenExpression be:
                CollectUdfReferencesInExpression(be.Expression, referencedNames);
                CollectUdfReferencesInExpression(be.Low, referencedNames);
                CollectUdfReferencesInExpression(be.High, referencedNames);
                break;
            case IsNullExpression isn:
                CollectUdfReferencesInExpression(isn.Expression, referencedNames);
                break;
            case LikeExpression lk:
                CollectUdfReferencesInExpression(lk.Expression, referencedNames);
                CollectUdfReferencesInExpression(lk.Pattern, referencedNames);
                CollectUdfReferencesInExpression(lk.EscapeCharacter, referencedNames);
                break;
            case AtTimeZoneExpression atz:
                CollectUdfReferencesInExpression(atz.Expression, referencedNames);
                CollectUdfReferencesInExpression(atz.TimeZone, referencedNames);
                break;
            case StructLiteralExpression sl:
                foreach (StructField f in sl.Fields)
                {
                    CollectUdfReferencesInExpression(f.Value, referencedNames);
                }
                break;
            case IndexAccessExpression ix:
                CollectUdfReferencesInExpression(ix.Source, referencedNames);
                CollectUdfReferencesInExpression(ix.Index, referencedNames);
                break;
            // Leaves: column references, literals, parameters, variables.
        }
    }

    /// <summary>
    /// Walks every expression embedded in the procedural body and inlines
    /// any <c>udf.X</c> macro references it finds. References to procedural
    /// UDFs (including self-references) are left intact — the runtime adapter
    /// dispatches them via the registered <see cref="ProceduralUdfFunction"/>.
    /// Returns a new statement list with the rewritten expressions; the
    /// original AST is untouched.
    /// </summary>
    private IReadOnlyList<Statement> RewriteBodyWithInlinedMacros(IReadOnlyList<Statement> body)
    {
        Statement[] rewritten = new Statement[body.Count];
        for (int i = 0; i < body.Count; i++)
        {
            rewritten[i] = RewriteStatement(body[i]);
        }
        return rewritten;
    }

    private Statement RewriteStatement(Statement stmt) => stmt switch
    {
        ReturnStatement ret => new ReturnStatement(UdfInliner.Inline(ret.Value, _udfs), ret.Span),
        DeclareStatement decl => decl.Initializer is null
            ? decl
            : new DeclareStatement(decl.VariableName, decl.TypeName, UdfInliner.Inline(decl.Initializer, _udfs), decl.Span),
        SetStatement set => new SetStatement(set.VariableName, UdfInliner.Inline(set.Value, _udfs), set.Span),
        IfStatement ifStmt => new IfStatement(
            UdfInliner.Inline(ifStmt.Predicate, _udfs),
            RewriteStatement(ifStmt.Then),
            ifStmt.Else is null ? null : RewriteStatement(ifStmt.Else),
            ifStmt.Span),
        WhileStatement whileStmt => new WhileStatement(
            UdfInliner.Inline(whileStmt.Predicate, _udfs),
            RewriteStatement(whileStmt.Body),
            whileStmt.Span),
        BlockStatement block => new BlockStatement(
            RewriteBodyWithInlinedMacros(block.Statements),
            block.Span),
        // Other procedural shapes (BREAK/CONTINUE) carry no expressions to
        // rewrite and pass through unchanged.
        _ => stmt,
    };

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

        if (removed)
        {
            // Drop the procedural adapter too, if one was registered. The
            // call is idempotent for macro UDFs (no adapter ever existed) so
            // we don't need to gate it on IsProcedural.
            UnregisterProceduralAdapter(drop.Name);
            _catalogStore?.Save(_udfs, _procedures);
        }
    }

    /// <summary>
    /// Registers (or replaces) the <see cref="ProceduralUdfFunction"/>
    /// adapter for <paramref name="descriptor"/> in the scalar function
    /// registry under its <c>udf.NAME</c> key. Replace semantics mirror the
    /// underlying <c>OR REPLACE</c> on the UDF registry.
    /// </summary>
    private void RegisterProceduralAdapter(UdfDescriptor descriptor, bool replace)
    {
        ProceduralUdfFunction adapter = new(descriptor, _functions);
        FunctionDescriptor catalogDescriptor = BuildProceduralDescriptor(descriptor);
        _functions.RegisterScalarInstance(
            UdfNameForFunctionRegistry(descriptor.Name),
            adapter,
            descriptor: catalogDescriptor,
            replace: replace);
    }

    /// <summary>
    /// Synthesises a <see cref="FunctionDescriptor"/> for a procedural UDF so the
    /// type resolver can read its return shape (including <c>Array&lt;T&gt;</c>
    /// returns) via the standard per-signature path. Parameters use
    /// <see cref="DataKindMatcher.Any"/> because the UDF adapter does its own
    /// arity check — the synthesised signature exists to carry the return rule,
    /// not to gate calls.
    /// </summary>
    private static FunctionDescriptor BuildProceduralDescriptor(UdfDescriptor udf)
    {
        DataKind returnKind = DataKind.String;
        bool returnIsArray = false;
        if (udf.ReturnTypeName is not null)
        {
            TypeAnnotationResolver.TryParse(udf.ReturnTypeName, out returnKind, out returnIsArray);
        }

        ReturnTypeRule returnRule = returnIsArray
            ? ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(returnKind))
            : ReturnTypeRule.Constant(returnKind);

        ParameterSpec[] parameters = new ParameterSpec[udf.Parameters.Count];
        for (int i = 0; i < udf.Parameters.Count; i++)
        {
            UdfParameter p = udf.Parameters[i];
            parameters[i] = new ParameterSpec(p.Name, DataKindMatcher.Any, IsOptional: p.Default is not null);
        }

        return new FunctionDescriptor(
            PrimaryName: UdfNameForFunctionRegistry(udf.Name),
            Aliases: Array.Empty<string>(),
            Category: FunctionCategory.Utility,
            Description: $"User-defined procedural function {udf.Name}.",
            Signatures:
            [
                new FunctionSignatureVariant(parameters, VariadicTrailing: null, ReturnType: returnRule),
            ]);
    }

    /// <summary>
    /// Drops the procedural adapter for <paramref name="udfName"/> from the
    /// scalar registry. Idempotent — returns silently when no adapter is
    /// registered (the common case for macro UDFs and DROP IF EXISTS misses).
    /// </summary>
    private void UnregisterProceduralAdapter(string udfName)
    {
        _functions.UnregisterScalar(UdfNameForFunctionRegistry(udfName));
    }

    /// <summary>
    /// Builds the <c>udf.NAME</c> key the scalar registry stores procedural
    /// adapters under. Centralised so any future tweak (e.g. case
    /// canonicalisation) lives in one place.
    /// </summary>
    private static string UdfNameForFunctionRegistry(string udfName)
        => UdfInliner.UdfNamespacePrefix + udfName;

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
    /// The descriptor's <see cref="UdfDescriptor.StatementBody"/> carries the
    /// body's original AST; the macro-inlining + reference-validation pass
    /// runs after this descriptor lands in the registry (see
    /// <see cref="ApplyCreateProceduralFunction"/>) so self-references can
    /// resolve to the UDF being defined without bootstrap headaches.
    /// </summary>
    private static UdfDescriptor BuildProceduralDescriptor(CreateFunctionStatement create, string? sourceText)
    {
        // Source text fallback: when the descriptor is built from an AST-only
        // path (e.g. a programmatic catalog mutation), synthesise a minimal
        // CREATE FUNCTION header so introspection and persistence still work.
        // Round-tripping a synthesised text through the parser would lose the
        // body's formatting, so we accept that the system_udfs.body column may
        // show a placeholder for those entries until an explicit source text
        // is supplied.
        string text = sourceText ?? $"CREATE FUNCTION {create.Name}";

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

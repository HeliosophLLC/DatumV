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
    private readonly TableCatalog _catalog;
    private readonly UdfRegistry _udfs;
    private readonly ProcedureRegistry _procedures;
    private readonly FunctionRegistry _functions;
    private readonly CatalogStore? _catalogStore;

    /// <summary>
    /// Wires the registrar to the catalog, registries, and (optional)
    /// persistent store it operates on. The instances are held by reference —
    /// every mutation goes through the same UDF / procedure / function
    /// registries the catalog exposes publicly, and every save targets the
    /// same file. The catalog reference exists so the registrar can build
    /// per-call <see cref="SchemaResolver"/> instances against the current
    /// session search_path.
    /// </summary>
    public RoutineRegistrar(
        TableCatalog catalog,
        UdfRegistry udfs,
        ProcedureRegistry procedures,
        FunctionRegistry functions,
        CatalogStore? catalogStore)
    {
        _catalog = catalog;
        _udfs = udfs;
        _procedures = procedures;
        _functions = functions;
        _catalogStore = catalogStore;
    }

    private SchemaResolver Resolver() => new(_catalog, _catalog.SearchPath);

    /// <summary>
    /// S7c: catalog search_path extended with the legacy <c>udf</c> and
    /// <c>proc</c> sentinels so unqualified CREATE FUNCTION / CREATE
    /// PROCEDURE (which still default to those sentinels) and unqualified
    /// DROP / CALL referencing them stay findable. S7d drops the
    /// fallback when the defaults move to a real schema.
    /// </summary>
    private IReadOnlyList<string> RoutineSearchPath()
    {
        IReadOnlyList<string> sessionPath = _catalog.SearchPath;
        bool hasUdf = false, hasProc = false;
        foreach (string s in sessionPath)
        {
            if (string.Equals(s, "udf", StringComparison.OrdinalIgnoreCase)) hasUdf = true;
            if (string.Equals(s, "proc", StringComparison.OrdinalIgnoreCase)) hasProc = true;
        }
        if (hasUdf && hasProc) return sessionPath;

        List<string> extended = new(sessionPath.Count + 2);
        extended.AddRange(sessionPath);
        if (!hasUdf) extended.Add("udf");
        if (!hasProc) extended.Add("proc");
        return extended;
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
        UdfDescriptor[] proceduralEntries = _udfs.Entries
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

        // Pick the target schema. Explicit qualification wins; unqualified
        // CREATE FUNCTION falls back to the legacy <c>udf</c> sentinel so
        // existing call sites that write <c>udf.foo(...)</c> still resolve
        // through the post-S7c qualified registry. S7d switches the default
        // to the first DDL-capable schema on search_path (typically
        // <c>public</c>) and migrates the call syntax accordingly.
        QualifiedName qn = new(create.SchemaName ?? "udf", create.Name);

        if (create.IfNotExists && _udfs.TryGet(qn, out _))
        {
            return;
        }

        if (create.StatementBody is not null)
        {
            ApplyCreateProceduralFunction(create, qn, sourceText);
        }
        else
        {
            ApplyCreateMacroFunction(create, qn);
        }

        _catalogStore?.Save(_udfs, _procedures);
    }

    private void ApplyCreateMacroFunction(CreateFunctionStatement create, QualifiedName qn)
    {
        UdfDescriptor descriptor = BuildMacroDescriptor(create, qn);
        _udfs.Register(descriptor, replace: create.OrReplace);
        // OR REPLACE may have swapped a previous procedural for this macro;
        // drop any stale adapter so dispatch falls through to the inliner.
        UnregisterProceduralAdapter(qn);
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
    private void ApplyCreateProceduralFunction(
        CreateFunctionStatement create, QualifiedName qn, string? sourceText)
    {
        UdfDescriptor initial = BuildProceduralDescriptor(create, qn, sourceText);

        _udfs.Register(initial, replace: create.OrReplace);

        UdfDescriptor finalDescriptor;
        // Procedural UDFs allow forward references — `a` can call `b`
        // even before `b` is registered, since the call dispatches at
        // runtime through the FunctionRegistry adapter. To make the inliner
        // treat unresolved names as "leave alone" (rather than throwing
        // "not registered"), pre-register a stub procedural descriptor for
        // each missing referenced name. Stubs land in the same schema as
        // the containing function — that's the natural assumption when a
        // body of one UDF references another.
        List<QualifiedName> stubNames = RegisterStubsForUnresolvedReferences(create.StatementBody!, qn);
        try
        {
            IReadOnlyList<Statement> rewrittenBody = RewriteBodyWithInlinedMacros(
                create.StatementBody!, qn.Schema);
            finalDescriptor = initial with { StatementBody = rewrittenBody };
            _udfs.Register(finalDescriptor, replace: true);
        }
        catch (Exception)
        {
            // Roll the partial registration back so a failed rewrite leaves
            // the catalog in the same state the caller observed before
            // CREATE FUNCTION started.
            _udfs.Unregister(qn);
            UnregisterProceduralAdapter(qn);
            throw;
        }
        finally
        {
            foreach (QualifiedName stubName in stubNames)
            {
                _udfs.Unregister(stubName);
            }
        }

        // Mirror the (final) descriptor into the scalar registry so the
        // standard scalar dispatch path can resolve the UDF at evaluation
        // time. The adapter lands at the function's real qualified name.
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
    /// <param name="self">The UDF currently being defined; the stub for self is unnecessary because the caller has already registered the real descriptor.</param>
    private List<QualifiedName> RegisterStubsForUnresolvedReferences(
        IReadOnlyList<Statement> body, QualifiedName self)
    {
        // Collect referenced UDF names from the body. We capture both
        // qualified (SchemaName != null) and unqualified call shapes.
        // Unqualified references stub into the containing function's
        // schema — i.e. <c>foo()</c> inside <c>myapp.parent()</c> stubs
        // as <c>(myapp, foo)</c>.
        HashSet<QualifiedName> referencedNames = new();
        foreach (Statement stmt in body)
        {
            CollectUdfReferences(stmt, referencedNames, defaultSchema: self.Schema);
        }

        List<QualifiedName> stubsRegistered = new();
        foreach (QualifiedName name in referencedNames)
        {
            if (name == self) continue;
            if (_udfs.TryGet(name, out _)) continue;

            UdfDescriptor stub = new(
                SchemaName: name.Schema,
                Name: name.Name,
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

    private void CollectUdfReferences(
        Statement stmt, HashSet<QualifiedName> referencedNames, string defaultSchema)
    {
        switch (stmt)
        {
            case ReturnStatement ret:
                CollectUdfReferencesInExpression(ret.Value, referencedNames, defaultSchema);
                break;
            case DeclareStatement decl:
                if (decl.Initializer is not null)
                {
                    CollectUdfReferencesInExpression(decl.Initializer, referencedNames, defaultSchema);
                }
                break;
            case SetStatement set:
                CollectUdfReferencesInExpression(set.Value, referencedNames, defaultSchema);
                break;
            case IfStatement ifStmt:
                CollectUdfReferencesInExpression(ifStmt.Predicate, referencedNames, defaultSchema);
                CollectUdfReferences(ifStmt.Then, referencedNames, defaultSchema);
                if (ifStmt.Else is not null) CollectUdfReferences(ifStmt.Else, referencedNames, defaultSchema);
                break;
            case WhileStatement whileStmt:
                CollectUdfReferencesInExpression(whileStmt.Predicate, referencedNames, defaultSchema);
                CollectUdfReferences(whileStmt.Body, referencedNames, defaultSchema);
                break;
            case BlockStatement block:
                foreach (Statement inner in block.Statements)
                {
                    CollectUdfReferences(inner, referencedNames, defaultSchema);
                }
                break;
        }
    }

    private void CollectUdfReferencesInExpression(
        Expression expression, HashSet<QualifiedName> referencedNames, string defaultSchema)
    {
        switch (expression)
        {
            case FunctionCallExpression fn:
                // Match any call that resolves through the UDF registry —
                // qualified or unqualified. The stub mechanism only cares
                // about names that aren't already registered as built-ins
                // (those don't need stubs). Capture the call's resolved
                // qualified name so the stub lands where the inliner will
                // look for it.
                if (fn.SchemaName is not null)
                {
                    // Explicit schema. The user wrote `myapp.x()` — stub
                    // exactly there if it doesn't exist.
                    referencedNames.Add(new QualifiedName(fn.SchemaName, fn.FunctionName));
                }
                else
                {
                    // Unqualified. Walk search_path to find the actual
                    // registration if one exists; otherwise stub into the
                    // containing function's schema (best-effort).
                    if (_udfs.TryResolve(null, fn.FunctionName, _catalog.SearchPath, out UdfDescriptor? resolved))
                    {
                        referencedNames.Add(resolved.QualifiedName);
                    }
                    else
                    {
                        referencedNames.Add(new QualifiedName(defaultSchema, fn.FunctionName));
                    }
                }
                foreach (Expression arg in fn.Arguments)
                {
                    CollectUdfReferencesInExpression(arg, referencedNames, defaultSchema);
                }
                break;
            case BinaryExpression b:
                CollectUdfReferencesInExpression(b.Left, referencedNames, defaultSchema);
                CollectUdfReferencesInExpression(b.Right, referencedNames, defaultSchema);
                break;
            case UnaryExpression u:
                CollectUdfReferencesInExpression(u.Operand, referencedNames, defaultSchema);
                break;
            case CastExpression c:
                CollectUdfReferencesInExpression(c.Expression, referencedNames, defaultSchema);
                break;
            case CaseExpression ce:
                if (ce.Operand is not null) CollectUdfReferencesInExpression(ce.Operand, referencedNames, defaultSchema);
                foreach (WhenClause w in ce.WhenClauses)
                {
                    CollectUdfReferencesInExpression(w.Condition, referencedNames, defaultSchema);
                    CollectUdfReferencesInExpression(w.Result, referencedNames, defaultSchema);
                }
                if (ce.ElseResult is not null) CollectUdfReferencesInExpression(ce.ElseResult, referencedNames, defaultSchema);
                break;
            case InExpression ie:
                CollectUdfReferencesInExpression(ie.Expression, referencedNames, defaultSchema);
                foreach (Expression v in ie.Values) CollectUdfReferencesInExpression(v, referencedNames, defaultSchema);
                break;
            case BetweenExpression be:
                CollectUdfReferencesInExpression(be.Expression, referencedNames, defaultSchema);
                CollectUdfReferencesInExpression(be.Low, referencedNames, defaultSchema);
                CollectUdfReferencesInExpression(be.High, referencedNames, defaultSchema);
                break;
            case IsNullExpression isn:
                CollectUdfReferencesInExpression(isn.Expression, referencedNames, defaultSchema);
                break;
            case LikeExpression lk:
                CollectUdfReferencesInExpression(lk.Expression, referencedNames, defaultSchema);
                CollectUdfReferencesInExpression(lk.Pattern, referencedNames, defaultSchema);
                CollectUdfReferencesInExpression(lk.EscapeCharacter, referencedNames, defaultSchema);
                break;
            case AtTimeZoneExpression atz:
                CollectUdfReferencesInExpression(atz.Expression, referencedNames, defaultSchema);
                CollectUdfReferencesInExpression(atz.TimeZone, referencedNames, defaultSchema);
                break;
            case StructLiteralExpression sl:
                foreach (StructField f in sl.Fields)
                {
                    CollectUdfReferencesInExpression(f.Value, referencedNames, defaultSchema);
                }
                break;
            case IndexAccessExpression ix:
                CollectUdfReferencesInExpression(ix.Source, referencedNames, defaultSchema);
                CollectUdfReferencesInExpression(ix.Index, referencedNames, defaultSchema);
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
    private IReadOnlyList<Statement> RewriteBodyWithInlinedMacros(
        IReadOnlyList<Statement> body, string defaultSchema)
    {
        // The 2-arg overload kept for SyncProceduralAdaptersFromRegistry — the
        // schema parameter isn't used inside the inliner today (search_path
        // is captured at the registrar level), but we keep it for symmetry
        // with the registration-time signature.
        _ = defaultSchema;
        Statement[] rewritten = new Statement[body.Count];
        for (int i = 0; i < body.Count; i++)
        {
            rewritten[i] = RewriteStatement(body[i]);
        }
        return rewritten;
    }

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
        ReturnStatement ret => new ReturnStatement(UdfInliner.Inline(ret.Value, _udfs, _catalog.SearchPath), ret.Span),
        DeclareStatement decl => decl.Initializer is null
            ? decl
            : new DeclareStatement(decl.VariableName, decl.TypeName, UdfInliner.Inline(decl.Initializer, _udfs, _catalog.SearchPath), decl.Span),
        SetStatement set => new SetStatement(set.VariableName, UdfInliner.Inline(set.Value, _udfs, _catalog.SearchPath), set.Span),
        IfStatement ifStmt => new IfStatement(
            UdfInliner.Inline(ifStmt.Predicate, _udfs, _catalog.SearchPath),
            RewriteStatement(ifStmt.Then),
            ifStmt.Else is null ? null : RewriteStatement(ifStmt.Else),
            ifStmt.Span),
        WhileStatement whileStmt => new WhileStatement(
            UdfInliner.Inline(whileStmt.Predicate, _udfs, _catalog.SearchPath),
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
        // Resolve through the registry: explicit schema = exact match;
        // unqualified = walk search_path. The legacy "udf" sentinel is
        // appended so functions created with the unqualified default
        // (which still lands at "udf" in S7c) are findable. S7d strips
        // this fallback.
        if (!_udfs.TryResolve(drop.SchemaName, drop.Name, RoutineSearchPath(), out UdfDescriptor? udf))
        {
            if (drop.IfExists) return;
            string label = drop.SchemaName is null ? drop.Name : $"{drop.SchemaName}.{drop.Name}";
            throw new InvalidOperationException(
                $"UDF '{label}' is not registered. Use DROP FUNCTION IF EXISTS to make this a no-op.");
        }

        _udfs.Unregister(udf.QualifiedName);
        // Drop the procedural adapter too, if one was registered. The
        // call is idempotent for macro UDFs (no adapter ever existed) so
        // we don't need to gate it on IsProcedural.
        UnregisterProceduralAdapter(udf.QualifiedName);
        _catalogStore?.Save(_udfs, _procedures);
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
        // Register at the UDF's real qualified name so the scalar
        // dispatcher finds it via schema lookup, not the legacy "udf."
        // prefix. RegisterScalarInstance parses dotted names back into
        // a QualifiedName key.
        _functions.RegisterScalarInstance(
            descriptor.QualifiedName.ToString(),
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
            PrimaryName: udf.Name,
            Aliases: Array.Empty<string>(),
            Category: FunctionCategory.Utility,
            Description: $"User-defined procedural function {udf.QualifiedName}.",
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
    private void UnregisterProceduralAdapter(QualifiedName udfName)
    {
        _functions.UnregisterScalar(udfName.ToString());
    }

    /// <summary>
    /// Builds the descriptor for a macro UDF (<c>AS expression</c> body). Runs
    /// the inliner against the partially-built registry so references to
    /// undefined UDFs and direct cycles (A → A) surface immediately rather
    /// than at the first call site. Indirect cycles closed by a later
    /// registration are caught at the call site that closes the loop because
    /// the visibility needed to detect them isn't available here.
    /// </summary>
    private UdfDescriptor BuildMacroDescriptor(CreateFunctionStatement create, QualifiedName qn)
    {
        if (create.ExpressionBody is null)
        {
            // Defensive: parser invariant guarantees one of ExpressionBody /
            // StatementBody is non-null. Reachable only via a programmatically
            // constructed CreateFunctionStatement that violates that invariant.
            throw new InvalidOperationException(
                $"CREATE FUNCTION {qn}: function body is missing.");
        }

        try
        {
            UdfInliner.Inline(create.ExpressionBody, _udfs, _catalog.SearchPath);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"CREATE FUNCTION {qn}: {ex.Message}", ex);
        }

        return new UdfDescriptor(
            SchemaName: qn.Schema,
            Name: qn.Name,
            Parameters: create.Parameters,
            ReturnTypeName: create.ReturnTypeName,
            ExpressionBody: create.ExpressionBody,
            ReturnIsNotNull: create.ReturnIsNotNull);
    }

    /// <summary>
    /// Builds the descriptor for a procedural UDF (<c>BEGIN…END</c> body).
    /// The descriptor's <see cref="UdfDescriptor.StatementBody"/> carries the
    /// body's original AST; the macro-inlining + reference-validation pass
    /// runs after this descriptor lands in the registry (see
    /// <see cref="ApplyCreateProceduralFunction"/>) so self-references can
    /// resolve to the UDF being defined without bootstrap headaches.
    /// </summary>
    private static UdfDescriptor BuildProceduralDescriptor(
        CreateFunctionStatement create, QualifiedName qn, string? sourceText)
    {
        string text = sourceText ?? $"CREATE FUNCTION {qn}";

        return new UdfDescriptor(
            SchemaName: qn.Schema,
            Name: qn.Name,
            Parameters: create.Parameters,
            ReturnTypeName: create.ReturnTypeName,
            ExpressionBody: null,
            ReturnIsNotNull: create.ReturnIsNotNull,
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

        // Same legacy-default rule as ApplyCreateFunction — unqualified
        // CREATE PROCEDURE lands in the legacy <c>proc</c> sentinel
        // schema; S7d will switch the default to first DDL-capable on
        // search_path.
        QualifiedName qn = new(create.SchemaName ?? "proc", create.Name);

        try
        {
            ValidateProcedureBody(create.Body);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"CREATE PROCEDURE {qn}: {ex.Message}", ex);
        }

        // When the source text isn't available (e.g. registered via the AST-only
        // BatchExecutor path), store a placeholder so the procedure can still run
        // and persist. The display in system_procedures.source_text will show this
        // synthetic text rather than the user's original formatting.
        string text = sourceText ?? $"CREATE PROCEDURE {qn}";

        ProcedureDescriptor descriptor = new(
            SchemaName: qn.Schema,
            Name: qn.Name,
            Parameters: create.Parameters,
            Body: create.Body,
            SourceText: text);

        if (create.IfNotExists && _procedures.TryGet(qn, out _))
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
        if (!_procedures.TryResolve(drop.SchemaName, drop.Name, RoutineSearchPath(), out ProcedureDescriptor? proc))
        {
            if (drop.IfExists) return;
            string label = drop.SchemaName is null ? drop.Name : $"{drop.SchemaName}.{drop.Name}";
            throw new InvalidOperationException(
                $"Procedure '{label}' is not registered. " +
                "Use DROP PROCEDURE IF EXISTS to make this a no-op.");
        }

        _procedures.Unregister(proc.QualifiedName);
        _catalogStore?.Save(_udfs, _procedures);
    }

    /// <summary>
    /// Walks every expression in a procedure body's statement tree and
    /// runs the UDF inliner against it, so unresolved <c>udf.X(...)</c>
    /// references surface at <c>CREATE PROCEDURE</c> time rather than at
    /// the first <c>CALL</c>. Doesn't substitute parameters — those are
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
                _ = UdfInliner.Inline(ifs.Predicate, _udfs, _catalog.SearchPath);
                ValidateProcedureBody(ifs.Then);
                if (ifs.Else is not null) ValidateProcedureBody(ifs.Else);
                break;
            case WhileStatement loop:
                _ = UdfInliner.Inline(loop.Predicate, _udfs, _catalog.SearchPath);
                ValidateProcedureBody(loop.Body);
                break;
            case ForCounterStatement forC:
                _ = UdfInliner.Inline(forC.Start, _udfs, _catalog.SearchPath);
                _ = UdfInliner.Inline(forC.End, _udfs, _catalog.SearchPath);
                if (forC.Step is not null) _ = UdfInliner.Inline(forC.Step, _udfs, _catalog.SearchPath);
                ValidateProcedureBody(forC.Body);
                break;
            case ForInStatement forIn:
                _ = UdfInliner.Inline(forIn.Source, _udfs, _catalog.SearchPath);
                ValidateProcedureBody(forIn.Body);
                break;
            case DeclareStatement decl:
                if (decl.Initializer is not null) _ = UdfInliner.Inline(decl.Initializer, _udfs, _catalog.SearchPath);
                break;
            case SetStatement set:
                _ = UdfInliner.Inline(set.Value, _udfs, _catalog.SearchPath);
                break;
            case QueryStatement q:
                _ = UdfInliner.Inline(q.Query, _udfs, _catalog.SearchPath);
                break;
            case CallStatement call:
                _ = UdfInliner.Inline(call.Call, _udfs, _catalog.SearchPath);
                break;
            case BreakStatement:
            case ContinueStatement:
                // No expressions to validate; legality (must sit inside a
                // loop) is enforced at invocation time by the executor.
                break;
            // Nested routine DDL inside a procedure body is rejected here so
            // the user sees the error at CREATE PROCEDURE rather than at the
            // first CALL. Nested DML and table DDL are intentionally allowed
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

using DatumIngest.Catalog.Registries;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Models;
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

        // Resolve the target schema via the same DDL-capable schema rules
        // that govern CREATE TABLE: explicit schema goes there if it's
        // mounted and writable; unqualified picks the first DDL-capable
        // schema on the session search_path (typically <c>public</c>).
        QualifiedName qn = Resolver().ResolveForCreate(create.SchemaName, create.Name);

        if (create.IfNotExists && _udfs.TryGet(qn, out _))
        {
            return;
        }

        // Snapshot pre-state at the resolved key so the event fire at the
        // bottom can pick Created vs Altered. Captured after IfNotExists
        // exits so it never observes a stale `before` for a no-op path.
        _udfs.TryGet(qn, out UdfDescriptor? before);

        if (create.StatementBody is not null)
        {
            ApplyCreateProceduralFunction(create, qn, sourceText);
        }
        else
        {
            ApplyCreateMacroFunction(create, qn);
        }

        _catalogStore?.Save(_udfs, _procedures, _catalog.DeclaredModels);

        if (_udfs.TryGet(qn, out UdfDescriptor? after))
        {
            if (before is null)
            {
                _catalog.Events.Raise(new FunctionCreatedEvent(qn, after, sourceText));
            }
            else
            {
                _catalog.Events.Raise(new FunctionAlteredEvent(qn, before, after, sourceText));
            }
        }
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
                foreach (Expression i in ix.Indices)
                    CollectUdfReferencesInExpression(i, referencedNames, defaultSchema);
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
    public void ApplyDropFunction(DropFunctionStatement drop, string? sourceText = null)
    {
        // Resolve through the registry: explicit schema = exact match;
        // unqualified = walk search_path. The legacy "udf" sentinel is
        // appended so functions created with the unqualified default
        // (which still lands at "udf" in S7c) are findable. S7d strips
        // this fallback.
        if (!_udfs.TryResolve(drop.SchemaName, drop.Name, _catalog.SearchPath, out UdfDescriptor? udf))
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
        _catalogStore?.Save(_udfs, _procedures, _catalog.DeclaredModels);

        _catalog.Events.Raise(new FunctionDroppedEvent(udf.QualifiedName, udf, sourceText));
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
            parameters[i] = new ParameterSpec(
                p.Name,
                DataKindMatcher.Any,
                IsOptional: p.Default is not null,
                Metadata: BuildParameterMetadata(p));
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

    // ───────────────────── Models ─────────────────────

    /// <summary>
    /// Applies a <c>CREATE MODEL</c> statement: resolves the
    /// <c>USING</c> path, asks the inference dispatcher to load the
    /// bundle (eagerly — failures surface at CREATE-time rather than at
    /// the first call site), builds a <see cref="ModelDescriptor"/>, and
    /// registers it under the catalog's <see cref="TableCatalog.DeclaredModels"/>.
    /// Disposes any sessions previously bound under the same name when
    /// <c>OR REPLACE</c> is in effect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>USING path resolution.</strong>
    /// <list type="bullet">
    ///   <item><description>Paths prefixed <c>file://</c> are treated as
    ///   absolute (the prefix is stripped). Useful for tests and
    ///   developer workflows where the ONNX file lives outside the
    ///   host's models directory.</description></item>
    ///   <item><description>All other paths are resolved against the
    ///   host's model directory (taken from
    ///   <c>TableCatalog.Models.ModelDirectory</c>). Throws when no
    ///   <see cref="DatumIngest.Models.ModelCatalog"/> is configured —
    ///   either supply <c>file://</c> or wire the model catalog before
    ///   issuing <c>CREATE MODEL</c>.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Persistence.</strong> SQL-defined models persist their
    /// verbatim CREATE MODEL source text in the catalog file (v5+).
    /// On startup, <see cref="TableCatalog.RehydrateModelsAsync"/> walks
    /// the persisted entries and re-invokes <see cref="ApplyCreateModelAsync"/>
    /// for each — the source text reparses, the USING path resolves
    /// against the current models directory, and the inference
    /// dispatcher reloads the bundle. Bound inference sessions are
    /// re-created at rehydrate time; they never travel across process
    /// boundaries.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The fixed schema all SQL-defined models live under. Built-in
    /// (ONNX / LlamaSharp / etc.) models also surface from this schema in
    /// <c>system.models</c>, so models are always addressable as
    /// <c>models.X</c> regardless of origin. CREATE MODEL refuses any other
    /// schema qualifier.
    /// </summary>
    private const string ModelsSchema = "models";

    public async Task ApplyCreateModelAsync(
        CreateModelStatement create, string? sourceText = null, bool suppressSave = false)
    {
        ValidateDefaultsContiguous(create.Parameters, $"CREATE MODEL {create.Name}");

        // Schema lockdown. CREATE MODEL always lands in `models`; explicit
        // qualifiers must match (case-insensitively) or be absent. This
        // mirrors how built-in models register — every model in the
        // catalog lives at `models.X`, and CREATE MODEL inherits that
        // namespacing rather than letting users scatter declarations
        // across `public`, custom schemas, etc.
        if (create.SchemaName is not null &&
            !string.Equals(create.SchemaName, ModelsSchema, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"CREATE MODEL {create.SchemaName}.{create.Name}: models must live in the " +
                $"'{ModelsSchema}' schema. Use 'CREATE MODEL {create.Name}' (lands in " +
                $"'{ModelsSchema}' implicitly) or 'CREATE MODEL {ModelsSchema}.{create.Name}' " +
                "(equivalent explicit form).");
        }

        if (_catalog.InferenceDispatcher is not Inference.IInferenceDispatcher dispatcher)
        {
            throw new InvalidOperationException(
                $"CREATE MODEL {create.Name}: no inference dispatcher is configured for this host. " +
                "Wire an IInferenceDispatcher via TableCatalog.InferenceDispatcher before issuing CREATE MODEL.");
        }

        // IMPLEMENTS validation happens before any inference-dispatcher work
        // — fast fail if the signature doesn't match the declared contract,
        // before paying load cost.
        if (create.ImplementsTaskName is { } taskName)
        {
            ValidateImplementsContract(create, taskName);
        }

        // Pass A + Pass B body-walk typecheck: when the declared return
        // type is a named struct (or Array<NamedStruct>), verify the
        // body's tail RETURN expression against the contract. Pass A
        // covers struct-literal returns directly; Pass B covers
        // variable-ref / UDF call / model call / CAST / array-literal
        // returns by walking the declared return-type annotations through
        // the registries.
        ValidateBodyReturnShape(create);

        // Slice-D registration-time pre-flight: a CHECK that the parameter's
        // default value already violates should fail CREATE MODEL, not wait
        // for the first call site that happens to omit the override. Runs
        // before the (expensive) ONNX dispatcher load.
        await ValidateDefaultsAgainstChecksAsync(
            create.Parameters,
            $"CREATE MODEL {create.Name}",
            CancellationToken.None).ConfigureAwait(false);

        QualifiedName qn = new(ModelsSchema, create.Name);

        if (create.IfNotExists && _catalog.DeclaredModels.TryGet(qn, out _))
        {
            return;
        }

        // Build the (alias → resolved-path) map from either the new
        // multi-file USING form or the legacy single-file form. The legacy
        // form's session is always keyed "default" so downstream code that
        // looks up BoundSessions["default"] keeps working without change.
        Dictionary<string, string> sessionPaths = new(StringComparer.Ordinal);
        List<ResolvedUsingFile>? resolvedFiles = null;
        string primaryResolvedPath;
        if (create.UsingFiles is { Count: > 0 } usingFiles)
        {
            resolvedFiles = new List<ResolvedUsingFile>(usingFiles.Count);
            foreach (UsingFileSpec spec in usingFiles)
            {
                string resolved = ResolveUsingPath(spec.Path, create.Name);
                if (!File.Exists(resolved))
                {
                    throw new FileNotFoundException(
                        $"CREATE MODEL {create.Name}: ONNX file not found at '{resolved}' " +
                        $"(USING '{spec.Path}' AS {spec.Alias}). " +
                        "Verify the path is correct relative to the host's model directory, " +
                        "or prefix with 'file://' for an absolute path.",
                        resolved);
                }
                sessionPaths[spec.Alias] = resolved;
                resolvedFiles.Add(new ResolvedUsingFile(spec.Path, spec.Alias, resolved));
            }
            primaryResolvedPath = resolvedFiles[0].ResolvedPath;
        }
        else
        {
            primaryResolvedPath = ResolveUsingPath(create.UsingPath, create.Name);
            if (!File.Exists(primaryResolvedPath))
            {
                throw new FileNotFoundException(
                    $"CREATE MODEL {create.Name}: ONNX file not found at '{primaryResolvedPath}' " +
                    $"(USING '{create.UsingPath}'). " +
                    "Verify the path is correct relative to the host's model directory, " +
                    "or prefix with 'file://' for an absolute path.",
                    primaryResolvedPath);
            }
            sessionPaths["default"] = primaryResolvedPath;
        }

        // Defer the ONNX session load until the body's first infer('alias', ...)
        // call. Eager LoadBundleAsync at registration time made catalog rehydration
        // (which replays every persisted CREATE MODEL on startup) load every
        // installed model's sessions before the host could serve requests — an
        // O(installed models) boot cost. With LazyModelSessions, registration
        // pays only path-resolution + AST cost; sessions land on first invoke
        // and stick around for subsequent calls.
        LazyModelSessions sessions = new(
            dispatcher,
            sessionPaths,
            bundleId: $"{qn} (USING '{create.UsingPath}')");

        ModelDescriptor descriptor = new(
            SchemaName: qn.Schema,
            Name: qn.Name,
            Parameters: create.Parameters,
            ReturnTypeName: create.ReturnTypeName,
            UsingPath: create.UsingPath,
            ResolvedUsingPath: primaryResolvedPath,
            StatementBody: create.StatementBody,
            BoundSessions: sessions,
            ReturnIsNotNull: create.ReturnIsNotNull,
            SourceText: sourceText ?? $"CREATE MODEL {qn}",
            ImplementsTaskName: create.ImplementsTaskName,
            UsingFiles: resolvedFiles);

        ModelDescriptor? displaced = _catalog.DeclaredModels.Register(
            descriptor, replace: create.OrReplace);

        // Wire the scalar dispatcher so `SELECT models.softmax_test(...)`
        // resolves to this model's body. Same pattern as the UDF adapter
        // path: the function registry holds the adapter under the model's
        // qualified name; OR REPLACE flips the entry atomically.
        RegisterModelAdapter(descriptor, replace: create.OrReplace);

        // Persist now so a process crash between CREATE MODEL completion
        // and a subsequent commit doesn't lose the registration. Skipped
        // during rehydration where the file already holds these entries
        // — re-saving would just rewrite identical bytes N times.
        if (!suppressSave)
        {
            _catalogStore?.Save(_udfs, _procedures, _catalog.DeclaredModels);
        }

        if (displaced is null)
        {
            _catalog.Events.Raise(new ModelCreatedEvent(qn, descriptor, sourceText));
        }
        else
        {
            _catalog.Events.Raise(new ModelAlteredEvent(qn, displaced, descriptor, sourceText));
        }

        // OR REPLACE: dispose the previous descriptor's sessions after the
        // new one is in place. In-flight queries holding a reference to the
        // displaced descriptor will keep running on the now-disposed
        // sessions until they finish — same behaviour as OR REPLACE for
        // UDF macros where in-flight inlined references keep the old AST
        // alive until the query completes.
        if (displaced is not null)
        {
            DisposeSessions(displaced);
        }
    }

    /// <summary>
    /// Applies an <c>EVICT MODEL</c> statement: drops the model's
    /// currently resident <see cref="IModel"/> instance from the residency
    /// manager so its VRAM is freed. The catalog registration is left in
    /// place — the next query that references the model will trigger a
    /// fresh load through <see cref="ModelCatalog.AcquireAsync"/>.
    /// </summary>
    /// <remarks>
    /// Manual EVICT is the user-side companion to the residency manager's
    /// automatic LRU eviction. Useful when the user knows they're done
    /// with a big model (Llama, SDXL) and wants to free VRAM
    /// proactively without waiting for pressure to force eviction.
    /// </remarks>
    public void ApplyEvictModel(EvictModelStatement evict, string? sourceText = null)
    {
        // Schema lockdown mirrors DROP MODEL — models live exclusively
        // in the `models` schema, so any other explicit qualifier is a
        // user mistake we surface with a pointed error rather than a
        // silent no-op.
        if (evict.SchemaName is not null &&
            !string.Equals(evict.SchemaName, ModelsSchema, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"EVICT MODEL {evict.SchemaName}.{evict.Name}: models live exclusively in the " +
                $"'{ModelsSchema}' schema. Use 'EVICT MODEL {evict.Name}' or " +
                $"'EVICT MODEL {ModelsSchema}.{evict.Name}'.");
        }

        ModelResidencyManager? residency = _catalog.Models?.ResidencyManager;
        ModelResidencyManager.EvictResult result =
            residency?.TryEvictUnpinned(evict.Name) ?? ModelResidencyManager.EvictResult.NotResident;

        switch (result)
        {
            case ModelResidencyManager.EvictResult.Evicted:
                break;

            case ModelResidencyManager.EvictResult.NotResident:
                if (!evict.IfExists)
                {
                    throw new InvalidOperationException(
                        $"Model '{evict.Name}' is not currently resident. Use EVICT MODEL IF EXISTS " +
                        "to make this a no-op.");
                }
                break;

            case ModelResidencyManager.EvictResult.Pinned:
                // Always an error — IF EXISTS doesn't suppress this. The
                // user asked to evict a model that's actively dispatching;
                // they need to know it wasn't done so they can retry.
                throw new InvalidOperationException(
                    $"Model '{evict.Name}' is currently in use by one or more active queries " +
                    "and cannot be evicted. Wait for those queries to complete, then retry.");
        }

        _ = sourceText;
    }

    /// <summary>
    /// Applies a <c>RESET CALIBRATION</c> statement: removes the per-model
    /// VRAM calibration curve from <see cref="ModelCatalog.CalibrationRegistry"/>
    /// and triggers a fresh on-disk write on the next save tick. The
    /// model itself stays resident (if loaded) and registered; the next
    /// dispatch falls through to the calibration coordinator for
    /// re-measurement.
    /// </summary>
    public void ApplyResetCalibration(ResetCalibrationStatement reset, string? sourceText = null)
    {
        if (reset.SchemaName is not null &&
            !string.Equals(reset.SchemaName, ModelsSchema, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"RESET CALIBRATION {reset.SchemaName}.{reset.Name}: models live exclusively in the " +
                $"'{ModelsSchema}' schema. Use 'RESET CALIBRATION {reset.Name}' or " +
                $"'RESET CALIBRATION {ModelsSchema}.{reset.Name}'.");
        }

        DatumIngest.Models.Calibration.CalibrationRegistry? registry =
            _catalog.Models?.CalibrationRegistry;
        bool removed = registry?.Remove(reset.Name) ?? false;

        if (!removed && !reset.IfExists)
        {
            throw new InvalidOperationException(
                $"Model '{reset.Name}' has no calibration entry. Use RESET CALIBRATION IF EXISTS " +
                "to make this a no-op.");
        }

        _ = sourceText;
    }

    /// <summary>
    /// Applies a <c>DROP MODEL</c> statement: removes the descriptor from
    /// the registry and disposes its bound inference sessions.
    /// </summary>
    public void ApplyDropModel(DropModelStatement drop, string? sourceText = null)
    {
        // Same schema lockdown as CREATE MODEL: explicit qualifiers must
        // be the `models` schema or absent. Lookups always go straight to
        // `models` — there's no point walking search_path when models
        // can only exist in one place.
        if (drop.SchemaName is not null &&
            !string.Equals(drop.SchemaName, ModelsSchema, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"DROP MODEL {drop.SchemaName}.{drop.Name}: models live exclusively in the " +
                $"'{ModelsSchema}' schema. Use 'DROP MODEL {drop.Name}' or " +
                $"'DROP MODEL {ModelsSchema}.{drop.Name}'.");
        }

        QualifiedName qn = new(ModelsSchema, drop.Name);

        ModelDescriptor? removed = _catalog.DeclaredModels.Unregister(qn);
        if (removed is null)
        {
            if (!drop.IfExists)
            {
                throw new InvalidOperationException(
                    $"Model '{qn}' is not registered. Use DROP MODEL IF EXISTS to make this a no-op.");
            }
            return;
        }

        // Drop the scalar adapter so subsequent SELECT calls fail with a
        // clean "not registered" error rather than dispatching into a
        // descriptor whose sessions are about to be disposed.
        _functions.UnregisterScalar(qn.ToString());

        // Drop the ModelCatalog entry too — symmetric with the dual
        // registration in RegisterModelAdapter. Without this, MIO's hoister
        // would still see the entry and route hoisted call sites into a
        // ProceduralModelAdapter whose descriptor's sessions are about to
        // be disposed below.
        _catalog.Models?.Unregister(removed.Name);
        // Evict the residency cache so any newly-acquired lease that races
        // the disposal below doesn't latch onto the stale ProceduralModelAdapter.
        _catalog.Models?.ResidencyManager.Evict(removed.Name);

        _catalogStore?.Save(_udfs, _procedures, _catalog.DeclaredModels);

        _catalog.Events.Raise(new ModelDroppedEvent(qn, removed, sourceText));

        DisposeSessions(removed);
    }

    /// <summary>
    /// Resolves a <c>USING</c> path supplied to a <c>CREATE MODEL</c>
    /// statement against the host's model directory, honouring the
    /// <c>file://</c> escape for absolute paths. Thin wrapper around
    /// <see cref="ModelCatalog.ResolveFilePath"/> that adds the CREATE MODEL
    /// caller label.
    /// </summary>
    private string ResolveUsingPath(string usingPath, string modelName)
        => ModelCatalog.ResolveFilePath(
            usingPath, _catalog.Models, $"CREATE MODEL {modelName}");

    /// <summary>
    /// Validates that <paramref name="create"/>'s parameter list and return
    /// type match the named task contract from
    /// <see cref="TaskTypeRegistry"/>. Field-by-field comparison is
    /// type-only (parameter names are documentation, not contract); named
    /// types match by name through the type-annotation resolver.
    /// </summary>
    /// <remarks>
    /// Throws <see cref="InvalidOperationException"/> with a clear message
    /// printing both the contract's expected signature and the model's
    /// declared signature when a mismatch fires. Errors surface at
    /// <c>CREATE MODEL</c> time so users find out before any inference
    /// dispatcher loads weights.
    /// </remarks>
    private static void ValidateImplementsContract(
        CreateModelStatement create, string taskName)
    {
        TaskTypeRegistry.TaskContract? contract = TaskTypeRegistry.TryGet(taskName);
        if (contract is null)
        {
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: IMPLEMENTS '{taskName}' references an unknown task contract. "
                + "See `SELECT name FROM datum_catalog.tasks` for the registered vocabulary.");
        }

        // Parameter arity check: the model's required (non-default) parameters
        // must match the contract's input list exactly. Models may declare
        // *additional* optional parameters (defaults at the tail) beyond the
        // contract — those are extra runtime knobs the model exposes, like
        // YOLOX's confidence/IoU thresholds. The contract still defines the
        // minimum invocation shape; optional params are additive.
        int requiredCount = 0;
        foreach (UdfParameter p in create.Parameters)
        {
            if (p.Default is null) requiredCount++;
            else break; // Defaults are contiguous at the tail (enforced elsewhere).
        }
        if (requiredCount != contract.InputKinds.Count)
        {
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: IMPLEMENTS {contract.Name} requires "
                + $"{contract.InputKinds.Count} required parameter(s) but the model declares {requiredCount}. "
                + $"Expected signature: ({string.Join(", ", contract.InputKinds)}) → {contract.ReturnKind}.");
        }

        // Per-parameter type match against the contract — applies only to
        // the required (leading) parameters. Names are documentation; only
        // kinds matter. Optional trailing parameters are model-specific
        // knobs and aren't validated against the contract.
        for (int i = 0; i < contract.InputKinds.Count; i++)
        {
            UdfParameter param = create.Parameters[i];
            if (param.TypeName is null
                || !TypeAnnotationResolver.TryParse(param.TypeName, out DataKind kind, out bool isArray))
            {
                throw new QueryPlanException(
                    $"CREATE MODEL {create.Name}: IMPLEMENTS {contract.Name} parameter "
                    + $"#{i + 1} ('{param.Name}') has unresolved type '{param.TypeName}'.");
            }
            string? namedTypeName = TypeAnnotationResolver.IsNamedType(StripArrayWrapperForName(param.TypeName))
                ? StripArrayWrapperForName(param.TypeName)
                : null;
            if (!contract.InputKinds[i].Matches(kind, isArray, namedTypeName))
            {
                throw new QueryPlanException(
                    $"CREATE MODEL {create.Name}: IMPLEMENTS {contract.Name} parameter "
                    + $"#{i + 1} expected {contract.InputKinds[i]}, got "
                    + $"{(isArray ? $"Array<{namedTypeName ?? kind.ToString()}>" : namedTypeName ?? kind.ToString())}. "
                    + $"Expected signature: ({string.Join(", ", contract.InputKinds)}) → {contract.ReturnKind}.");
            }
        }

        // Return type match.
        if (!TypeAnnotationResolver.TryParse(create.ReturnTypeName, out DataKind returnKind, out bool returnIsArray))
        {
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: IMPLEMENTS {contract.Name} return type "
                + $"'{create.ReturnTypeName}' is not a recognized type annotation.");
        }
        string? returnNamedTypeName = TypeAnnotationResolver.IsNamedType(StripArrayWrapperForName(create.ReturnTypeName))
            ? StripArrayWrapperForName(create.ReturnTypeName)
            : null;
        if (!contract.ReturnKind.Matches(returnKind, returnIsArray, returnNamedTypeName))
        {
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: IMPLEMENTS {contract.Name} return expected "
                + $"{contract.ReturnKind}, got "
                + $"{(returnIsArray ? $"Array<{returnNamedTypeName ?? returnKind.ToString()}>" : returnNamedTypeName ?? returnKind.ToString())}. "
                + $"Expected signature: ({string.Join(", ", contract.InputKinds)}) → {contract.ReturnKind}.");
        }
    }

    /// <summary>
    /// Pass A + Pass B body-walk typecheck. When the declared return type
    /// is a named struct (or <c>Array&lt;NamedStruct&gt;</c>), verifies the
    /// body's tail RETURN expression against the contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Pass A — struct-literal returns.</strong> When the body's
    /// tail RETURN is a struct literal (<c>RETURN { class: ..., score: ... }</c>),
    /// compares the literal's field names against the named type's. Field-kind
    /// verification is intentionally skipped because the parser's literal-kind
    /// inference has known quirks (small numeric literals parse to <c>Int8</c>,
    /// not <c>Int32</c>; <c>RETURN { class: 1, score: 0.5 }</c> would
    /// false-positive on kind).
    /// </para>
    /// <para>
    /// <strong>Pass B — indirect returns.</strong> Compares the model's
    /// declared return-type annotation against the RETURN expression's
    /// derived annotation, recovered from:
    /// <list type="bullet">
    /// <item>Variable references — looked up against the body's DECLAREs +
    /// parameters by name.</item>
    /// <item>UDF / model calls — looked up by qualified name against the
    /// registries; comparison is on the callee's declared <c>RETURNS T</c>
    /// annotation.</item>
    /// <item>CAST expressions — use the cast target as the actual type.</item>
    /// <item>Array literals (<c>[ {...}, {...} ]</c> against
    /// <c>RETURNS Array&lt;NamedStruct&gt;</c>) — recurse Pass A's
    /// field-name check into each element.</item>
    /// <item>Built-in scalar functions — arity/array-ness check only
    /// (named-type names aren't surfaced through <see cref="FunctionDescriptor"/>
    /// today, so a same-kind same-array-ness call passes through).</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>What doesn't fire.</strong> SET reassignment after DECLARE
    /// isn't tracked (only the DECLARE's annotation is consulted); RETURN
    /// expressions inside non-tail control flow are ignored (Pass B looks
    /// at the body's tail statement only).
    /// </para>
    /// </remarks>
    private void ValidateBodyReturnShape(CreateModelStatement create)
    {
        // Resolve the expected return-type triple (kind, isArray, optional
        // named-type name). Skip when the annotation isn't a named type
        // (primitives have no struct shape to verify) and isn't an
        // array of a named type.
        (DataKind Kind, bool IsArray, string? NamedTypeName)? expected =
            ParseAnnotationTriple(create.ReturnTypeName);
        if (expected is null) return;
        if (expected.Value.NamedTypeName is null)
        {
            // No named type in the declared return — there's nothing
            // structural to compare against.
            return;
        }

        // Find the tail RETURN. Bodies must end with RETURN (enforced by
        // ValidateProceduralBody at parse time), but be defensive against
        // an empty body — earlier validation should have caught it.
        if (create.StatementBody.Count == 0) return;
        if (create.StatementBody[^1] is not ReturnStatement ret) return;

        // Pass A — struct-literal-only check against a named (non-array)
        // struct return. Direct comparison of field names without
        // exercising the registries.
        if (ret.Value is StructLiteralExpression literal && !expected.Value.IsArray)
        {
            ValidatePassAStructLiteral(create, expected.Value.NamedTypeName, literal);
            return;
        }

        // Pass B — indirect returns. Walk DECLAREs + parameters into a
        // variable-name → type-annotation map once, then dispatch on
        // the RETURN expression shape.
        Dictionary<string, string> declaredVars = CollectDeclaredVarTypes(create);
        ValidatePassBReturnExpression(create, expected.Value, ret.Value, declaredVars);
    }

    /// <summary>
    /// Parses a textual type annotation into the <c>(kind, isArray,
    /// optional named-type name)</c> triple used by Pass A / Pass B
    /// comparisons. Returns <see langword="null"/> when the annotation
    /// is unrecognised — caller treats that as "skip the check" rather
    /// than throwing, since IMPLEMENTS validation already enforced
    /// resolvability at CREATE time.
    /// </summary>
    private static (DataKind Kind, bool IsArray, string? NamedTypeName)? ParseAnnotationTriple(
        string? annotation)
    {
        if (string.IsNullOrEmpty(annotation)) return null;
        if (!TypeAnnotationResolver.TryParse(annotation, out DataKind kind, out bool isArray))
        {
            return null;
        }
        string inner = StripArrayWrapperForName(annotation);
        string? name = TypeAnnotationResolver.IsNamedType(inner) ? inner : null;
        return (kind, isArray, name);
    }

    /// <summary>
    /// Pass A field-name comparison against a named struct return.
    /// Throws <see cref="QueryPlanException"/> with both expected + actual
    /// field lists on mismatch.
    /// </summary>
    private static void ValidatePassAStructLiteral(
        CreateModelStatement create,
        string expectedNamedType,
        StructLiteralExpression literal)
    {
        IReadOnlyList<StructFieldDescriptor>? expectedFields = ResolveNamedStructFields(expectedNamedType);
        if (expectedFields is null) return; // Defensive — registry/resolver out of sync.

        HashSet<string> expectedNameSet = new(
            expectedFields.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var actualNameSet = new HashSet<string>(
            literal.Fields.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        if (!expectedNameSet.SetEquals(actualNameSet))
        {
            string expectedList = string.Join(", ", expectedFields.Select(f => f.Name));
            string actualList = string.Join(", ", literal.Fields.Select(f => f.Name));
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: RETURN struct literal fields don't match declared "
                + $"return type '{expectedNamedType}'. "
                + $"Expected fields: [{expectedList}]. Got: [{actualList}].");
        }
    }

    /// <summary>
    /// Resolves the field list for a named struct via a fresh
    /// <see cref="TypeRegistry"/> — its constructor pre-interns the
    /// vocabulary, so the lookup hits without any external state. Returns
    /// <see langword="null"/> if the registry and resolver are out of sync
    /// (a programming error; callers treat it as "skip").
    /// </summary>
    private static IReadOnlyList<StructFieldDescriptor>? ResolveNamedStructFields(string namedType)
    {
        TypeRegistry registry = new();
        int typeId = registry.GetTypeIdByName(namedType);
        if (typeId == TypeRegistry.NoType) return null;
        TypeDescriptor? desc = registry.GetDescriptor(typeId);
        return desc?.Fields;
    }

    /// <summary>
    /// Walks the body's DECLARE statements and the model's parameter list
    /// into a name → declared-type-annotation map. Used by Pass B to
    /// resolve <c>RETURN varname</c> against the variable's declared
    /// type. SET-after-DECLARE reassignment isn't tracked — only the
    /// initial DECLARE annotation is consulted.
    /// </summary>
    private static Dictionary<string, string> CollectDeclaredVarTypes(CreateModelStatement create)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (UdfParameter param in create.Parameters)
        {
            if (param.TypeName is not null)
            {
                result[param.Name] = param.TypeName;
            }
        }
        foreach (Statement stmt in create.StatementBody)
        {
            CollectDeclaredVarTypesInStatement(stmt, result);
        }
        return result;
    }

    private static void CollectDeclaredVarTypesInStatement(
        Statement stmt, Dictionary<string, string> result)
    {
        switch (stmt)
        {
            case DeclareStatement decl when decl.TypeName is not null:
                // First DECLARE wins per scope. Re-DECLARE in inner blocks
                // would shadow but Pass B doesn't model nested scopes — the
                // outer DECLARE drives the check, which matches the
                // straight-line bodies Pass B targets.
                result.TryAdd(decl.VariableName, decl.TypeName);
                break;
            case BlockStatement block:
                foreach (Statement inner in block.Statements)
                {
                    CollectDeclaredVarTypesInStatement(inner, result);
                }
                break;
            case IfStatement ifStmt:
                CollectDeclaredVarTypesInStatement(ifStmt.Then, result);
                if (ifStmt.Else is not null)
                {
                    CollectDeclaredVarTypesInStatement(ifStmt.Else, result);
                }
                break;
            case WhileStatement whileStmt:
                CollectDeclaredVarTypesInStatement(whileStmt.Body, result);
                break;
        }
    }

    /// <summary>
    /// Pass B dispatch on the RETURN expression. Compares against the
    /// declared return-type triple via <see cref="MatchAnnotationTriples"/>;
    /// shapes that can't be resolved to a concrete annotation skip the
    /// check rather than false-positiving (the runtime path will still
    /// surface a clean error if the return is genuinely wrong).
    /// </summary>
    private void ValidatePassBReturnExpression(
        CreateModelStatement create,
        (DataKind Kind, bool IsArray, string? NamedTypeName) expected,
        Expression value,
        IReadOnlyDictionary<string, string> declaredVars)
    {
        // Array literal (parsed as `array(...)` function call).
        if (value is FunctionCallExpression arrayCall
            && arrayCall.SchemaName is null
            && string.Equals(arrayCall.FunctionName, "array", StringComparison.OrdinalIgnoreCase))
        {
            ValidatePassBArrayLiteral(create, expected, arrayCall);
            return;
        }

        // Variable / parameter reference. Bare identifiers parse as
        // ColumnReference(TableName=null) inside a model body; the
        // parameter-ref form ($name) parses as ParameterExpression.
        string? actualAnnotation = null;
        switch (value)
        {
            case ColumnReference col when col.TableName is null && col.SchemaName is null:
                declaredVars.TryGetValue(col.ColumnName, out actualAnnotation);
                break;

            case ParameterExpression param:
                declaredVars.TryGetValue(param.Name, out actualAnnotation);
                break;

            case CastExpression cast:
                actualAnnotation = cast.TargetType;
                break;

            case FunctionCallExpression fnCall:
                actualAnnotation = ResolveCalleeReturnAnnotation(fnCall);
                if (actualAnnotation is null)
                {
                    // Built-in scalar function — fall back to
                    // arity/array-ness check via descriptor signatures.
                    ValidatePassBBuiltinCall(create, expected, fnCall);
                    return;
                }
                break;
        }

        if (actualAnnotation is null) return; // Unresolvable — skip.

        (DataKind, bool, string?)? actual = ParseAnnotationTriple(actualAnnotation);
        if (actual is null) return; // Unparseable — skip.

        if (!MatchAnnotationTriples(expected, actual.Value))
        {
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: RETURN expression type '{actualAnnotation}' "
                + $"doesn't match declared return type '{create.ReturnTypeName}'. "
                + $"Expected: {FormatTriple(expected)}. Got: {FormatTriple(actual.Value)}.");
        }
    }

    /// <summary>
    /// Pass B array-literal check. When the declared return is
    /// <c>Array&lt;NamedStruct&gt;</c> and the body returns <c>[{...}, ...]</c>,
    /// applies Pass A's field-name comparison to each struct-literal
    /// element. Empty arrays skip — Pass B can't infer per-element shape
    /// from an empty literal.
    /// </summary>
    private static void ValidatePassBArrayLiteral(
        CreateModelStatement create,
        (DataKind Kind, bool IsArray, string? NamedTypeName) expected,
        FunctionCallExpression arrayCall)
    {
        if (!expected.IsArray)
        {
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: RETURN expression is an array literal but "
                + $"declared return type '{create.ReturnTypeName}' is not an array.");
        }

        if (expected.NamedTypeName is null) return; // Array<scalar> — nothing structural to check.

        foreach (Expression element in arrayCall.Arguments)
        {
            if (element is StructLiteralExpression elementLiteral)
            {
                ValidatePassAStructLiteral(create, expected.NamedTypeName, elementLiteral);
            }
            // Non-literal elements (variable refs, function calls) inside
            // an array literal would need recursive Pass B dispatch.
            // Defer until a real case arrives — most array-literal returns
            // are inlined struct literals.
        }
    }

    /// <summary>
    /// Looks up <paramref name="fnCall"/> against the UDF and declared-model
    /// registries (in that order) and returns the callee's declared
    /// <c>RETURNS T</c> annotation. Returns <see langword="null"/> when the
    /// call resolves to a built-in or to nothing (the caller falls back to
    /// the built-in shape check or skips).
    /// </summary>
    private string? ResolveCalleeReturnAnnotation(FunctionCallExpression fnCall)
    {
        // Explicit `models.X(...)` qualifier — go straight to DeclaredModels.
        if (string.Equals(fnCall.SchemaName, "models", StringComparison.OrdinalIgnoreCase))
        {
            QualifiedName modelQn = new("models", fnCall.FunctionName);
            if (_catalog.DeclaredModels.TryGet(modelQn, out ModelDescriptor? model))
            {
                return model.ReturnTypeName;
            }
            return null;
        }

        // UDF lookup — explicit schema or search-path walk.
        if (_udfs.TryResolve(fnCall.SchemaName, fnCall.FunctionName, _catalog.SearchPath,
            out UdfDescriptor? udf))
        {
            return udf.ReturnTypeName;
        }

        // Unqualified call that didn't resolve as a UDF — could still be a
        // model in the `models` schema.
        if (fnCall.SchemaName is null)
        {
            QualifiedName modelQn = new("models", fnCall.FunctionName);
            if (_catalog.DeclaredModels.TryGet(modelQn, out ModelDescriptor? model))
            {
                return model.ReturnTypeName;
            }
        }

        return null;
    }

    /// <summary>
    /// Pass B built-in fallback. When the RETURN expression resolves to a
    /// registered built-in scalar function, check whether every matched
    /// signature variant produces an array vs. a scalar. If all variants
    /// agree and disagree with the declared <c>RETURNS T</c> array-ness,
    /// throw. Otherwise skip (named-type names aren't surfaced through
    /// <see cref="FunctionDescriptor"/>, so we can't compare by name).
    /// </summary>
    private void ValidatePassBBuiltinCall(
        CreateModelStatement create,
        (DataKind Kind, bool IsArray, string? NamedTypeName) expected,
        FunctionCallExpression fnCall)
    {
        FunctionDescriptor? descriptor = _functions.TryGetScalarDescriptor(fnCall.CallName);
        if (descriptor is null || descriptor.Signatures.Count == 0) return;

        bool allProduceArray = true;
        bool noneProduceArray = true;
        foreach (FunctionSignatureVariant variant in descriptor.Signatures)
        {
            if (variant.ReturnType.ProducesArray) noneProduceArray = false;
            else allProduceArray = false;
        }

        // Mixed signatures — can't decide statically; skip.
        if (!allProduceArray && !noneProduceArray) return;

        bool builtinIsArray = allProduceArray;
        if (builtinIsArray != expected.IsArray)
        {
            string actualShape = builtinIsArray ? "Array<...>" : "scalar";
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: RETURN built-in '{fnCall.CallName}' produces "
                + $"{actualShape} but declared return type '{create.ReturnTypeName}' is "
                + $"{(expected.IsArray ? "an array" : "a scalar")}.");
        }
    }

    /// <summary>
    /// Pass B comparison primitive. Triples match when kind + isArray are
    /// equal and either the expected has no named-type constraint or the
    /// actual carries the same name (case-insensitive).
    /// </summary>
    private static bool MatchAnnotationTriples(
        (DataKind Kind, bool IsArray, string? NamedTypeName) expected,
        (DataKind Kind, bool IsArray, string? NamedTypeName) actual)
    {
        if (expected.Kind != actual.Kind) return false;
        if (expected.IsArray != actual.IsArray) return false;
        if (expected.NamedTypeName is null) return true;
        // Expected has a named-type constraint. Actual must carry the
        // same name; an unnamed actual (e.g. a built-in returning bare
        // Struct) doesn't match.
        return actual.NamedTypeName is not null
            && string.Equals(expected.NamedTypeName, actual.NamedTypeName,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Human-readable rendering of a triple for Pass B error messages.</summary>
    private static string FormatTriple((DataKind Kind, bool IsArray, string? NamedTypeName) triple)
        => (triple.IsArray, triple.NamedTypeName) switch
        {
            (false, null) => triple.Kind.ToString(),
            (true, null) => $"Array<{triple.Kind}>",
            (false, _) => triple.NamedTypeName!,
            (true, _) => $"Array<{triple.NamedTypeName}>",
        };

    /// <summary>
    /// Returns the inner identifier when <paramref name="annotation"/> is
    /// wrapped in <c>Array&lt;...&gt;</c>, otherwise <paramref name="annotation"/>
    /// unchanged. Used by contract validation to check whether an
    /// annotation's element is a named type independent of array-ness.
    /// </summary>
    private static string StripArrayWrapperForName(string annotation)
    {
        const string Prefix = "Array<";
        if (annotation.Length > Prefix.Length + 1
            && annotation.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && annotation[^1] == '>')
        {
            return annotation[Prefix.Length..^1].Trim();
        }
        return annotation;
    }

    /// <summary>
    /// Best-effort disposal of every bound session in a descriptor.
    /// Disposal failures are logged via <see cref="Console.Error"/>
    /// rather than rethrown — the descriptor is already out of the
    /// registry and a thrown exception here would mask the real reason
    /// the descriptor was being released.
    /// </summary>
    private static void DisposeSessions(ModelDescriptor descriptor)
        => descriptor.BoundSessions.DisposeLoaded();

    /// <summary>
    /// Registers (or replaces) the SQL-defined model on two surfaces:
    /// <list type="bullet">
    ///   <item><description>
    ///     The scalar function registry — the <see cref="ProceduralModelFunction"/>
    ///     adapter satisfies any call site the planner didn't hoist into a
    ///     <c>ModelInvocationOperator</c> (e.g. inside a UDF body, inside
    ///     an unhoisted clause). The hoister prefers MIO for top-level
    ///     <c>models.X(...)</c> calls; the scalar dispatch is the fallback.
    ///   </description></item>
    ///   <item><description>
    ///     The <see cref="ModelCatalog"/> via a <see cref="ProceduralModelAdapter"/>
    ///     wrapped in a <see cref="ModelCatalogEntry"/>. The hoister consults
    ///     this catalog at plan time; once the SQL-defined model has an entry
    ///     here, top-level call sites lift into MIO and inherit operator-
    ///     boundary parity with built-in models (tracer, residency lease,
    ///     RowLimit short-circuit, streaming-sink awareness, sub-batching).
    ///   </description></item>
    /// </list>
    /// Both surfaces stay in sync: <c>OR REPLACE</c> replaces both atomically,
    /// <c>DROP MODEL</c> tears down both.
    /// </summary>
    private void RegisterModelAdapter(ModelDescriptor descriptor, bool replace)
    {
        ProceduralModelFunction scalarAdapter = new(descriptor, _functions);
        FunctionDescriptor catalogDescriptor = BuildModelFunctionDescriptor(descriptor);
        _functions.RegisterScalarInstance(
            descriptor.QualifiedName.ToString(),
            scalarAdapter,
            descriptor: catalogDescriptor,
            replace: replace);

        ModelCatalog? models = _catalog.Models;
        if (models is null) return;

        ProceduralModelAdapter iModelAdapter = new(descriptor, _functions);
        // Estimate VRAM as on-disk file size × 1.2 — same heuristic the
        // C# builtin path uses (ModelResidencyManager.DefaultFileSizeMultiplier).
        // Weights dominate the resident footprint; the 20% slack covers ORT's
        // session metadata + per-input/output tensor buffers. Without this,
        // EstimatedVramBytes was 0, making every SQL-defined model invisible
        // to the residency manager's admission control — multiple models would
        // happily co-load past dedicated VRAM and spill into shared memory,
        // which the NVIDIA driver mishandles into native crashes inside
        // InferenceSession.Run.
        long estimatedVram = EstimateFileSizeBytes(descriptor.ResolvedUsingPath);
        ModelCatalogEntry entry = new(
            Name: descriptor.Name,
            Backend: "sql",
            RelativePath: null,
            InputKinds: iModelAdapter.InputKinds,
            OutputKind: iModelAdapter.OutputKind,
            IsDeterministic: iModelAdapter.IsDeterministic,
            // Pre-warm every bound session synchronously before returning
            // the adapter. ProceduralModelAdapter's constructor records
            // metadata only — the actual ONNX session loads lazily on
            // first infer() call inside the body. Without this warm-up
            // the residency manager's RecordWeightCost would measure
            // VRAM before/after an effectively-empty loader call and
            // report weight_cost = 0 → NULL in system.models. Shifting
            // the load cost forward to the loader call (a) makes the
            // weight-cost measurement see real VRAM growth, and (b)
            // doesn't add any net work — the first inference would have
            // paid the same cost. Multi-session bundles load every
            // alias up-front; for typical SQL-defined models every
            // alias is referenced per-inference anyway.
            Loader: _ =>
            {
                foreach (string alias in descriptor.BoundSessions.Keys)
                {
                    descriptor.BoundSessions
                        .ResolveAsync(alias, CancellationToken.None)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                }
                return iModelAdapter;
            },
            OptionalArgKinds: iModelAdapter.OptionalKinds.Count > 0 ? iModelAdapter.OptionalKinds : null,
            EstimatedVramBytes: estimatedVram,
            DisplayName: descriptor.QualifiedName.ToString(),
            Batchable: iModelAdapter.IsBatchable,
            // Threads the resolved ONNX path through to the calibration
            // layer so RecordWeightCost can fingerprint SQL-defined
            // models the same way it fingerprints builtins. Without
            // this, every SQL-defined model's system.models row would
            // surface weight_cost_bytes = NULL because the residency
            // manager's RecordWeightCost short-circuits on a null
            // RelativePath. Use the same fingerprint regardless of
            // multi-file bundles — the primary ResolvedUsingPath is
            // the anchor weights file the planner already treats as
            // the model's canonical identity.
            FingerprintPath: descriptor.ResolvedUsingPath,
            // Preserve the RETURNS-clause array bit and the user-declared
            // parameter shapes so the LanguageServer manifest can render
            // `img: Image` / `→ Array<Float32>` instead of the lossy
            // `input: <kind>` / `→ <element kind>` defaults that
            // ModelCatalogEntry's name/kind-only fields would produce.
            OutputIsArray: iModelAdapter.OutputIsArray,
            ParameterInfos: BuildModelParameterInfos(descriptor));

        if (replace)
        {
            models.Unregister(descriptor.Name);
            // Drop the cached IModel for this name so AcquireAsync re-runs
            // the loader against the new ModelCatalogEntry. Without this,
            // the residency manager keeps handing back the displaced
            // ProceduralModelAdapter whose descriptor's sessions are about
            // to be disposed by DisposeSessions(displaced) — every
            // subsequent invocation would then fault inside Session.Run
            // with "Cannot access a disposed object".
            models.ResidencyManager.Evict(descriptor.Name);
        }
        models.Register(entry);
    }

    /// <summary>
    /// Synthesises a <see cref="FunctionDescriptor"/> for a SQL-defined
    /// model so the type resolver can read its return shape (including
    /// <c>Array&lt;T&gt;</c> returns) via the standard per-signature
    /// path. Mirrors the UDF analog
    /// (<see cref="BuildProceduralDescriptor(UdfDescriptor)"/>): parameter
    /// kinds use <see cref="DataKindMatcher.Any"/> because the adapter
    /// does its own arity check; the synthesised signature carries the
    /// return rule, not gating logic.
    /// </summary>
    private static FunctionDescriptor BuildModelFunctionDescriptor(ModelDescriptor model)
    {
        DataKind returnKind = DataKind.String;
        bool returnIsArray = false;
        if (model.ReturnTypeName is not null)
        {
            TypeAnnotationResolver.TryParse(model.ReturnTypeName, out returnKind, out returnIsArray);
        }

        ReturnTypeRule returnRule = returnIsArray
            ? ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(returnKind))
            : ReturnTypeRule.Constant(returnKind);

        ParameterSpec[] parameters = new ParameterSpec[model.Parameters.Count];
        for (int i = 0; i < model.Parameters.Count; i++)
        {
            UdfParameter p = model.Parameters[i];
            // Resolve the declared type into a kind + array-ness so hover /
            // signature help / completion show the actual annotation
            // (<c>img: Image</c>) instead of the wildcard <c>img: Any</c>
            // we used to emit. Unrecognised type names fall back to
            // <c>Any</c> rather than throwing — CREATE MODEL already
            // validates the annotation at registration time, so the
            // fallback is purely defensive.
            DataKindMatcher matcher = DataKindMatcher.Any;
            ArrayMatch arrayMatch = ArrayMatch.Either;
            if (TypeAnnotationResolver.TryParse(p.TypeName, out DataKind kind, out bool isArray))
            {
                matcher = DataKindMatcher.Exact(kind);
                arrayMatch = isArray ? ArrayMatch.Array : ArrayMatch.Scalar;
            }
            parameters[i] = new ParameterSpec(
                p.Name,
                matcher,
                IsOptional: p.Default is not null,
                IsArray: arrayMatch,
                Metadata: BuildParameterMetadata(p));
        }

        return new FunctionDescriptor(
            PrimaryName: model.Name,
            Aliases: Array.Empty<string>(),
            Category: FunctionCategory.Utility,
            Description: $"SQL-defined model {model.QualifiedName}.",
            Signatures:
            [
                new FunctionSignatureVariant(parameters, VariadicTrailing: null, ReturnType: returnRule),
            ]);
    }

    /// <summary>
    /// Builds the per-parameter metadata snapshot attached to a SQL-defined
    /// model's <see cref="ModelCatalogEntry"/>. Unlike the function-descriptor
    /// path (which expresses kinds as matcher / arity), this is a plain
    /// name + kind + shape list — what the language server needs to render
    /// hover / signature / completion popups. Returns <see langword="null"/>
    /// when the descriptor has no parameters so the manifest builder can
    /// fall back to its generic <c>input</c>/<c>inputN</c> labels.
    /// </summary>
    private static IReadOnlyList<ModelParameterInfo>? BuildModelParameterInfos(ModelDescriptor model)
    {
        if (model.Parameters.Count == 0) return null;
        ModelParameterInfo[] infos = new ModelParameterInfo[model.Parameters.Count];
        for (int i = 0; i < model.Parameters.Count; i++)
        {
            UdfParameter p = model.Parameters[i];
            // CREATE MODEL validated the annotation at registration, but we
            // defensively fall back to a non-array Unknown kind on parse
            // failure rather than throwing — manifest rendering should
            // never break model registration.
            DataKind kind = DataKind.Unknown;
            bool isArray = false;
            if (TypeAnnotationResolver.TryParse(p.TypeName, out DataKind parsedKind, out bool parsedIsArray))
            {
                kind = parsedKind;
                isArray = parsedIsArray;
            }
            infos[i] = new ModelParameterInfo(p.Name, kind, isArray, IsOptional: p.Default is not null);
        }
        return infos;
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

        // Same DDL-capable schema rules as ApplyCreateFunction: explicit
        // qualification wins; unqualified picks the first DDL-capable
        // schema on the session search_path.
        QualifiedName qn = Resolver().ResolveForCreate(create.SchemaName, create.Name);

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

        // Capture pre-state for the event below — Created vs Altered turns
        // on whether the key already had a descriptor.
        _procedures.TryGet(qn, out ProcedureDescriptor? before);

        _procedures.Register(descriptor, replace: create.OrReplace);
        _catalogStore?.Save(_udfs, _procedures, _catalog.DeclaredModels);

        if (before is null)
        {
            _catalog.Events.Raise(new ProcedureCreatedEvent(qn, descriptor, sourceText));
        }
        else
        {
            _catalog.Events.Raise(new ProcedureAlteredEvent(qn, before, descriptor, sourceText));
        }
    }

    /// <summary>
    /// Applies a <c>DROP PROCEDURE</c> statement. Throws when the named
    /// procedure isn't registered unless the statement carries <c>IF EXISTS</c>.
    /// </summary>
    public void ApplyDropProcedure(DropProcedureStatement drop, string? sourceText = null)
    {
        if (!_procedures.TryResolve(drop.SchemaName, drop.Name, _catalog.SearchPath, out ProcedureDescriptor? proc))
        {
            if (drop.IfExists) return;
            string label = drop.SchemaName is null ? drop.Name : $"{drop.SchemaName}.{drop.Name}";
            throw new InvalidOperationException(
                $"Procedure '{label}' is not registered. " +
                "Use DROP PROCEDURE IF EXISTS to make this a no-op.");
        }

        _procedures.Unregister(proc.QualifiedName);
        _catalogStore?.Save(_udfs, _procedures, _catalog.DeclaredModels);

        _catalog.Events.Raise(new ProcedureDroppedEvent(proc.QualifiedName, proc, sourceText));
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

    /// <summary>
    /// Reads the on-disk size of the model bundle and applies the same
    /// 1.2× multiplier the C# builtin path uses to size resident VRAM.
    /// Returns 0 (forces the admission manager to treat the load as
    /// unknown-size) when the file is missing or unreadable rather than
    /// throwing — registration is past the point where we know the file
    /// existed at <c>CREATE MODEL</c> time, so a stat failure here is a
    /// rare race we'd rather log around than abort.
    /// </summary>
    private static long EstimateFileSizeBytes(string? resolvedPath)
    {
        if (string.IsNullOrEmpty(resolvedPath)) return 0;
        try
        {
            FileInfo info = new(resolvedPath);
            if (!info.Exists) return 0;
            // 1.2× — matches the builtin ModelCatalogEntry estimates and the
            // residency manager's own default multiplier.
            return (long)(info.Length * 1.2);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Registration-time pre-flight: evaluates each parameter's default
    /// expression once against an empty scope and runs the canonicalised
    /// <c>CHECK</c> against it, so an out-of-range default (e.g. a typo'd
    /// <c>0.25</c> that should have been <c>0.025</c>) surfaces as a
    /// CREATE-time error instead of waiting for the first call site that
    /// happens to omit the override. Skips parameters without a default
    /// (nothing to evaluate) or without a check (nothing to enforce).
    /// </summary>
    /// <remarks>
    /// Parameter defaults in real catalog SQL are constant expressions —
    /// <c>CAST(0.25 AS Float32)</c>, literals, simple arithmetic — so the
    /// evaluator never blocks. The evaluator is built against the same
    /// <see cref="FunctionRegistry"/> the body uses, so a default that
    /// invokes a registered function (rare, but legal) still resolves.
    /// </remarks>
    private async ValueTask ValidateDefaultsAgainstChecksAsync(
        IReadOnlyList<UdfParameter> parameters,
        string contextLabel,
        CancellationToken cancellationToken)
    {
        Arena scratch = new();
        MemoryAccountant accountant = new();
        VariableScope checkScope = new(accountant);
        // Scope-bound evaluator so CustomCheck expressions resolve the
        // parameter name to the just-evaluated default value (and any
        // earlier parameter to its evaluated default).
        ExpressionEvaluator evaluator = new(
            _functions,
            meter: null,
            outerRow: null,
            sourceSchema: null,
            letBindingExpressions: null,
            store: scratch,
            sidecarRegistry: _catalog.SidecarRegistry,
            variableScope: checkScope,
            variableStore: scratch,
            typeRegistry: null,
            accountant: accountant);
        EvaluationFrame frame = new(
            Row.Empty,
            scratch,
            scratch,
            accountant,
            outerRow: null,
            sidecarRegistry: _catalog.SidecarRegistry,
            types: null);

        for (int i = 0; i < parameters.Count; i++)
        {
            UdfParameter p = parameters[i];
            if (p.Default is null) continue;

            ValueRef defaultValue;
            try
            {
                defaultValue = await evaluator.EvaluateAsValueRefAsync(p.Default, frame, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Don't pretend a broken default is a constraint failure — surface
                // the underlying evaluation error with the registration context
                // wrapped around it so the caller knows which CREATE site to fix.
                throw new InvalidOperationException(
                    $"{contextLabel}: default expression for parameter '@{p.Name}' failed to evaluate at registration time. {ex.Message}",
                    ex);
            }

            // Declare into scope so subsequent parameters' CustomChecks can
            // reference this one — mirrors the runtime BindParametersAsync
            // ordering. Cheap; the scratch arena + accountant are torn down
            // with this method.
            checkScope.Declare(p.Name, defaultValue);

            if (p.Check is null) continue;
            ParameterCheck typed = ParameterCheckWalker.Canonicalise(p.Check, p.Name);

            if (typed is CustomCheck cc)
            {
                if (defaultValue.IsNull) continue;
                bool ok = await evaluator.EvaluateAsBooleanAsync(cc.Expr, frame, cancellationToken).ConfigureAwait(false);
                if (!ok)
                {
                    throw new FunctionArgumentException(
                        contextLabel,
                        $"default value for parameter '@{p.Name}' violates CHECK constraint.");
                }
                continue;
            }

            string? error = typed.Validate(defaultValue);
            if (error is not null)
            {
                throw new FunctionArgumentException(
                    contextLabel,
                    $"default value for parameter '@{p.Name}' violates CHECK: {error}");
            }
        }
    }

    /// <summary>
    /// Lifts the four UI-facing fields on a <see cref="UdfParameter"/>
    /// (<c>Check</c>, <c>Step</c>, <c>Unit</c>, <c>Description</c>) into a
    /// <see cref="ParameterMetadata"/> record, canonicalising the raw
    /// CHECK expression through <see cref="ParameterCheckWalker"/> so the
    /// catalog surfaces a typed constraint shape. Returns <see langword="null"/>
    /// when no field is set — keeps the registered <see cref="ParameterSpec"/>
    /// clean for parameters without any declared hints.
    /// </summary>
    private static ParameterMetadata? BuildParameterMetadata(UdfParameter p)
    {
        if (p.Check is null && p.Step is null && p.Unit is null && p.Description is null)
        {
            return null;
        }
        ParameterCheck? check = p.Check is null
            ? null
            : ParameterCheckWalker.Canonicalise(p.Check, p.Name);
        return new ParameterMetadata(
            Check: check,
            Step: p.Step,
            Unit: p.Unit,
            Description: p.Description);
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
                    $"{contextLabel}: parameter '{p.Name}' has no default but follows a parameter " +
                    "with a default. Defaults must be contiguous at the end of the parameter list.");
            }
        }
    }
}

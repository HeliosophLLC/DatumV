using System.Runtime.InteropServices;

using DatumIngest.Catalog;
using DatumIngest.Catalog.Registries;
using DatumIngest.Inference;
using DatumIngest.Model;
using DatumIngest.Models;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// End-to-end tests for <c>CREATE MODEL</c> / <c>DROP MODEL</c> through
/// <see cref="TableCatalog.PlanAsync(string)"/>. Stops at registry state
/// — the actual <c>infer()</c> dispatch path is covered separately
/// once Phase 3b lands.
/// </summary>
/// <remarks>
/// Uses a stub <see cref="IInferenceDispatcher"/> so the tests don't
/// depend on ONNX Runtime being available — every CREATE MODEL still
/// goes through the registrar's USING-resolution + file-existence checks
/// (a real on-disk fixture is used) but the "load" returns a tracked
/// fake session that records its own disposal.
/// </remarks>
public sealed class ModelRegistrationTests : ServiceTestBase
{
    private readonly string _modelFile;
    private readonly string _absoluteUsingPath;

    public ModelRegistrationTests()
    {
        // A real file on disk so ResolveUsingPath's File.Exists check passes.
        // Contents don't matter — the stub dispatcher never reads them.
        _modelFile = Path.Combine(Path.GetTempPath(),
            $"datum-test-model-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(_modelFile, [0]);
        _absoluteUsingPath = "file://" + _modelFile;
    }

    public override void Dispose()
    {
        if (File.Exists(_modelFile))
        {
            File.Delete(_modelFile);
        }
        base.Dispose();
    }

    private TableCatalog CreateCatalogWithDispatcher(out StubDispatcher dispatcher)
    {
        TableCatalog catalog = CreateCatalog();
        dispatcher = new StubDispatcher();
        catalog.InferenceDispatcher = dispatcher;
        return catalog;
    }

    private string Ddl(string body) =>
        $"CREATE MODEL classify(x INT32) RETURNS INT32 USING '{_absoluteUsingPath}' " +
        $"AS BEGIN {body} END";

    // ───────────────────── CREATE MODEL ─────────────────────

    [Fact]
    public void CreateModel_RegistersInDeclaredModels()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);

        catalog.Plan(Ddl("RETURN x"));

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out ModelDescriptor? descriptor));
        Assert.Equal("classify", descriptor!.Name);
        Assert.Equal("models", descriptor.SchemaName);
        Assert.Equal("INT32", descriptor.ReturnTypeName);
        Assert.Equal(_absoluteUsingPath, descriptor.UsingPath);
        // BoundSessions now exposes alias keys; the actual ONNX load is
        // lazy (deferred to the body's first infer() call), so the
        // dispatcher hasn't been touched yet at registration time.
        Assert.Single(descriptor.BoundSessions.Keys);
        Assert.Equal(0, dispatcher.LoadCallCount);
    }

    [Fact]
    public void CreateModel_RegistersScalarAdapter()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(Ddl("RETURN x"));

        // Adapter lands at `models.classify` so SELECT models.classify(...)
        // dispatches to the body via the standard scalar pipeline.
        Assert.NotNull(catalog.Functions.TryGetScalar(
            new QualifiedName("models", "classify")));
    }

    [Fact]
    public void CreateModel_ExplicitModelsSchema_AcceptedAndNormalized()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL models.classify(x INT32) RETURNS INT32 " +
            $"USING '{_absoluteUsingPath}' AS BEGIN RETURN x END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out _));
    }

    [Fact]
    public void CreateModel_NonModelsSchema_Throws()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan(
                $"CREATE MODEL public.classify(x INT32) RETURNS INT32 " +
                $"USING '{_absoluteUsingPath}' AS BEGIN RETURN x END"));
        Assert.Contains("'models' schema", ex.Message);
    }

    [Fact]
    public void CreateModel_NoDispatcherWired_Throws()
    {
        // Catalog without InferenceDispatcher — CREATE MODEL must surface
        // a clear "wire a dispatcher first" error rather than NRE.
        TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan(Ddl("RETURN x")));
        Assert.Contains("InferenceDispatcher", ex.Message);
    }

    [Fact]
    public void CreateModel_FileMissing_ThrowsFileNotFound()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        string bogus = "file://" + Path.Combine(Path.GetTempPath(),
            $"datum-test-missing-{Guid.NewGuid():N}.onnx");

        Assert.Throws<FileNotFoundException>(() => catalog.Plan(
            $"CREATE MODEL classify(x INT32) RETURNS INT32 " +
            $"USING '{bogus}' AS BEGIN RETURN x END"));
    }

    [Fact]
    public void CreateModel_RelativePath_NoModelCatalog_Throws()
    {
        // Relative paths require a wired Models (ModelCatalog) so the
        // registrar can resolve against ModelDirectory. Without one we
        // expect a clean error pointing at the fix.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan(
                "CREATE MODEL classify(x INT32) RETURNS INT32 " +
                "USING 'classify.onnx' AS BEGIN RETURN x END"));
        Assert.Contains("ModelCatalog", ex.Message);
    }

    [Fact]
    public void CreateModel_DuplicateName_Throws()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(Ddl("RETURN x"));

        Assert.ThrowsAny<InvalidOperationException>(
            () => catalog.Plan(Ddl("RETURN x + 1")));
    }

    [Fact]
    public async Task CreateModel_OrReplace_ReplacesAndDisposesOldSessions()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);

        catalog.Plan(Ddl("RETURN x"));
        // Sessions are lazy now — force the first load by resolving the
        // alias through the descriptor's BoundSessions accessor. This is
        // what the body's first infer() call does at runtime.
        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out ModelDescriptor? firstDescriptor));
        _ = await firstDescriptor!.BoundSessions.ResolveAsync("default", default);
        StubSession firstSession = dispatcher.LastSession!;
        Assert.False(firstSession.Disposed);

        catalog.Plan(
            $"CREATE OR REPLACE MODEL classify(x INT32) RETURNS INT32 " +
            $"USING '{_absoluteUsingPath}' AS BEGIN RETURN x + 1 END");

        // Old session must be disposed (it was loaded). New session is
        // lazy — verify by resolving and confirming it's distinct + live.
        Assert.True(firstSession.Disposed);
        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out ModelDescriptor? secondDescriptor));
        _ = await secondDescriptor!.BoundSessions.ResolveAsync("default", default);
        Assert.NotSame(firstSession, dispatcher.LastSession);
        Assert.False(dispatcher.LastSession!.Disposed);
        Assert.Equal(2, dispatcher.LoadCallCount);
    }

    [Fact]
    public async Task CreateModel_IfNotExists_NoOpWhenAlreadyRegistered()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);

        catalog.Plan(Ddl("RETURN x"));
        // Force the lazy session to load so we have a StubSession to assert on.
        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out ModelDescriptor? descriptor));
        _ = await descriptor!.BoundSessions.ResolveAsync("default", default);
        StubSession firstSession = dispatcher.LastSession!;

        catalog.Plan(
            $"CREATE MODEL IF NOT EXISTS classify(x INT32) RETURNS INT32 " +
            $"USING '{_absoluteUsingPath}' AS BEGIN RETURN x + 1 END");

        // No second load happened — the existing descriptor wins and
        // no new session was bound (the original load counted as 1).
        Assert.Equal(1, dispatcher.LoadCallCount);
        Assert.False(firstSession.Disposed);
    }

    // ───────────────────── DROP MODEL ─────────────────────

    [Fact]
    public async Task DropModel_RemovesFromRegistryAndDisposesSessions()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);

        catalog.Plan(Ddl("RETURN x"));
        // Force the lazy session to load — DROP only disposes sessions
        // that were actually allocated; this test verifies the disposal
        // path for a model that has been used.
        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out ModelDescriptor? descriptor));
        _ = await descriptor!.BoundSessions.ResolveAsync("default", default);
        StubSession session = dispatcher.LastSession!;

        catalog.Plan("DROP MODEL classify");

        Assert.False(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out _));
        Assert.Null(catalog.Functions.TryGetScalar(
            new QualifiedName("models", "classify")));
        Assert.True(session.Disposed);
    }

    [Fact]
    public void DropModel_ExplicitModelsSchema_Accepted()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(Ddl("RETURN x"));
        catalog.Plan("DROP MODEL models.classify");

        Assert.False(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out _));
    }

    [Fact]
    public void DropModel_NonExistent_Throws()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan("DROP MODEL never_registered"));
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void DropModel_IfExists_NoOpWhenAbsent()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        // Should not throw.
        catalog.Plan("DROP MODEL IF EXISTS never_registered");
    }

    // ───────────────────── Scalar dispatch into SQL-defined models ─────────────────────
    //
    // The hoister-blocks-SQL-models gap: when a query references
    // `models.<sql_defined>()`, ModelInvocationHoister sees the `models.`
    // schema and tries to find the name in `ModelCatalog` (built-in
    // registry). It used to throw outright when the lookup missed, leaving
    // SQL-defined models unreachable through SELECT even though their
    // ProceduralModelFunction adapter sits in the FunctionRegistry. Option
    // A makes the hoister tolerant: when the name isn't a built-in, leave
    // the call alone and let the scalar pipeline route it through the
    // adapter. We lose CSE + batched dispatch for SQL-defined models in
    // exchange for actually being able to call them; revisit when batching
    // becomes a measured need (see the project memo's follow-up).

    [Fact]
    public async Task SqlDefinedModel_DispatchesViaScalarPipeline_WhenModelCatalogWired()
    {
        // Wires the engine's built-in ModelCatalog (the hoister is a no-op
        // when Models is null, which would mask the hoister bug). The SQL-
        // defined model is NOT registered in that catalog; the hoister
        // should leave the call site alone and the scalar pipeline should
        // dispatch it through ProceduralModelFunction.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        // Add an in-memory source row so the query has something to scan.
        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"], [new object?[] { 5 }]));

        catalog.Plan(
            $"CREATE MODEL square(x INT32) RETURNS INT32 USING '{_absoluteUsingPath}' " +
            $"AS BEGIN RETURN x * x END");

        IQueryPlan plan = catalog.Plan("SELECT models.square(v) FROM data");

        List<DataValue> values = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0]);
            }
        }
        Assert.Single(values);
        Assert.Equal(DataKind.Int32, values[0].Kind);
        Assert.Equal(25, values[0].AsInt32());
    }

    [Fact]
    public void DropModel_NonModelsSchema_Throws()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan("DROP MODEL public.classify"));
        Assert.Contains("'models' schema", ex.Message);
    }

    // ───────────────────── IMPLEMENTS contract enforcement ─────────────────────

    [Fact]
    public void CreateModel_Implements_MatchingSignature_RegistersWithTaskName()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL talk(prompt String) RETURNS String "
            + $"IMPLEMENTS TextGenerator "
            + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN prompt END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "talk"), out ModelDescriptor? descriptor));
        Assert.Equal("TextGenerator", descriptor!.ImplementsTaskName);
    }

    [Fact]
    public void CreateModel_Implements_OmittedClause_HasNullTaskName()
    {
        // IMPLEMENTS is optional — without it, the descriptor's task name
        // is null and signature enforcement is skipped.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL opaque(x INT32) RETURNS Float32[] "
            + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN [CAST(1.0 AS Float32)] END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "opaque"), out ModelDescriptor? descriptor));
        Assert.Null(descriptor!.ImplementsTaskName);
    }

    [Fact]
    public void CreateModel_Implements_MismatchedReturn_ThrowsWithBothShapes()
    {
        // TextGenerator requires (String) → String; this declares
        // (String) → Int32. Error message should print both shapes.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        DatumIngest.Execution.QueryPlanException ex = Assert.Throws<DatumIngest.Execution.QueryPlanException>(
            () => catalog.Plan(
                $"CREATE MODEL bad_return(prompt String) RETURNS Int32 "
                + $"IMPLEMENTS TextGenerator "
                + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN 42 END"));
        Assert.Contains("TextGenerator", ex.Message);
        Assert.Contains("String", ex.Message); // contract's expected return
        Assert.Contains("Int32", ex.Message);  // model's actual return
    }

    [Fact]
    public void CreateModel_Implements_MismatchedParamArity_Throws()
    {
        // TextGenerator requires one parameter (String); this declares two.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        DatumIngest.Execution.QueryPlanException ex = Assert.Throws<DatumIngest.Execution.QueryPlanException>(
            () => catalog.Plan(
                $"CREATE MODEL bad_arity(p String, n Int32) RETURNS String "
                + $"IMPLEMENTS TextGenerator "
                + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN p END"));
        Assert.Contains("TextGenerator", ex.Message);
        Assert.Contains("parameter", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateModel_Implements_RequiredParamsMatch_OptionalParamsAdditive_Registers()
    {
        // TextGenerator requires 1 param (String). Model declares 1 required
        // + 1 optional (Float32 with default) — the contract still matches
        // on the required slot; optional params are additive runtime knobs
        // (e.g. temperature, top_p) that aren't part of the contract.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL gen_with_knob(prompt String, temperature Float32 = CAST(0.7 AS Float32)) RETURNS String "
            + $"IMPLEMENTS TextGenerator "
            + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN prompt END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "gen_with_knob"), out ModelDescriptor? descriptor));
        Assert.Equal("TextGenerator", descriptor!.ImplementsTaskName);
        Assert.Equal(2, descriptor.Parameters.Count);
        Assert.Null(descriptor.Parameters[0].Default);
        Assert.NotNull(descriptor.Parameters[1].Default);
    }

    [Fact]
    public void CreateModel_Implements_OptionalParamInsteadOfRequired_Throws()
    {
        // TextGenerator requires 1 required param. If the model marks its
        // single param as optional (with a default), the contract is
        // violated — required-param-count (0) ≠ contract-input-count (1).
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        DatumIngest.Execution.QueryPlanException ex = Assert.Throws<DatumIngest.Execution.QueryPlanException>(
            () => catalog.Plan(
                $"CREATE MODEL bad_optional(prompt String = 'default') RETURNS String "
                + $"IMPLEMENTS TextGenerator "
                + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN prompt END"));
        Assert.Contains("TextGenerator", ex.Message);
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateModel_DefaultInsideCheckRange_RegistersCleanly()
    {
        // Sanity check: a default value that satisfies the CHECK lets
        // CREATE MODEL succeed normally. Without this, the failing-default
        // test below wouldn't prove anything about the new behaviour.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL clamp_ok(t Float32 = CAST(0.5 AS Float32) "
            + $"CHECK (t BETWEEN 0.0 AND 1.0)) RETURNS Float32 "
            + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN t END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "clamp_ok"), out ModelDescriptor? _));
    }

    [Fact]
    public void CreateModel_DefaultViolatesCheck_FailsRegistration()
    {
        // The default 1.5 is outside [0, 1]; the new registration-time
        // pre-flight should catch this and reject CREATE MODEL with a
        // recognisable error mentioning both the parameter and the bounds.
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);

        // FunctionArgumentException is an ExecutionException subclass — same
        // boundary semantics as the kind validation that fires before it.
        DatumIngest.Functions.FunctionArgumentException ex = Assert.Throws<DatumIngest.Functions.FunctionArgumentException>(
            () => catalog.Plan(
                $"CREATE MODEL bad_default(t Float32 = CAST(1.5 AS Float32) "
                + $"CHECK (t BETWEEN 0.0 AND 1.0)) RETURNS Float32 "
                + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN t END"));

        Assert.Contains("bad_default", ex.Message);
        Assert.Contains("@t", ex.Message);
        Assert.Contains("CHECK", ex.Message);
        // ONNX dispatcher should NOT have been hit — pre-flight runs first.
        Assert.Equal(0, dispatcher.LoadCallCount);
        // Registry should not have been touched either.
        Assert.False(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "bad_default"), out _));
    }

    [Fact]
    public void CreateModel_CustomCheckDefaultViolation_FailsRegistration()
    {
        // `x = 7 OR x = 42` falls into CustomCheck (non-canonical shape).
        // Default 3 doesn't satisfy it; registration pre-flight should
        // evaluate the predicate against the default and reject CREATE.
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);

        DatumIngest.Functions.FunctionArgumentException ex = Assert.Throws<DatumIngest.Functions.FunctionArgumentException>(
            () => catalog.Plan(
                $"CREATE MODEL bad_custom(x Int32 = 3 CHECK (x = 7 OR x = 42)) RETURNS Int32 "
                + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN x END"));

        Assert.Contains("bad_custom", ex.Message);
        Assert.Contains("@x", ex.Message);
        Assert.Equal(0, dispatcher.LoadCallCount);
    }

    [Fact]
    public void CreateModel_CustomCheckDefaultSatisfies_RegistersCleanly()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL good_custom(x Int32 = 7 CHECK (x = 7 OR x = 42)) RETURNS Int32 "
            + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN x END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "good_custom"), out _));
    }

    [Fact]
    public void CreateModel_DefaultPassesAndNoCheck_StillRegisters()
    {
        // A parameter with a default but no CHECK skips the pre-flight
        // entirely — nothing to enforce. Guards against the validator
        // accidentally rejecting unconstrained-default parameters.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL no_check(t Float32 = CAST(42.0 AS Float32)) RETURNS Float32 "
            + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN t END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "no_check"), out _));
    }

    [Fact]
    public void CreateModel_Implements_MismatchedParamKind_Throws()
    {
        // TextGenerator requires String; this declares Int32 input.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        DatumIngest.Execution.QueryPlanException ex = Assert.Throws<DatumIngest.Execution.QueryPlanException>(
            () => catalog.Plan(
                $"CREATE MODEL bad_input(n Int32) RETURNS String "
                + $"IMPLEMENTS TextGenerator "
                + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN CAST(n AS String) END"));
        Assert.Contains("TextGenerator", ex.Message);
        Assert.Contains("String", ex.Message);
    }

    [Fact]
    public void CreateModel_Implements_UnknownTask_ThrowsWithVocabPointer()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        DatumIngest.Execution.QueryPlanException ex = Assert.Throws<DatumIngest.Execution.QueryPlanException>(
            () => catalog.Plan(
                $"CREATE MODEL bogus(x Int32) RETURNS Int32 "
                + $"IMPLEMENTS NonExistentTask "
                + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN x END"));
        Assert.Contains("NonExistentTask", ex.Message);
        Assert.Contains("system.task_contracts", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateModel_Implements_NamedTypeReturn_Matches()
    {
        // TextClassifier requires (String) → ScoredClass. The named-type
        // resolution flows through TypeAnnotationResolver's static lookup
        // — `RETURNS ScoredClass` resolves to (DataKind.Struct, IsArray=false,
        // namedTypeName="ScoredClass"), which matches the contract.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL classify_text(t String) RETURNS ScoredClass "
            + $"IMPLEMENTS TextClassifier "
            + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN {{class: 1, score: CAST(0.5 AS Float32)}} END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify_text"), out ModelDescriptor? descriptor));
        Assert.Equal("TextClassifier", descriptor!.ImplementsTaskName);
        Assert.Equal("ScoredClass", descriptor.ReturnTypeName);
    }

    // ───────────────────── Pass A body-walk RETURN typecheck ─────────────────────

    [Fact]
    public void CreateModel_PassA_StructLiteralReturn_MatchingFields_Registers()
    {
        // ScoredClass = Struct<class: Int32, score: Float32>. RETURN with
        // matching field names succeeds even when the literal expressions
        // are wrapped in CASTs (which is the typical user shape).
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL classify_text(t String) RETURNS ScoredClass "
            + $"USING '{_absoluteUsingPath}' "
            + $"AS BEGIN RETURN {{class: CAST(1 AS Int32), score: CAST(0.5 AS Float32)}} END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify_text"), out _));
    }

    [Fact]
    public void CreateModel_PassA_StructLiteralReturn_WrongFieldName_Throws()
    {
        // ScoredClass has fields (class, score). This literal has (label,
        // score) — same arity but wrong name. Mismatch should fire at
        // CREATE time with both sets printed.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        DatumIngest.Execution.QueryPlanException ex = Assert.Throws<DatumIngest.Execution.QueryPlanException>(
            () => catalog.Plan(
                $"CREATE MODEL wrong_field(t String) RETURNS ScoredClass "
                + $"USING '{_absoluteUsingPath}' "
                + $"AS BEGIN RETURN {{label: 'cat', score: CAST(0.5 AS Float32)}} END"));
        Assert.Contains("ScoredClass", ex.Message);
        Assert.Contains("class", ex.Message);   // Expected field listed
        Assert.Contains("label", ex.Message);   // Actual field listed
    }

    [Fact]
    public void CreateModel_PassA_StructLiteralReturn_MissingField_Throws()
    {
        // ScoredClass has fields (class, score). This literal has only
        // (class) — score is missing. Mismatch should fire.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        DatumIngest.Execution.QueryPlanException ex = Assert.Throws<DatumIngest.Execution.QueryPlanException>(
            () => catalog.Plan(
                $"CREATE MODEL missing_field(t String) RETURNS ScoredClass "
                + $"USING '{_absoluteUsingPath}' "
                + $"AS BEGIN RETURN {{class: CAST(1 AS Int32)}} END"));
        Assert.Contains("ScoredClass", ex.Message);
        Assert.Contains("score", ex.Message);
    }

    [Fact]
    public void CreateModel_PassA_StructLiteralReturn_ExtraField_Throws()
    {
        // ScoredClass has fields (class, score). This literal has an extra
        // `confidence` field. Pass A treats this as a mismatch — exact
        // contract, not lenient.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        DatumIngest.Execution.QueryPlanException ex = Assert.Throws<DatumIngest.Execution.QueryPlanException>(
            () => catalog.Plan(
                $"CREATE MODEL extra_field(t String) RETURNS ScoredClass "
                + $"USING '{_absoluteUsingPath}' "
                + $"AS BEGIN RETURN {{class: CAST(1 AS Int32), score: CAST(0.5 AS Float32), confidence: CAST(0.9 AS Float32)}} END"));
        Assert.Contains("ScoredClass", ex.Message);
        Assert.Contains("confidence", ex.Message);
    }

    [Fact]
    public void CreateModel_PassA_StructLiteralReturn_FieldsReordered_Accepts()
    {
        // Name-aware, order-insensitive matching (locked-in design decision).
        // Reordered fields should still pass.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL reorder(t String) RETURNS ScoredClass "
            + $"USING '{_absoluteUsingPath}' "
            + $"AS BEGIN RETURN {{score: CAST(0.5 AS Float32), class: CAST(1 AS Int32)}} END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "reorder"), out _));
    }

    [Fact]
    public void CreateModel_PassA_PrimitiveReturn_SkipsBodyCheck()
    {
        // Pass A only checks named-struct returns. RETURNS Float32 with a
        // body that returns a struct literal is wrong, but Pass A doesn't
        // see it — that's Pass B's territory (full type inference).
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        // The cast inside the BEGIN/END body forces a Float32 return —
        // this is well-formed, just exercising the "Pass A skips primitive
        // returns" branch without surfacing an unrelated parse / cast error.
        catalog.Plan(
            $"CREATE MODEL primitive_return(x Float32) RETURNS Float32 "
            + $"USING '{_absoluteUsingPath}' "
            + $"AS BEGIN RETURN x END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "primitive_return"), out _));
    }

    [Fact]
    public void CreateModel_PassA_NonStructLiteralReturn_SkipsBodyCheck()
    {
        // Pass A is struct-literal-only. RETURN <function-call> doesn't
        // fire Pass A (Pass B handles it). Use a no-op variable-pass body
        // that's well-formed against the named-struct return: declare a
        // typed null of the named type, then return it.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL via_decl(t String) RETURNS ScoredClass "
            + $"USING '{_absoluteUsingPath}' "
            + $"AS BEGIN DECLARE r ScoredClass; RETURN r END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "via_decl"), out _));
    }

    // ───────────────────── Pass B body-walk RETURN typecheck ─────────────────────

    [Fact]
    public void CreateModel_PassB_VariableRefReturn_WrongDeclareType_Throws()
    {
        // RETURNS ScoredClass + DECLARE r BoundingBox; RETURN r.
        // The DECLARE's annotated type drives Pass B: BoundingBox ≠
        // ScoredClass, so the registrar should throw at CREATE time.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        DatumIngest.Execution.QueryPlanException ex = Assert.Throws<DatumIngest.Execution.QueryPlanException>(
            () => catalog.Plan(
                $"CREATE MODEL var_wrong_type(t String) RETURNS ScoredClass "
                + $"USING '{_absoluteUsingPath}' "
                + $"AS BEGIN DECLARE r BoundingBox; RETURN r END"));
        Assert.Contains("ScoredClass", ex.Message);
        Assert.Contains("BoundingBox", ex.Message);
    }

    [Fact]
    public void CreateModel_PassB_VariableRefReturn_UnknownVariable_Skips()
    {
        // RETURN <unknown identifier> — Pass B can't resolve a type
        // annotation, so it skips silently rather than false-positiving.
        // Runtime evaluation will throw a clean "unbound variable" error
        // at first row scan.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL var_unknown(t String) RETURNS ScoredClass "
            + $"USING '{_absoluteUsingPath}' "
            + $"AS BEGIN RETURN nonexistent END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "var_unknown"), out _));
    }

    [Fact]
    public void CreateModel_PassB_UdfCallReturn_MatchingNamedType_Registers()
    {
        // UDF whose declared return is ScoredClass; model's RETURNS is
        // ScoredClass; body RETURNs the UDF call. Names match → passes.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            "CREATE FUNCTION make_class() RETURNS ScoredClass AS BEGIN "
            + "RETURN {class: CAST(1 AS Int32), score: CAST(0.5 AS Float32)} END");

        catalog.Plan(
            $"CREATE MODEL via_udf(t String) RETURNS ScoredClass "
            + $"USING '{_absoluteUsingPath}' "
            + $"AS BEGIN RETURN make_class() END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "via_udf"), out _));
    }

    [Fact]
    public void CreateModel_PassB_UdfCallReturn_WrongNamedType_Throws()
    {
        // UDF returns BoundingBox; model RETURNS ScoredClass; body
        // RETURNs the UDF. Mismatch → throws at CREATE.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            "CREATE FUNCTION make_bbox() RETURNS BoundingBox AS BEGIN "
            + "RETURN {x: CAST(0 AS Float32), y: CAST(0 AS Float32), "
            + "w: CAST(0 AS Float32), h: CAST(0 AS Float32)} END");

        DatumIngest.Execution.QueryPlanException ex = Assert.Throws<DatumIngest.Execution.QueryPlanException>(
            () => catalog.Plan(
                $"CREATE MODEL via_udf_wrong(t String) RETURNS ScoredClass "
                + $"USING '{_absoluteUsingPath}' "
                + $"AS BEGIN RETURN make_bbox() END"));
        Assert.Contains("ScoredClass", ex.Message);
        Assert.Contains("BoundingBox", ex.Message);
    }

    [Fact]
    public void CreateModel_PassB_ModelCallReturn_MatchingNamedType_Registers()
    {
        // Model B returns ScoredClass; Model A RETURNs models.B(...)
        // and declares RETURNS ScoredClass. Names match → passes.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL inner_class(t String) RETURNS ScoredClass "
            + $"USING '{_absoluteUsingPath}' "
            + $"AS BEGIN RETURN {{class: CAST(1 AS Int32), score: CAST(0.5 AS Float32)}} END");

        catalog.Plan(
            $"CREATE MODEL outer_class(t String) RETURNS ScoredClass "
            + $"USING '{_absoluteUsingPath}' "
            + $"AS BEGIN RETURN models.inner_class(t) END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "outer_class"), out _));
    }

    [Fact]
    public void CreateModel_PassB_ModelCallReturn_WrongNamedType_Throws()
    {
        // Model B returns BoundingBox; Model A RETURNS ScoredClass and
        // RETURNs models.B(...). Mismatch → throws.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL inner_bbox(t String) RETURNS BoundingBox "
            + $"USING '{_absoluteUsingPath}' "
            + $"AS BEGIN RETURN {{x: CAST(0 AS Float32), y: CAST(0 AS Float32), "
            + $"w: CAST(0 AS Float32), h: CAST(0 AS Float32)}} END");

        DatumIngest.Execution.QueryPlanException ex = Assert.Throws<DatumIngest.Execution.QueryPlanException>(
            () => catalog.Plan(
                $"CREATE MODEL outer_wrong(t String) RETURNS ScoredClass "
                + $"USING '{_absoluteUsingPath}' "
                + $"AS BEGIN RETURN models.inner_bbox(t) END"));
        Assert.Contains("ScoredClass", ex.Message);
        Assert.Contains("BoundingBox", ex.Message);
    }

    [Fact]
    public void CreateModel_PassB_ArrayLiteralReturn_MatchingElementFields_Registers()
    {
        // RETURNS Array<RegionScore> + RETURN [{bbox: ..., score: ...}, ...].
        // Pass B recurses into each element and applies the Pass A
        // field-name check against the element's named-struct type.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL detect(t String) RETURNS Array<RegionScore> "
            + $"USING '{_absoluteUsingPath}' "
            + $"AS BEGIN RETURN ["
            + $"{{bbox: {{x: CAST(0 AS Float32), y: CAST(0 AS Float32), "
            + $"w: CAST(1 AS Float32), h: CAST(1 AS Float32)}}, "
            + $"score: CAST(0.9 AS Float32)}}"
            + $"] END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "detect"), out _));
    }

    [Fact]
    public void CreateModel_PassB_ArrayLiteralReturn_WrongElementFields_Throws()
    {
        // RETURNS Array<RegionScore> + RETURN [{wrong: 1}].
        // Element field-names don't match RegionScore (bbox, score) →
        // throws at CREATE.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        DatumIngest.Execution.QueryPlanException ex = Assert.Throws<DatumIngest.Execution.QueryPlanException>(
            () => catalog.Plan(
                $"CREATE MODEL detect_wrong(t String) RETURNS Array<RegionScore> "
                + $"USING '{_absoluteUsingPath}' "
                + $"AS BEGIN RETURN [{{wrong: CAST(1 AS Int32)}}] END"));
        Assert.Contains("RegionScore", ex.Message);
        Assert.Contains("wrong", ex.Message);
    }

    [Fact]
    public void CreateModel_PassB_ArrayLiteralReturn_Empty_Skips()
    {
        // Empty array literal — Pass B can't infer per-element shape from
        // an empty array, so it skips. (Runtime returns an empty Array<T>
        // which the executor will coerce to the declared type.)
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(
            $"CREATE MODEL detect_empty(t String) RETURNS Array<RegionScore> "
            + $"USING '{_absoluteUsingPath}' "
            + $"AS BEGIN RETURN [] END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "detect_empty"), out _));
    }

    [Fact]
    public void CreateModel_PassB_BuiltinCallReturn_ArrayVsScalar_Throws()
    {
        // RETURNS ScoredClass (scalar struct) + RETURN softmax([...])
        // which returns Array<Float32>. Pass B's arity/array-ness check
        // against the built-in's matched signature catches the
        // array-vs-scalar mismatch.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        DatumIngest.Execution.QueryPlanException ex = Assert.Throws<DatumIngest.Execution.QueryPlanException>(
            () => catalog.Plan(
                $"CREATE MODEL builtin_arr_scalar(t String) RETURNS ScoredClass "
                + $"USING '{_absoluteUsingPath}' "
                + $"AS BEGIN RETURN softmax([CAST(1.0 AS Float32), CAST(2.0 AS Float32)]) END"));
        Assert.Contains("ScoredClass", ex.Message);
    }

    // ───────────────────── infer() runtime bridge (Phase 3b) ─────────────────────

    [Fact]
    public void Infer_OutsideModelBody_ThrowsAtPlanTime()
    {
        // The plan-time gate should refuse infer() in any non-CREATE-MODEL
        // context — the runtime "no CurrentModel frame" guard stays as a
        // backstop, but users should hit the friendly error before any
        // rows are scanned.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };
        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"], [new object?[] { 1.0f }]));

        Exception ex = Assert.ThrowsAny<Exception>(
            () => catalog.Plan("SELECT infer(v) FROM data"));
        Assert.Contains("CREATE", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MODEL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Infer_InNestedSubquery_ThrowsAtPlanTime()
    {
        // Lock that the gate isn't tricked by nesting — a subquery's
        // expression resolver still walks function calls.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };
        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"], [new object?[] { 1.0f }]));

        Assert.ThrowsAny<Exception>(
            () => catalog.Plan("SELECT (SELECT infer(v) FROM data) FROM data"));
    }

    [Fact]
    public async Task Infer_FromModelBody_RoundTripsThroughBoundSession()
    {
        // The smallest viable infer() shape: single Float32 input, single
        // Float32 output. Stub session doubles its input. The model body
        // is `RETURN infer(x)`, so calling the model with 3.0 should
        // surface 6.0 — proving infer() resolved frame.CurrentModel,
        // pulled the bound session, marshalled the scalar into a tensor,
        // and unwrapped the output back to a scalar ValueRef.
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        dispatcher.NextSession = StubSession.Float32Doubler();

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"], [new object?[] { 3.0f }]));

        catalog.Plan(
            $"CREATE MODEL doubler(x Float32) RETURNS Float32 USING '{_absoluteUsingPath}' " +
            $"AS BEGIN RETURN infer(x) END");

        IQueryPlan plan = catalog.Plan("SELECT models.doubler(v) FROM data");

        List<DataValue> values = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0]);
            }
        }
        Assert.Single(values);
        Assert.Equal(DataKind.Float32, values[0].Kind);
        Assert.Equal(6.0f, values[0].AsFloat32());
    }

    [Fact]
    public async Task Infer_RankTwoOutput_ProducesMultiDimValue()
    {
        // ONNX-tensor outputs with rank ≥ 2 surface as multi-dim DataValues so
        // SQL bracket-access (m[y, x]) and array_shape() see the declared shape.
        // The stub emits a 2×3 Float32 matrix; the model returns it directly via
        // `RETURN infer(x)` and the test asserts the resulting value's
        // IsMultiDim / Ndim / GetShape / element values.
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        dispatcher.NextSession = StubSession.Float32Matrix2x3();

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"], [new object?[] { 10.0f }]));

        catalog.Plan(
            $"CREATE MODEL gen_matrix(x Float32) RETURNS Array<Float32> USING '{_absoluteUsingPath}' " +
            $"AS BEGIN RETURN infer(x) END");

        IQueryPlan plan = catalog.Plan("SELECT models.gen_matrix(v) AS m FROM data");

        // Multi-dim values are arena-backed; read shape/elements within the foreach
        // before the batch arena is released, then capture the materialized values.
        int rowsSeen = 0;
        int[]? shape = null;
        float[]? elements = null;
        bool isMultiDim = false;
        int ndim = 0;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                DataValue m = batch[i][0];
                isMultiDim = m.IsMultiDim;
                ndim = m.Ndim;
                shape = m.GetShape(batch.Arena).ToArray();
                elements = m.AsArraySpan<float>(batch.Arena).ToArray();
                rowsSeen++;
            }
        }
        Assert.Equal(1, rowsSeen);
        Assert.True(isMultiDim);
        Assert.Equal(2, ndim);
        Assert.Equal([2, 3], shape!);
        Assert.Equal([10f, 11f, 12f, 13f, 14f, 15f], elements!);
    }

    [Fact]
    public async Task Infer_RankTwoOutput_BracketAccessReadsSingleElement()
    {
        // End-to-end: bracket-syntax access against the multi-dim infer output
        // (m[y, x]) flows through the evaluator's row-major flat-offset path.
        // Asserts the depth-map-style chain that motivated multi-dim function
        // outputs: SQL sees a single Float32 element extracted from a 2-D
        // ONNX result without having to manually flatten / unflatten.
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        dispatcher.NextSession = StubSession.Float32Matrix2x3();

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"], [new object?[] { 10.0f }]));

        catalog.Plan(
            $"CREATE MODEL gen_matrix(x Float32) RETURNS Array<Float32> USING '{_absoluteUsingPath}' " +
            $"AS BEGIN RETURN infer(x) END");

        // Row-major: [10, 11, 12, 13, 14, 15] in a 2×3 shape (1-based) →
        //   m[1, 1] = 10, m[1, 3] = 12, m[2, 1] = 13, m[2, 3] = 15
        IQueryPlan plan = catalog.Plan(
            "SELECT models.gen_matrix(v) AS m," +
            "       models.gen_matrix(v)[1, 1] AS a," +
            "       models.gen_matrix(v)[1, 3] AS b," +
            "       models.gen_matrix(v)[2, 1] AS c," +
            "       models.gen_matrix(v)[2, 3] AS d" +
            " FROM data");

        float a = 0, b = 0, c = 0, d = 0;
        int rowsSeen = 0;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                a = batch[i]["a"].AsFloat32();
                b = batch[i]["b"].AsFloat32();
                c = batch[i]["c"].AsFloat32();
                d = batch[i]["d"].AsFloat32();
                rowsSeen++;
            }
        }
        Assert.Equal(1, rowsSeen);
        Assert.Equal(10f, a);
        Assert.Equal(12f, b);
        Assert.Equal(13f, c);
        Assert.Equal(15f, d);
    }

    [Fact]
    public async Task Infer_RankTwoOutput_CardinalityAndArrayShapeWork()
    {
        // Regression coverage for the function-boundary side of multi-dim infer
        // outputs: cardinality() must return product(shape), array_shape() must
        // return the dim vector, array_ndims() must return ndim, and
        // array_length(arr, dim) must work per-dim — all against a value built
        // by infer() rather than a static-shape column.
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        dispatcher.NextSession = StubSession.Float32Matrix2x3();

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"], [new object?[] { 10.0f }]));

        catalog.Plan(
            $"CREATE MODEL gen_matrix(x Float32) RETURNS Array<Float32> USING '{_absoluteUsingPath}' " +
            $"AS BEGIN RETURN infer(x) END");

        IQueryPlan plan = catalog.Plan(
            "SELECT cardinality(models.gen_matrix(v))         AS total," +
            "       array_ndims(models.gen_matrix(v))         AS nd," +
            "       array_length(models.gen_matrix(v), 1)     AS d1," +
            "       array_length(models.gen_matrix(v), 2)     AS d2," +
            "       array_get(models.gen_matrix(v), 2, 3)     AS elem" +
            " FROM data");

        int total = 0, nd = 0, d1 = 0, d2 = 0;
        float elem = 0;
        int rowsSeen = 0;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                total = batch[i]["total"].AsInt32();
                nd = batch[i]["nd"].AsInt32();
                d1 = batch[i]["d1"].AsInt32();
                d2 = batch[i]["d2"].AsInt32();
                elem = batch[i]["elem"].AsFloat32();
                rowsSeen++;
            }
        }
        Assert.Equal(1, rowsSeen);
        Assert.Equal(6, total);     // product(2, 3)
        Assert.Equal(2, nd);
        Assert.Equal(2, d1);
        Assert.Equal(3, d2);
        Assert.Equal(15f, elem);    // m[2, 3] (1-based) = 10 + 5 = 15
    }

    [Fact]
    public async Task Infer_MultiInput_StructArg_FeedsSessionInputsByFieldName()
    {
        // Multi-input v1: pass a struct of tensors whose field names match
        // the session's input names. The stub session takes two Int64
        // scalars (input_a + input_b) and emits their sum as a Float32. The
        // body's `infer({input_a: a, input_b: b})` proves struct-arg
        // dispatch, name-based input resolution, and the no-explicit-shape
        // fast path (each input's [1] spec resolves without help).
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        dispatcher.NextSession = StubSession.MultiInputInt64Sum();

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["a", "b"], [new object?[] { 7L, 1L }]));

        catalog.Plan(
            $"CREATE MODEL adder(a Int64, b Int64) RETURNS Float32 USING '{_absoluteUsingPath}' "
            + $"AS BEGIN RETURN infer({{input_a: a, input_b: b}}) END");

        IQueryPlan plan = catalog.Plan("SELECT models.adder(a, b) FROM data");

        List<DataValue> values = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0]);
            }
        }
        Assert.Single(values);
        Assert.Equal(DataKind.Float32, values[0].Kind);
        Assert.Equal(8.0f, values[0].AsFloat32());
    }

    [Fact]
    public async Task Infer_MultiInput_StructArgWithExplicitShapes_RoutesParallelShapesByFieldName()
    {
        // Exercises the (Struct, Struct) 2-arg form — the canonical shape
        // for BERT-family embedding models where every input has multiple
        // dynamic dims and the per-input shape can't be inferred from a 1-d
        // length alone. The stub session declares both inputs as fully-
        // dynamic ([null, null]) so the dispatch is FORCED through the
        // explicit-shape path: a 1-arg struct call would throw at shape
        // resolution. The test passes only if each shape-struct field
        // routes to its corresponding session input by name.
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        dispatcher.NextSession = StubSession.MultiInputMaskedDotProduct();

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["a", "b"], [new object?[] { 7L, 1L }]));

        catalog.Plan(
            $"CREATE MODEL embed(a Int64, b Int64) RETURNS Float32 USING '{_absoluteUsingPath}' "
            + $"AS BEGIN RETURN infer("
            + $"{{input_ids: a, attention_mask: b}}, "
            + $"{{input_ids: [CAST(1 AS Int32), CAST(1 AS Int32)], "
            + $"attention_mask: [CAST(1 AS Int32), CAST(1 AS Int32)]}}) END");

        IQueryPlan plan = catalog.Plan("SELECT models.embed(a, b) FROM data");

        List<DataValue> values = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0]);
            }
        }
        Assert.Single(values);
        Assert.Equal(DataKind.Float32, values[0].Kind);
        Assert.Equal(7.0f, values[0].AsFloat32()); // 7L * 1L = 7
    }

    [Fact]
    public async Task InferOutputs_MultiOutput_ReturnsStructKeyedByOutputName()
    {
        // infer_outputs() surfaces ALL session outputs as a Struct keyed by
        // ONNX output name. The stub emits two Float32 arrays — "logits"
        // and "boxes" — and the body destructures both. Tests the typical
        // multi-output shape: detector with separate classification + box outputs.
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        dispatcher.NextSession = StubSession.MultiOutputDual();

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"], [new object?[] { 2.0f }]));

        // Body picks the SECOND output ('boxes') by name, proving the field
        // lookup uses the session's output names — not just the first slot.
        catalog.Plan(
            $"CREATE MODEL pick_boxes(x Float32) RETURNS Float32 USING '{_absoluteUsingPath}' "
            + "AS BEGIN "
            + "  DECLARE outputs Struct = infer_outputs(x); "
            + "  DECLARE boxes Float32[] = outputs['boxes']; "
            + "  RETURN boxes[1] "
            + "END");

        IQueryPlan plan = catalog.Plan("SELECT models.pick_boxes(v) FROM data");

        List<DataValue> values = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0]);
            }
        }
        Assert.Single(values);
        // Stub: boxes[1] (1-based, first element) = input * 10 = 20.0
        Assert.Equal(20.0f, values[0].AsFloat32());
    }

    [Fact]
    public async Task InferOutputs_PositionalAccess_PicksFirstOutput()
    {
        // outputs[1] grabs the first declared output via the struct's
        // positional-access path (1-based ordinal). Useful when the output
        // names are unstable across exports (PyTorch's numeric default
        // names like "1992" / "out_0").
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        dispatcher.NextSession = StubSession.MultiOutputDual();

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"], [new object?[] { 2.0f }]));

        catalog.Plan(
            $"CREATE MODEL pick_first(x Float32) RETURNS Float32 USING '{_absoluteUsingPath}' "
            + "AS BEGIN "
            + "  DECLARE outputs Struct = infer_outputs(x); "
            + "  DECLARE logits Float32[] = outputs[1]; "
            + "  RETURN logits[1] "
            + "END");

        IQueryPlan plan = catalog.Plan("SELECT models.pick_first(v) FROM data");

        List<DataValue> values = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0]);
            }
        }
        Assert.Single(values);
        // Stub: logits[1] (1-based, first element) = input * 2 = 4.0
        Assert.Equal(4.0f, values[0].AsFloat32());
    }

    [Fact]
    public async Task Infer_SingleOutput_StillEmitsArrayNotStruct()
    {
        // Regression guard: plain infer() on a single-output session keeps
        // emitting a primitive array / scalar (compat with all the existing
        // SQL models that DECLARE the result as Float32[] / Float32). The
        // struct emit is opt-in via infer_outputs().
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        dispatcher.NextSession = StubSession.Float32Doubler();

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"], [new object?[] { 3.0f }]));

        catalog.Plan(
            $"CREATE MODEL doubler_compat(x Float32) RETURNS Float32 USING '{_absoluteUsingPath}' "
            + "AS BEGIN RETURN infer(x) END");

        IQueryPlan plan = catalog.Plan("SELECT models.doubler_compat(v) FROM data");

        List<DataValue> values = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0]);
            }
        }
        Assert.Equal(DataKind.Float32, values[0].Kind);
        Assert.Equal(6.0f, values[0].AsFloat32());
    }

    [Fact]
    public async Task Infer_MultiOutputSession_StillEmitsFirstOutputOnly()
    {
        // Compat path: plain infer() against a multi-output session keeps
        // returning the FIRST declared output (the U²-Net / all-MiniLM
        // expectation). The struct emit is reserved for infer_outputs().
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        dispatcher.NextSession = StubSession.MultiOutputDual();

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"], [new object?[] { 2.0f }]));

        catalog.Plan(
            $"CREATE MODEL first_only(x Float32) RETURNS Float32 USING '{_absoluteUsingPath}' "
            + "AS BEGIN "
            + "  DECLARE logits Float32[] = infer(x); "
            + "  RETURN logits[1] "
            + "END");

        IQueryPlan plan = catalog.Plan("SELECT models.first_only(v) FROM data");

        List<DataValue> values = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0]);
            }
        }
        Assert.Single(values);
        // Stub: logits[1] (1-based, first element) = input * 2 = 4.0 (first declared output)
        Assert.Equal(4.0f, values[0].AsFloat32());
    }

    // ───────────────────── Batched dispatch ─────────────────────

    /// <summary>
    /// SQL-defined model with a straight-line body (DECLARE/RETURN-only)
    /// and an ONNX session declaring a dynamic leading dim hits the
    /// columnar dispatch path: N rows produce ONE <c>Session.Run</c>
    /// call with a packed <c>[N, ...]</c> shape, not N per-row calls.
    /// This is the payoff for the IsStraightLineBody flag + InferFunction's
    /// cross-row packing.
    /// </summary>
    [Fact]
    public async Task BatchedModel_DynamicBatchSession_RunsInferenceOnce_WithPackedShape()
    {
        int runCallCount = 0;
        int[]? observedShape = null;

        StubSession session = new(
            inputs: [new TensorSpec("input", DataKind.Float32, new int?[] { null, 3 })],
            outputs: [new TensorSpec("output", DataKind.Float32, new int?[] { null, 3 })],
            run: bag =>
            {
                runCallCount++;
                IInferenceTensor incoming = bag["input"];
                observedShape = incoming.Shape.ToArray();
                ReadOnlySpan<float> data = incoming.AsSpan<float>();
                float[] passthrough = data.ToArray();
                StubTensorBag output = new();
                output.Add<float>("output", DataKind.Float32, observedShape, passthrough.AsSpan());
                return output;
            });

        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        dispatcher.NextSession = session;
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"],
            [
                new object?[] { new float[] { 1f, 2f, 3f } },
                new object?[] { new float[] { 4f, 5f, 6f } },
                new object?[] { new float[] { 7f, 8f, 9f } },
                new object?[] { new float[] { 10f, 11f, 12f } },
            ]));

        catalog.Plan(
            $"CREATE MODEL passthrough(x Float32[]) RETURNS Float32[] "
            + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN infer(x) END");

        IQueryPlan plan = catalog.Plan("SELECT models.passthrough(v) FROM data");

        int rowCount = 0;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            rowCount += batch.Count;
        }

        Assert.Equal(4, rowCount);
        Assert.Equal(1, runCallCount);
        Assert.Equal(new[] { 4, 3 }, observedShape);
    }

    /// <summary>
    /// <c>infer_outputs()</c> against a multi-output session with a dynamic
    /// leading dim batches across rows: ONE <c>Session.Run</c> with packed
    /// <c>[N, ...]</c> inputs returns one batched output bag whose tensors are
    /// split back into per-row slices. Each row's result is a Struct of every
    /// declared output, identical in shape to the per-row dispatch path.
    /// Regression guard for the depth-anything / RT-DETR / RoBERTa-QA / BlazeFace
    /// calibration win — without this batching, every "batch size" the
    /// calibrator probes runs N sequential Session.Run calls and the curve is
    /// meaningless (uniform VRAM across batch sizes).
    /// </summary>
    [Fact]
    public async Task BatchedInferOutputs_MultiOutputDynamicBatch_RunsOnce_SplitsAcrossRows()
    {
        int runCallCount = 0;
        int[]? observedInputShape = null;

        // Dual scalar-per-row outputs (shape [batch]): exercises the perRow == 1
        // → scalar branch in the batched-split path, which mirrors per-row
        // dispatch's "product == 1 → scalar" rule. Values are deterministic
        // (2*v from logits, 10*v from boxes) so each row's struct slice can be
        // asserted by value.
        StubSession session = new(
            inputs: [new TensorSpec("input", DataKind.Float32, new int?[] { null, 1 })],
            outputs:
            [
                new TensorSpec("logits", DataKind.Float32, new int?[] { null }),
                new TensorSpec("boxes",  DataKind.Float32, new int?[] { null }),
            ],
            run: bag =>
            {
                runCallCount++;
                IInferenceTensor incoming = bag["input"];
                observedInputShape = incoming.Shape.ToArray();
                ReadOnlySpan<float> data = incoming.AsSpan<float>();
                int batchN = incoming.Shape[0];

                float[] logits = new float[batchN];
                float[] boxes  = new float[batchN];
                for (int row = 0; row < batchN; row++)
                {
                    float v = data[row];
                    logits[row] = v * 2f;
                    boxes[row]  = v * 10f;
                }

                StubTensorBag output = new();
                output.Add<float>("logits", DataKind.Float32, [batchN], logits.AsSpan());
                output.Add<float>("boxes",  DataKind.Float32, [batchN], boxes.AsSpan());
                return output;
            });

        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        dispatcher.NextSession = session;
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"],
            [
                new object?[] { new float[] { 1f } },
                new object?[] { new float[] { 2f } },
                new object?[] { new float[] { 3f } },
                new object?[] { new float[] { 4f } },
            ]));

        // Body pulls the 'boxes' field by name — proves the per-row struct
        // exposes EVERY output, not just the first, even on the batched path.
        catalog.Plan(
            $"CREATE MODEL detector(x Float32[]) RETURNS Float32 "
            + $"USING '{_absoluteUsingPath}' AS BEGIN "
            + "  DECLARE outputs Struct = infer_outputs(x); "
            + "  RETURN outputs['boxes'] "
            + "END");

        IQueryPlan plan = catalog.Plan("SELECT models.detector(v) FROM data");

        List<float> values = [];
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0].AsFloat32());
            }
        }

        Assert.Equal(4, values.Count);
        Assert.Equal(1, runCallCount); // payoff: ONE Session.Run for 4 rows
        Assert.Equal(new[] { 4, 1 }, observedInputShape);
        // boxes for each row = v * 10.
        Assert.Equal([10f, 20f, 30f, 40f], values);
    }

    /// <summary>
    /// Multi-dim per-row shape preservation: with a batched output shape
    /// <c>[N, 1, 3, 3]</c> (the depth-anything intrinsics export shape — the
    /// SQL author writes <c>array_get(intrinsics, 1, 1, 1, 1)</c> assuming
    /// rank 4), each row's slice must be a rank-4 multi-dim array
    /// <c>[1, 1, 3, 3]</c>, NOT a stripped rank-3 <c>[1, 3, 3]</c>. Dropping
    /// the leading batch dim would silently break every downstream
    /// <c>array_get</c> that indexes with the rank declared in the model's
    /// RETURNS annotation. Regression guard.
    /// </summary>
    [Fact]
    public async Task BatchedInferOutputs_RankPreservingSplit_KeepsLeadingBatchOneDim()
    {
        int runCallCount = 0;

        // Session output [batch, 1, 3, 3] — once batched, the per-row split
        // must surface each row as a rank-4 [1, 1, 3, 3] multi-dim array.
        // Per-row dispatch with batch=1 input would have produced exactly
        // that shape from ONNX; the batched path has to match byte-for-byte.
        StubSession session = new(
            inputs: [new TensorSpec("input", DataKind.Float32, new int?[] { null, 1 })],
            outputs: [new TensorSpec("intrinsics", DataKind.Float32, new int?[] { null, 1, 3, 3 })],
            run: bag =>
            {
                runCallCount++;
                IInferenceTensor incoming = bag["input"];
                int batchN = incoming.Shape[0];
                ReadOnlySpan<float> data = incoming.AsSpan<float>();

                // Per-row K matrix where the [0,0] entry encodes the input
                // scalar — gives the assertion a deterministic target.
                float[] intrinsics = new float[batchN * 1 * 3 * 3];
                for (int row = 0; row < batchN; row++)
                {
                    float v = data[row];
                    int rowBase = row * 9;
                    intrinsics[rowBase + 0] = v * 100f; // [0,0]
                }

                StubTensorBag output = new();
                output.Add<float>("intrinsics", DataKind.Float32, [batchN, 1, 3, 3], intrinsics.AsSpan());
                return output;
            });

        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        dispatcher.NextSession = session;
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"],
            [
                new object?[] { new float[] { 1f } },
                new object?[] { new float[] { 2f } },
                new object?[] { new float[] { 3f } },
            ]));

        // 4-index array_get matches the rank-4 [1, 1, 3, 3] per-row shape.
        // If the batched-split path were to strip the leading batch dim, this
        // would surface as a rank-3 array and the body would error
        // "array is 3-dimensional but 4 indices were supplied".
        catalog.Plan(
            $"CREATE MODEL k_picker(x Float32[]) RETURNS Float32 "
            + $"USING '{_absoluteUsingPath}' AS BEGIN "
            + "  DECLARE outputs Struct = infer_outputs(x); "
            + "  DECLARE intr Float32[] = outputs['intrinsics']; "
            + "  RETURN array_get(intr, 1, 1, 1, 1) "
            + "END");

        IQueryPlan plan = catalog.Plan("SELECT models.k_picker(v) FROM data");

        List<float> values = [];
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0].AsFloat32());
            }
        }

        Assert.Equal(3, values.Count);
        Assert.Equal(1, runCallCount);
        Assert.Equal([100f, 200f, 300f], values);
    }

    /// <summary>
    /// The 2-arg <c>infer(value, shape)</c> form with a uniform <c>[1, ...]</c>
    /// shape literal across rows (the SQL-author idiom mirrored by midas-small
    /// and yolox bodies) also batches: <c>InferFunction.ExecuteBatchAsync</c>
    /// recognises that every row's explicit shape leads with <c>1</c> and
    /// is otherwise identical, and recombines them as <c>[N, ...]</c>.
    /// </summary>
    [Fact]
    public async Task BatchedModel_ExplicitBatchOneShape_StillPacksAsBatchN()
    {
        int runCallCount = 0;
        int[]? observedShape = null;

        StubSession session = new(
            inputs: [new TensorSpec("input", DataKind.Float32, new int?[] { null, 3 })],
            outputs: [new TensorSpec("output", DataKind.Float32, new int?[] { null, 3 })],
            run: bag =>
            {
                runCallCount++;
                IInferenceTensor incoming = bag["input"];
                observedShape = incoming.Shape.ToArray();
                ReadOnlySpan<float> data = incoming.AsSpan<float>();
                float[] passthrough = data.ToArray();
                StubTensorBag output = new();
                output.Add<float>("output", DataKind.Float32, observedShape, passthrough.AsSpan());
                return output;
            });

        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        dispatcher.NextSession = session;
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"],
            [
                new object?[] { new float[] { 1f, 2f, 3f } },
                new object?[] { new float[] { 4f, 5f, 6f } },
                new object?[] { new float[] { 7f, 8f, 9f } },
            ]));

        catalog.Plan(
            $"CREATE MODEL passthrough2(x Float32[]) RETURNS Float32[] "
            + $"USING '{_absoluteUsingPath}' AS BEGIN "
            + $"  RETURN infer(x, [CAST(1 AS Int32), CAST(3 AS Int32)]) "
            + $"END");

        IQueryPlan plan = catalog.Plan("SELECT models.passthrough2(v) FROM data");

        int rowCount = 0;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            rowCount += batch.Count;
        }

        Assert.Equal(3, rowCount);
        Assert.Equal(1, runCallCount);
        Assert.Equal(new[] { 3, 3 }, observedShape);
    }

    /// <summary>
    /// Session shape <c>[-1, 3, -1, -1]</c> — leading batch dim AND trailing
    /// dims all dynamic, the depth-anything / glpn export shape. The 2-arg
    /// <c>infer(value, [1, 3, H, W])</c> form supplies the trailing dims via
    /// the explicit shape literal, so cross-row batching still kicks in
    /// even though the session spec itself can't tell us H or W. Regression
    /// guard for the depth-model perf path.
    /// </summary>
    [Fact]
    public async Task BatchedModel_AllDynamicSessionShape_ExplicitShapeSuppliesTrailingDims()
    {
        int runCallCount = 0;
        int[]? observedShape = null;

        StubSession session = new(
            inputs: [new TensorSpec("input", DataKind.Float32, new int?[] { null, 3, null, null })],
            outputs: [new TensorSpec("output", DataKind.Float32, new int?[] { null, 1, null, null })],
            run: bag =>
            {
                runCallCount++;
                IInferenceTensor incoming = bag["input"];
                observedShape = incoming.Shape.ToArray();
                // Single-channel output the same H×W per row; emit zeros.
                int batchN = incoming.Shape[0];
                int h = incoming.Shape[2];
                int w = incoming.Shape[3];
                float[] data = new float[batchN * h * w];
                StubTensorBag output = new();
                output.Add<float>("output", DataKind.Float32, [batchN, 1, h, w], data.AsSpan());
                return output;
            });

        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        dispatcher.NextSession = session;
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        // 3 rows, each with a 3*4*4 = 48-element preprocessed tensor — matches
        // the explicit [1, 3, 4, 4] shape literal product.
        float[] tensor = new float[48];
        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"],
            [
                new object?[] { tensor },
                new object?[] { tensor },
                new object?[] { tensor },
            ]));

        catalog.Plan(
            $"CREATE MODEL flex(x Float32[]) RETURNS Float32[] "
            + $"USING '{_absoluteUsingPath}' AS BEGIN "
            + $"  RETURN infer(x, [CAST(1 AS Int32), CAST(3 AS Int32), CAST(4 AS Int32), CAST(4 AS Int32)]) "
            + $"END");

        IQueryPlan plan = catalog.Plan("SELECT models.flex(v) FROM data");

        int rowCount = 0;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            rowCount += batch.Count;
        }

        Assert.Equal(3, rowCount);
        Assert.Equal(1, runCallCount);
        Assert.Equal(new[] { 3, 3, 4, 4 }, observedShape);
    }

    /// <summary>
    /// End-to-end check against a real shipped model body — drives
    /// <c>models/sql/glpn-nyu.sql</c>'s first CREATE MODEL (the visualization
    /// variant returning Image) through the columnar batched dispatch with a
    /// stub session whose input shape mirrors the real ONNX export
    /// (<c>[-1, 3, -1, -1]</c>, dynamic batch + dynamic H×W). The body's
    /// <c>infer(tensor, [1, 3, 480, 480])</c> 2-arg form provides the
    /// trailing dims via the literal, so cross-row batching kicks in even
    /// against the fully-flexible session. Three input rows → one packed
    /// <c>Session.Run</c> with shape <c>[3, 3, 480, 480]</c>.
    /// </summary>
    [Fact]
    public async Task ShippedGlpnNyuBody_BatchesAgainstDynamicSessionShape()
    {
        // Locate models/sql/glpn-nyu.sql relative to the test binary.
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "models", "sql")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        // Per-entry folder + dated filename layout under the catalog
        // substrate. Picks the newest cut so this test follows future
        // bumps without needing to be touched.
        string entrySqlDir = Path.Combine(dir!.FullName, "models", "sql", "glpn-nyu");
        string sqlPath = Directory.EnumerateFiles(entrySqlDir, "*.sql")
            .OrderByDescending(p => p)
            .First();
        string source = File.ReadAllText(sqlPath);

        // Substitute the catalog-versioned relative USING path with our
        // temp file — the stub dispatcher returns a fake session regardless
        // of the actual file contents. The shipped SQL declares paths like
        // 'glpn-nyu/<version>/onnx/model.onnx' (literal version segment per
        // PR 2). The version segment depends on the latest cut and would
        // need updating every catalog bump, so swap by Regex against the
        // shape instead of by literal.
        string sqlSource = System.Text.RegularExpressions.Regex.Replace(
            source,
            @"'glpn-nyu/[^']*/onnx/model\.onnx'",
            $"'{_absoluteUsingPath}'");
        // The single-statement Plan API needs exactly one statement with no
        // trailing separator. Slice up to and including the first `END`
        // (closing the glpn_nyu body) and drop everything after — the
        // statement-separator `;` and the second CREATE MODEL alike.
        int firstEndSemi = sqlSource.IndexOf("END;", StringComparison.Ordinal);
        Assert.True(firstEndSemi > 0, "Could not locate end of first CREATE MODEL in glpn-nyu.sql.");
        sqlSource = sqlSource[..(firstEndSemi + "END".Length)];

        int runCallCount = 0;
        int[]? observedShape = null;

        StubSession session = new(
            inputs: [new TensorSpec("pixel_values", DataKind.Float32, new int?[] { null, 3, null, null })],
            outputs: [new TensorSpec("predicted_depth", DataKind.Float32, new int?[] { null, 1, null, null })],
            run: bag =>
            {
                runCallCount++;
                IInferenceTensor incoming = bag["pixel_values"];
                observedShape = incoming.Shape.ToArray();
                int batchN = incoming.Shape[0];
                int h = incoming.Shape[2];
                int w = incoming.Shape[3];
                float[] depth = new float[batchN * h * w];
                StubTensorBag output = new();
                output.Add<float>("predicted_depth", DataKind.Float32, [batchN, 1, h, w], depth.AsSpan());
                return output;
            });

        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        dispatcher.NextSession = session;
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        // Three small bitmaps encoded to PNG bytes. image_to_tensor_chw
        // stretch-resizes every row to 480×480 internally, so per-row element
        // counts agree and the explicit [1, 3, 480, 480] shape literal is
        // uniform across rows. InMemoryTableProvider needs DataKind.Image
        // explicitly so byte[] cells materialise as Image rather than UInt8[].
        byte[] png0 = EncodeSolidPng(32, 32, new SkiaSharp.SKColor(200, 150, 100));
        byte[] png1 = EncodeSolidPng(40, 40, new SkiaSharp.SKColor(50, 100, 200));
        byte[] png2 = EncodeSolidPng(48, 48, new SkiaSharp.SKColor(150, 200, 50));

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["img"], [DataKind.Image],
            [
                new object?[] { png0 },
                new object?[] { png1 },
                new object?[] { png2 },
            ]));

        catalog.Plan(sqlSource);

        IQueryPlan plan = catalog.Plan("SELECT models.glpn_nyu(img) FROM data");

        int rowCount = 0;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            rowCount += batch.Count;
        }

        Assert.Equal(3, rowCount);
        Assert.Equal(1, runCallCount);
        Assert.Equal(new[] { 3, 3, 480, 480 }, observedShape);
    }

    private static byte[] EncodeSolidPng(int width, int height, SkiaSharp.SKColor color)
    {
        using SkiaSharp.SKBitmap bitmap = new(width, height);
        using (SkiaSharp.SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(color);
        }
        using SkiaSharp.SKData encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// Single-row dispatch keeps the per-row path even on a batchable body —
    /// the columnar interpreter is skipped when <c>rowCount &lt;= 1</c>
    /// because there's no batching win to spend the columnar setup on.
    /// Assertion is functional (correct output) plus exactly one
    /// <c>Session.Run</c> call (the per-row path's one dispatch).
    /// </summary>
    [Fact]
    public async Task BatchedModel_SingleRow_UsesPerRowPath()
    {
        int runCallCount = 0;
        StubSession session = new(
            inputs: [new TensorSpec("input", DataKind.Float32, new int?[] { null, 3 })],
            outputs: [new TensorSpec("output", DataKind.Float32, new int?[] { null, 3 })],
            run: bag =>
            {
                runCallCount++;
                IInferenceTensor incoming = bag["input"];
                ReadOnlySpan<float> data = incoming.AsSpan<float>();
                float[] passthrough = data.ToArray();
                StubTensorBag output = new();
                output.Add<float>("output", DataKind.Float32, incoming.Shape.ToArray(), passthrough.AsSpan());
                return output;
            });

        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        dispatcher.NextSession = session;
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath())
        {
            // Existing tests assert deterministic dispatch counts; opt out
            // of the production DoublingBatchSizePolicy (which would settle
            // at batch=1 against stub sessions that don't move VRAM).
            BatchSizePolicy = StaticBatchSizePolicy.Instance,
        };

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["v"],
            [new object?[] { new float[] { 1f, 2f, 3f } }]));

        catalog.Plan(
            $"CREATE MODEL once(x Float32[]) RETURNS Float32[] "
            + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN infer(x) END");

        IQueryPlan plan = catalog.Plan("SELECT models.once(v) FROM data");

        int rowCount = 0;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            rowCount += batch.Count;
        }

        Assert.Equal(1, rowCount);
        Assert.Equal(1, runCallCount);
    }

    // ───────────────────── Stubs ─────────────────────

    /// <summary>
    /// Minimal <see cref="IInferenceDispatcher"/> for registrar tests.
    /// Records the number of <see cref="LoadBundleAsync"/> calls and the
    /// most recently-returned session so tests can assert on disposal.
    /// </summary>
    private sealed class StubDispatcher : IInferenceDispatcher
    {
        public int LoadCallCount { get; private set; }
        public StubSession? LastSession { get; private set; }

        /// <summary>
        /// Pre-built session to hand out on the next <see cref="LoadBundleAsync"/>
        /// call. Tests that exercise infer() set this to a <see cref="StubSession"/>
        /// configured with a real run delegate; tests that only exercise the
        /// registrar leave it null and get the no-op default.
        /// </summary>
        public StubSession? NextSession { get; set; }

        public IReadOnlyList<IInferenceBackend> Backends => Array.Empty<IInferenceBackend>();

        public ValueTask<IReadOnlyDictionary<string, IModelSession>> LoadBundleAsync(
            BundleManifest bundle,
            InferencePreferences preferences,
            CancellationToken cancellationToken)
        {
            LoadCallCount++;
            StubSession session = NextSession ?? new StubSession();
            NextSession = null;
            LastSession = session;
            IReadOnlyDictionary<string, IModelSession> sessions =
                new Dictionary<string, IModelSession>(StringComparer.Ordinal)
                {
                    ["default"] = session,
                };
            return ValueTask.FromResult(sessions);
        }
    }

    /// <summary>
    /// Minimal <see cref="IInferenceSession"/> stub. Tracks disposal so
    /// OR REPLACE / DROP tests can assert the registrar reaches in and
    /// releases native handles when displacing or removing a descriptor.
    /// </summary>
    private sealed class StubSession : IInferenceSession
    {
        private readonly Func<TensorBag, TensorBag>? _run;

        public StubSession()
            : this(Array.Empty<TensorSpec>(), Array.Empty<TensorSpec>(), run: null)
        {
        }

        public StubSession(
            IReadOnlyList<TensorSpec> inputs,
            IReadOnlyList<TensorSpec> outputs,
            Func<TensorBag, TensorBag>? run)
        {
            Inputs = inputs;
            Outputs = outputs;
            _run = run;
        }

        public bool Disposed { get; private set; }

        public IReadOnlyList<TensorSpec> Inputs { get; }
        public IReadOnlyList<TensorSpec> Outputs { get; }
        public InferenceBackendId Backend => InferenceBackendId.OnnxRuntime;
        public InferenceDevice Device => InferenceDevice.OnnxRuntimeCpu;
        public long EstimatedResidentBytes => 0;

        public TensorBag CreateInputBag() => new StubTensorBag();

        public ValueTask<TensorBag> RunAsync(TensorBag inputs, CancellationToken cancellationToken)
        {
            if (_run is null)
            {
                throw new NotSupportedException(
                    "Stub session does not support inference. Configure a run delegate via the parameterised constructor.");
            }
            return ValueTask.FromResult(_run(inputs));
        }

        public void Dispose() => Disposed = true;

        /// <summary>
        /// Single Float32-input, single Float32-output stub that doubles its
        /// scalar input. Smallest viable shape for exercising
        /// <c>infer()</c>'s scalar marshalling path.
        /// </summary>
        public static StubSession Float32Doubler() => new(
            inputs: [new TensorSpec("input", DataKind.Float32, [1])],
            outputs: [new TensorSpec("output", DataKind.Float32, [1])],
            run: bag =>
            {
                ReadOnlySpan<float> incoming = bag["input"].AsSpan<float>();
                float[] doubled = new float[incoming.Length];
                for (int i = 0; i < incoming.Length; i++)
                {
                    doubled[i] = incoming[i] * 2f;
                }
                StubTensorBag output = new();
                output.Add<float>("output", DataKind.Float32, [doubled.Length], doubled.AsSpan());
                return output;
            });

        /// <summary>
        /// Single Float32-input, single Float32 rank-2 output stub: emits a 2×3
        /// matrix derived from the scalar input. Exercises the multi-dim output
        /// construction path — the resulting <c>infer()</c> ValueRef should
        /// carry <see cref="DataValue.IsMultiDim"/> with shape <c>[2, 3]</c>.
        /// </summary>
        public static StubSession Float32Matrix2x3() => new(
            inputs: [new TensorSpec("input", DataKind.Float32, [1])],
            outputs: [new TensorSpec("output", DataKind.Float32, [2, 3])],
            run: bag =>
            {
                float v = bag["input"].AsSpan<float>()[0];
                float[] cells = [
                    v + 0f, v + 1f, v + 2f,
                    v + 3f, v + 4f, v + 5f,
                ];
                StubTensorBag output = new();
                output.Add<float>("output", DataKind.Float32, [2, 3], cells.AsSpan());
                return output;
            });

        /// <summary>
        /// Two-input scalar Int64 session that emits the sum as a Float32
        /// scalar. Smallest shape for multi-input infer() dispatch — both
        /// inputs have a fully-determined [1] spec so no explicit-shape
        /// argument is needed.
        /// </summary>
        public static StubSession MultiInputInt64Sum() => new(
            inputs:
            [
                new TensorSpec("input_a", DataKind.Int64, [1]),
                new TensorSpec("input_b", DataKind.Int64, [1]),
            ],
            outputs: [new TensorSpec("output", DataKind.Float32, [1])],
            run: bag =>
            {
                long a = bag["input_a"].AsSpan<long>()[0];
                long b = bag["input_b"].AsSpan<long>()[0];
                float sum = a + b;
                StubTensorBag output = new();
                output.Add<float>("output", DataKind.Float32, [1], new[] { sum }.AsSpan());
                return output;
            });

        /// <summary>
        /// Single Float32-input, two-Float32-output stub: emits
        /// <c>logits = [input * 2, input * 3]</c> and
        /// <c>boxes  = [input * 10, input * 20]</c>. Smallest viable shape
        /// for exercising the multi-output struct-emit path: distinct
        /// output names, distinct values, both >1 element so the
        /// array-vs-scalar branch is exercised.
        /// </summary>
        public static StubSession MultiOutputDual() => new(
            inputs: [new TensorSpec("input", DataKind.Float32, [1])],
            outputs:
            [
                new TensorSpec("logits", DataKind.Float32, [2]),
                new TensorSpec("boxes",  DataKind.Float32, [2]),
            ],
            run: bag =>
            {
                ReadOnlySpan<float> incoming = bag["input"].AsSpan<float>();
                float v = incoming[0];
                StubTensorBag output = new();
                output.Add<float>("logits", DataKind.Float32, [2], new[] { v * 2f, v * 3f }.AsSpan());
                output.Add<float>("boxes",  DataKind.Float32, [2], new[] { v * 10f, v * 20f }.AsSpan());
                return output;
            });

        /// <summary>
        /// BERT-shaped two-input session: <c>input_ids</c> and
        /// <c>attention_mask</c> both as <see cref="DataKind.Int64"/> with
        /// shape <c>[batch=?, seq_len=?]</c> (every dim dynamic), output
        /// <c>pooled</c> as <see cref="DataKind.Float32"/>. The dual-dynamic
        /// input shape forces the call-site to pass explicit shapes — the
        /// canonical scenario for the (Struct, Struct) infer() form. Run
        /// delegate emits <c>sum(ids[i] * mask[i])</c> so tests can assert
        /// on a deterministic Float32 scalar.
        /// </summary>
        public static StubSession MultiInputMaskedDotProduct() => new(
            inputs:
            [
                new TensorSpec("input_ids",      DataKind.Int64, new int?[] { null, null }),
                new TensorSpec("attention_mask", DataKind.Int64, new int?[] { null, null }),
            ],
            outputs: [new TensorSpec("pooled", DataKind.Float32, new int?[] { 1 })],
            run: bag =>
            {
                ReadOnlySpan<long> ids  = bag["input_ids"].AsSpan<long>();
                ReadOnlySpan<long> mask = bag["attention_mask"].AsSpan<long>();
                float sum = 0;
                int n = System.Math.Min(ids.Length, mask.Length);
                for (int i = 0; i < n; i++) sum += ids[i] * mask[i];
                StubTensorBag output = new();
                output.Add<float>("pooled", DataKind.Float32, [1], new[] { sum }.AsSpan());
                return output;
            });
    }

    /// <summary>
    /// Managed-only <see cref="TensorBag"/> for tests that exercise the
    /// inference layer without ONNX Runtime. Stores tensor bytes in heap
    /// arrays; <see cref="StubTensor.AsSpan{T}"/> casts them via
    /// <see cref="MemoryMarshal"/>.
    /// </summary>
    private sealed class StubTensorBag : TensorBag
    {
        private readonly Dictionary<string, StubTensor> _tensors =
            new(StringComparer.Ordinal);
        private readonly List<string> _names = new();

        public override int Count => _tensors.Count;
        public override IReadOnlyList<string> Names => _names;
        public override IInferenceTensor this[string name] => _tensors[name];

        public override bool TryGet(string name, out IInferenceTensor tensor)
        {
            if (_tensors.TryGetValue(name, out StubTensor? hit))
            {
                tensor = hit;
                return true;
            }
            tensor = null!;
            return false;
        }

        public override IInferenceTensor Add<T>(
            string name, DataKind elementKind, ReadOnlySpan<int> shape, ReadOnlySpan<T> data)
        {
            byte[] bytes = MemoryMarshal.AsBytes(data).ToArray();
            StubTensor tensor = new(name, elementKind, shape.ToArray(), bytes);
            _tensors[name] = tensor;
            _names.Add(name);
            return tensor;
        }

        public override void Dispose()
        {
            // Heap arrays — nothing to release.
        }
    }

    private sealed class StubTensor : IInferenceTensor
    {
        private readonly byte[] _bytes;

        public StubTensor(string name, DataKind elementKind, int[] shape, byte[] bytes)
        {
            Name = name;
            ElementKind = elementKind;
            Shape = shape;
            _bytes = bytes;
        }

        public string Name { get; }
        public DataKind ElementKind { get; }
        public IReadOnlyList<int> Shape { get; }
        public bool IsResidentOnCpu => true;

        public ReadOnlySpan<T> AsSpan<T>() where T : unmanaged
            => MemoryMarshal.Cast<byte, T>(_bytes);

        public void Dispose()
        {
            // Heap-backed; nothing to release.
        }
    }
}

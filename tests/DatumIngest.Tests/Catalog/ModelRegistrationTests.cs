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
        Assert.Single(descriptor.BoundSessions);
        Assert.Equal(1, dispatcher.LoadCallCount);
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
    public void CreateModel_OrReplace_ReplacesAndDisposesOldSessions()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);

        catalog.Plan(Ddl("RETURN x"));
        StubSession firstSession = dispatcher.LastSession!;
        Assert.False(firstSession.Disposed);

        catalog.Plan(
            $"CREATE OR REPLACE MODEL classify(x INT32) RETURNS INT32 " +
            $"USING '{_absoluteUsingPath}' AS BEGIN RETURN x + 1 END");

        // Old session must be disposed; new session is live.
        Assert.True(firstSession.Disposed);
        Assert.NotSame(firstSession, dispatcher.LastSession);
        Assert.False(dispatcher.LastSession!.Disposed);
        Assert.Equal(2, dispatcher.LoadCallCount);
    }

    [Fact]
    public void CreateModel_IfNotExists_NoOpWhenAlreadyRegistered()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);

        catalog.Plan(Ddl("RETURN x"));
        StubSession firstSession = dispatcher.LastSession!;

        catalog.Plan(
            $"CREATE MODEL IF NOT EXISTS classify(x INT32) RETURNS INT32 " +
            $"USING '{_absoluteUsingPath}' AS BEGIN RETURN x + 1 END");

        // No second load happened — the existing descriptor wins and
        // no new session was bound.
        Assert.Equal(1, dispatcher.LoadCallCount);
        Assert.False(firstSession.Disposed);
    }

    // ───────────────────── DROP MODEL ─────────────────────

    [Fact]
    public void DropModel_RemovesFromRegistryAndDisposesSessions()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);

        catalog.Plan(Ddl("RETURN x"));
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
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath());

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

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
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

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan(
                $"CREATE MODEL bad_arity(p String, n Int32) RETURNS String "
                + $"IMPLEMENTS TextGenerator "
                + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN p END"));
        Assert.Contains("TextGenerator", ex.Message);
        Assert.Contains("parameter", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateModel_Implements_MismatchedParamKind_Throws()
    {
        // TextGenerator requires String; this declares Int32 input.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
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

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan(
                $"CREATE MODEL bogus(x Int32) RETURNS Int32 "
                + $"IMPLEMENTS NonExistentTask "
                + $"USING '{_absoluteUsingPath}' AS BEGIN RETURN x END"));
        Assert.Contains("NonExistentTask", ex.Message);
        Assert.Contains("datum_catalog.tasks", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    // ───────────────────── infer() runtime bridge (Phase 3b) ─────────────────────

    [Fact]
    public void Infer_OutsideModelBody_ThrowsAtPlanTime()
    {
        // The plan-time gate should refuse infer() in any non-CREATE-MODEL
        // context — the runtime "no CurrentModel frame" guard stays as a
        // backstop, but users should hit the friendly error before any
        // rows are scanned.
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath());
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
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath());
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
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath());

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
    public async Task Infer_MultiInput_StructArg_FeedsSessionInputsByFieldName()
    {
        // Multi-input v1: pass a struct of tensors whose field names match
        // the session's input names. The stub session takes two Int64
        // scalars (input_a + input_b) and emits their sum as a Float32. The
        // body's `infer({input_a: a, input_b: b})` proves struct-arg
        // dispatch, name-based input resolution, and the no-explicit-shape
        // fast path (each input's [1] spec resolves without help).
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath());

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
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath());

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

        public ValueTask<IReadOnlyDictionary<string, IInferenceSession>> LoadBundleAsync(
            BundleManifest bundle,
            InferencePreferences preferences,
            CancellationToken cancellationToken)
        {
            LoadCallCount++;
            StubSession session = NextSession ?? new StubSession();
            NextSession = null;
            LastSession = session;
            IReadOnlyDictionary<string, IInferenceSession> sessions =
                new Dictionary<string, IInferenceSession>(StringComparer.Ordinal)
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

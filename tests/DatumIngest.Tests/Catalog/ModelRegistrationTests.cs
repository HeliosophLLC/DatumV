using DatumIngest.Catalog;
using DatumIngest.Execution;
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
        $"CREATE MODEL classify(@x INT32) RETURNS INT32 USING '{_absoluteUsingPath}' " +
        $"AS BEGIN {body} END";

    // ───────────────────── CREATE MODEL ─────────────────────

    [Fact]
    public void CreateModel_RegistersInDeclaredModels()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);

        catalog.Plan(Ddl("RETURN @x"));

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

        catalog.Plan(Ddl("RETURN @x"));

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
            $"CREATE MODEL models.classify(@x INT32) RETURNS INT32 " +
            $"USING '{_absoluteUsingPath}' AS BEGIN RETURN @x END");

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out _));
    }

    [Fact]
    public void CreateModel_NonModelsSchema_Throws()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan(
                $"CREATE MODEL public.classify(@x INT32) RETURNS INT32 " +
                $"USING '{_absoluteUsingPath}' AS BEGIN RETURN @x END"));
        Assert.Contains("'models' schema", ex.Message);
    }

    [Fact]
    public void CreateModel_NoDispatcherWired_Throws()
    {
        // Catalog without InferenceDispatcher — CREATE MODEL must surface
        // a clear "wire a dispatcher first" error rather than NRE.
        TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan(Ddl("RETURN @x")));
        Assert.Contains("InferenceDispatcher", ex.Message);
    }

    [Fact]
    public void CreateModel_FileMissing_ThrowsFileNotFound()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        string bogus = "file://" + Path.Combine(Path.GetTempPath(),
            $"datum-test-missing-{Guid.NewGuid():N}.onnx");

        Assert.Throws<FileNotFoundException>(() => catalog.Plan(
            $"CREATE MODEL classify(@x INT32) RETURNS INT32 " +
            $"USING '{bogus}' AS BEGIN RETURN @x END"));
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
                "CREATE MODEL classify(@x INT32) RETURNS INT32 " +
                "USING 'classify.onnx' AS BEGIN RETURN @x END"));
        Assert.Contains("ModelCatalog", ex.Message);
    }

    [Fact]
    public void CreateModel_DuplicateName_Throws()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out _);

        catalog.Plan(Ddl("RETURN @x"));

        Assert.ThrowsAny<InvalidOperationException>(
            () => catalog.Plan(Ddl("RETURN @x + 1")));
    }

    [Fact]
    public void CreateModel_OrReplace_ReplacesAndDisposesOldSessions()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher(out StubDispatcher dispatcher);

        catalog.Plan(Ddl("RETURN @x"));
        StubSession firstSession = dispatcher.LastSession!;
        Assert.False(firstSession.Disposed);

        catalog.Plan(
            $"CREATE OR REPLACE MODEL classify(@x INT32) RETURNS INT32 " +
            $"USING '{_absoluteUsingPath}' AS BEGIN RETURN @x + 1 END");

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

        catalog.Plan(Ddl("RETURN @x"));
        StubSession firstSession = dispatcher.LastSession!;

        catalog.Plan(
            $"CREATE MODEL IF NOT EXISTS classify(@x INT32) RETURNS INT32 " +
            $"USING '{_absoluteUsingPath}' AS BEGIN RETURN @x + 1 END");

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

        catalog.Plan(Ddl("RETURN @x"));
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

        catalog.Plan(Ddl("RETURN @x"));
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
            $"CREATE MODEL square(@x INT32) RETURNS INT32 USING '{_absoluteUsingPath}' " +
            $"AS BEGIN RETURN @x * @x END");

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

        public IReadOnlyList<IInferenceBackend> Backends => Array.Empty<IInferenceBackend>();

        public ValueTask<IReadOnlyDictionary<string, IInferenceSession>> LoadBundleAsync(
            BundleManifest bundle,
            InferencePreferences preferences,
            CancellationToken cancellationToken)
        {
            LoadCallCount++;
            StubSession session = new();
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
        public bool Disposed { get; private set; }

        public IReadOnlyList<TensorSpec> Inputs => Array.Empty<TensorSpec>();
        public IReadOnlyList<TensorSpec> Outputs => Array.Empty<TensorSpec>();
        public InferenceBackendId Backend => InferenceBackendId.OnnxRuntime;
        public InferenceDevice Device => InferenceDevice.OnnxRuntimeCpu;
        public long EstimatedResidentBytes => 0;

        public TensorBag CreateInputBag()
            => throw new NotSupportedException("Stub session does not support tensor bags.");

        public ValueTask<TensorBag> RunAsync(TensorBag inputs, CancellationToken cancellationToken)
            => throw new NotSupportedException("Stub session does not support inference.");

        public void Dispose() => Disposed = true;
    }
}

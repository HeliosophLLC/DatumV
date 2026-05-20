using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Plans;
using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Inference;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// Pins the side-effect-free contract of <see cref="ModelPlan"/>:
/// <see cref="TableCatalog.PlanAsync(string)"/> for CREATE/DROP/EVICT
/// MODEL and RESET CALIBRATION must not mutate the model registry
/// until the returned plan is iterated.
/// </summary>
/// <remarks>
/// Uses a real on-disk fixture file so the registrar's USING-resolution
/// and File.Exists gates pass. Registration doesn't touch the inference
/// dispatcher (loads are deferred to the body's first infer() call), so
/// no dispatcher stub is required.
/// </remarks>
public sealed class ModelPlanTests : ServiceTestBase
{
    private readonly string _modelFile;
    private readonly string _absoluteUsingPath;

    public ModelPlanTests()
    {
        _modelFile = Path.Combine(Path.GetTempPath(),
            $"datum-test-modelplan-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(_modelFile, [0]);
        _absoluteUsingPath = "file://" + _modelFile;
    }

    public override void Dispose()
    {
        if (File.Exists(_modelFile)) File.Delete(_modelFile);
        base.Dispose();
    }

    private TableCatalog CreateCatalogWithDispatcher()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.InferenceDispatcher = new NoOpDispatcher();
        return catalog;
    }

    private string CreateDdl(string name) =>
        $"CREATE MODEL {name}(x INT32) RETURNS INT32 USING '{_absoluteUsingPath}' " +
        "AS BEGIN RETURN x END";

    /// <summary>
    /// Minimal <see cref="IInferenceDispatcher"/> that satisfies the
    /// CREATE MODEL null-check without depending on any backend.
    /// LoadBundleAsync is never invoked because the model bodies in
    /// these tests don't call <c>infer()</c> — registration only walks
    /// USING paths and validates the body.
    /// </summary>
    private sealed class NoOpDispatcher : IInferenceDispatcher
    {
        public IReadOnlyList<IInferenceBackend> Backends => [];

        public ValueTask<IReadOnlyDictionary<string, IModelSession>> LoadBundleAsync(
            BundleManifest bundle,
            InferencePreferences preferences,
            CancellationToken cancellationToken)
            => throw new NotSupportedException(
                "NoOpDispatcher does not load sessions; tests must not exercise infer().");
    }

    [Fact]
    public async Task PlanAsync_CreateModel_DoesNotRegisterUntilIterated()
    {
        using TableCatalog catalog = CreateCatalogWithDispatcher();

        StatementPlan plan = await catalog.PlanAsync(CreateDdl("classify"));

        Assert.IsType<ModelPlan>(plan);
        Assert.Equal("CreateModel", plan.ExplainTree.OperatorName);
        Assert.False(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out _),
            "PlanAsync must not register the model — the side effect belongs to ExecuteAsync.");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out _));
    }

    [Fact]
    public async Task PlanAsync_DropModel_DoesNotUnregisterUntilIterated()
    {
        using TableCatalog catalog = CreateCatalogWithDispatcher();
        await Drain(catalog, CreateDdl("classify"));
        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out _));

        StatementPlan plan = await catalog.PlanAsync("DROP MODEL models.classify");

        Assert.IsType<ModelPlan>(plan);
        Assert.Equal("DropModel", plan.ExplainTree.OperatorName);
        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out _),
            "PlanAsync must not unregister the model — the side effect belongs to ExecuteAsync.");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.False(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out _));
    }

    [Fact]
    public async Task PlanAsync_EvictModel_HasNoSideEffectsUntilIterated()
    {
        using TableCatalog catalog = CreateCatalogWithDispatcher();
        await Drain(catalog, CreateDdl("classify"));

        StatementPlan plan = await catalog.PlanAsync("EVICT MODEL IF EXISTS models.classify");

        Assert.IsType<ModelPlan>(plan);
        Assert.Equal("EvictModel", plan.ExplainTree.OperatorName);
        // The model registration stays untouched whether or not Evict runs;
        // what matters here is the plan returned correctly without throwing.
        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out _));

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out _));
    }

    [Fact]
    public async Task PlanAsync_ResetCalibration_HasNoSideEffectsUntilIterated()
    {
        using TableCatalog catalog = CreateCatalogWithDispatcher();
        await Drain(catalog, CreateDdl("classify"));

        StatementPlan plan = await catalog.PlanAsync(
            "RESET CALIBRATION IF EXISTS models.classify");

        Assert.IsType<ModelPlan>(plan);
        Assert.Equal("ResetCalibration", plan.ExplainTree.OperatorName);
        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out _));

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "classify"), out _));
    }

    [Fact]
    public async Task ModelPlan_DoubleExecute_Throws()
    {
        using TableCatalog catalog = CreateCatalogWithDispatcher();

        StatementPlan plan = await catalog.PlanAsync(CreateDdl("classify"));

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in catalog.ExecuteAsync(plan)) { }
        });
    }

    private static async Task Drain(TableCatalog catalog, string sql)
    {
        StatementPlan plan = await catalog.PlanAsync(sql);
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }
    }
}

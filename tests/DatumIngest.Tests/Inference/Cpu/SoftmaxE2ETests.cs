using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Inference;
using DatumIngest.Inference.OnnxRuntime;
using DatumIngest.Model;
using DatumIngest.Models;

using Microsoft.Extensions.Logging.Abstractions;

namespace DatumIngest.Tests.Inference.Cpu;

/// <summary>
/// End-to-end Phase 4 test: CREATE MODEL → SELECT → real ONNX session →
/// expected math. Uses the real <see cref="OnnxRuntimeBackend"/> +
/// <see cref="InferenceDispatcher"/> over <c>tests/Fixtures/softmax.onnx</c>
/// (single Float32 input <c>x</c>, single Float32 output <c>y</c>, both
/// 1-d with one dynamic dim).
/// </summary>
/// <remarks>
/// Lives under <c>Inference/Cpu/</c> so it runs by default. The
/// <c>Inference/Gpu/</c> sibling will hold tests that demand a GPU device
/// and want the standard exclusion filter to skip them on CPU-only
/// machines.
/// </remarks>
public sealed class SoftmaxE2ETests : ServiceTestBase
{
    private static string FixturePath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "softmax.onnx");

    private TableCatalog CreateCatalogWithRealDispatcher()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath());
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);
        return catalog;
    }

    [Fact]
    public async Task Softmax_RealOnnx_RoundTripsThroughInfer()
    {
        string fixturePath = FixturePath();
        Assert.True(File.Exists(fixturePath),
            $"Fixture missing at {fixturePath}. Check tests/DatumIngest.Tests/Fixtures/softmax.onnx is present and copied to bin output.");

        TableCatalog catalog = CreateCatalogWithRealDispatcher();
        catalog.Add(new InMemoryTableProvider(
            CreatePool(), "data", ["d"], [new object?[] { 1 }]));

        // CREATE MODEL with Float32[] in / Float32[] out, body just calls
        // infer() which marshals the array into a 1-d tensor, dispatches,
        // and unwraps the output array.
        catalog.Plan(
            $"CREATE MODEL softmax(x Float32[]) RETURNS Float32[] " +
            $"USING 'file://{fixturePath}' " +
            $"AS BEGIN RETURN infer(x) END");

        // CAST the literal so the planner sees Float32[] on the call site —
        // bare numeric-literal arrays default to a wider kind.
        // Build the literal element-wise so each entry is Float32. Bracket-
        // literal kind inference doesn't currently coalesce numeric
        // literals to Float32, so we cast each scalar and let the array
        // constructor pick that up.
        IQueryPlan plan = catalog.Plan(
            "SELECT models.softmax([CAST(1.0 AS Float32), CAST(2.0 AS Float32), CAST(3.0 AS Float32)]) FROM data");

        // Collect inside the iteration loop so the array data can be read
        // against the batch's arena before it's recycled.
        List<float[]> rows = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                DataValue cell = batch[i][0];
                Assert.True(cell.IsArray, $"Expected array result, got Kind={cell.Kind}");
                rows.Add(cell.AsArraySpan<float>(batch.Arena).ToArray());
            }
        }

        Assert.Single(rows);
        float[] actual = rows[0];
        // Compare element-wise against canonical softmax([1, 2, 3]) values:
        //   exp(x_i) / sum(exp(x))  →  ~[0.09003, 0.24473, 0.66524].
        float[] expected = SoftmaxReference([1.0f, 2.0f, 3.0f]);
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.InRange(actual[i] - expected[i], -1e-5f, 1e-5f);
        }
    }

    private static float[] SoftmaxReference(float[] x)
    {
        float max = x[0];
        for (int i = 1; i < x.Length; i++)
        {
            if (x[i] > max) max = x[i];
        }
        float[] exps = new float[x.Length];
        float sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            exps[i] = MathF.Exp(x[i] - max);
            sum += exps[i];
        }
        for (int i = 0; i < x.Length; i++)
        {
            exps[i] /= sum;
        }
        return exps;
    }

    /// <summary>
    /// Step 2 probe: a SQL-defined model whose body is NOT straight-line
    /// falls back through <c>ModelInvocationOperator</c> + the
    /// <c>ProceduralModelAdapter</c>. The tracer fires for this path —
    /// proving the step-2 adapter wiring still works. Every SQL-defined
    /// model body now flows through this MIO path; the historical
    /// straight-line-body lowering was removed (see the next test).
    /// </summary>
    [Fact]
    public async Task Softmax_NonStraightLineBody_RoutesThroughMioWithTracer()
    {
        string fixturePath = FixturePath();
        Assert.True(File.Exists(fixturePath),
            $"Fixture missing at {fixturePath}.");

        TableCatalog catalog = CreateCatalogWithRealDispatcher();
        catalog.Add(new InMemoryTableProvider(
            CreatePool(), "data", ["d"], [new object?[] { 1 }]));

        RecordingTracer tracer = new();
        catalog.ModelTracer = tracer;

        // SET-after-DECLARE makes the body non-straight-line, so the
        // lowerer rejects it and the call falls through to MIO + adapter.
        // The SET is a no-op for the math — it just trips the predicate.
        catalog.Plan(
            $"CREATE MODEL softmax_branch(x Float32[]) RETURNS Float32[] " +
            $"USING 'file://{fixturePath}' " +
            $"AS BEGIN " +
            $"  DECLARE result Float32[] = infer(x); " +
            $"  SET result = result; " +
            $"  RETURN result " +
            $"END");

        IQueryPlan plan = catalog.Plan(
            "SELECT models.softmax_branch([CAST(1.0 AS Float32), CAST(2.0 AS Float32), CAST(3.0 AS Float32)]) FROM data");

        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            // Drain — we care about the tracer events, not the values.
        }

        Assert.True(tracer.StartedCount > 0,
            "Tracer never fired OnDispatchStarted — the non-straight-line SQL-defined model was not hoisted into ModelInvocationOperator.");
        Assert.Equal(tracer.StartedCount, tracer.CompletedCount);
        Assert.Contains(tracer.ObservedModels, name =>
            string.Equals(name, "softmax_branch", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every SQL-defined model body — straight-line or not — now runs
    /// through <see cref="ModelInvocationOperator"/> + <c>ProceduralModelAdapter</c>.
    /// The InferOperator lowering path was deleted because it paid for
    /// repeated arena retention + sidecar re-decode at every operator
    /// boundary, measuring ~20× slower per row than the unified MIO path.
    /// Cross-row batching for batchable shapes now lives on
    /// <c>InferFunction.ExecuteBatchAsync</c> instead of being structural.
    /// This test pins the plan shape (Model Invocation node, no Infer) so
    /// a regression that re-introduces lowering is caught early.
    /// </summary>
    [Fact]
    public async Task Softmax_StraightLineBody_RoutesThroughModelInvocationOperator()
    {
        string fixturePath = FixturePath();
        Assert.True(File.Exists(fixturePath), $"Fixture missing at {fixturePath}.");

        TableCatalog catalog = CreateCatalogWithRealDispatcher();
        catalog.Add(new InMemoryTableProvider(
            CreatePool(), "data", ["d"], [new object?[] { 1 }]));

        catalog.Plan(
            $"CREATE MODEL softmax_lowered(x Float32[]) RETURNS Float32[] " +
            $"USING 'file://{fixturePath}' " +
            $"AS BEGIN RETURN infer(x) END");

        IQueryPlan plan = catalog.Plan(
            "SELECT models.softmax_lowered([CAST(1.0 AS Float32), CAST(2.0 AS Float32), CAST(3.0 AS Float32)]) FROM data");

        ExplainPlanNode tree = plan.ExplainTree;
        bool sawInfer = false;
        bool sawMio = false;
        WalkPlan(tree, node =>
        {
            if (node.OperatorName == "Infer") sawInfer = true;
            if (node.OperatorName == "Model Invocation") sawMio = true;
        });

        Assert.False(sawInfer, "Plan tree must NOT contain an Infer operator — SQL-defined-body lowering was removed.");
        Assert.True(sawMio, "Plan tree must contain a Model Invocation operator for SQL-defined model bodies.");

        // Output correctness — the MIO path must produce the same softmax
        // result the lowered path used to. Compare against the canonical.
        List<float[]> rows = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                DataValue cell = batch[i][0];
                rows.Add(cell.AsArraySpan<float>(batch.Arena).ToArray());
            }
        }
        Assert.Single(rows);
        float[] expected = SoftmaxReference([1.0f, 2.0f, 3.0f]);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.InRange(rows[0][i] - expected[i], -1e-5f, 1e-5f);
        }
    }

    private static void WalkPlan(ExplainPlanNode node, Action<ExplainPlanNode> visit)
    {
        visit(node);
        foreach (ExplainPlanNode child in node.Children) WalkPlan(child, visit);
    }

    /// <summary>
    /// Regression test for the 2-arg <c>infer(value, shape)</c> path. The
    /// softmax fixture's input shape is a single dynamic dim, and the
    /// explicit shape <c>[3]</c> matches the 3-element softmax input, so
    /// output is identical to the 1-arg form. Pinned plan shape: Model
    /// Invocation node (no Infer node — lowering was removed).
    /// </summary>
    [Fact]
    public async Task Softmax_TwoArgInfer_WithExplicitShape_ProducesSameOutput()
    {
        string fixturePath = FixturePath();
        Assert.True(File.Exists(fixturePath), $"Fixture missing at {fixturePath}.");

        TableCatalog catalog = CreateCatalogWithRealDispatcher();
        catalog.Add(new InMemoryTableProvider(
            CreatePool(), "data", ["d"], [new object?[] { 1 }]));

        catalog.Plan(
            $"CREATE MODEL softmax_shaped(x Float32[]) RETURNS Float32[] " +
            $"USING 'file://{fixturePath}' " +
            $"AS BEGIN RETURN infer(x, [CAST(3 AS Int32)]) END");

        IQueryPlan plan = catalog.Plan(
            "SELECT models.softmax_shaped([CAST(1.0 AS Float32), CAST(2.0 AS Float32), CAST(3.0 AS Float32)]) FROM data");

        ExplainPlanNode tree = plan.ExplainTree;
        bool sawMio = false;
        WalkPlan(tree, n =>
        {
            if (n.OperatorName == "Model Invocation") sawMio = true;
        });
        Assert.True(sawMio, "Plan tree must contain a Model Invocation operator for SQL-defined model bodies.");

        List<float[]> rows = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                DataValue cell = batch[i][0];
                rows.Add(cell.AsArraySpan<float>(batch.Arena).ToArray());
            }
        }

        Assert.Single(rows);
        float[] expected = SoftmaxReference([1.0f, 2.0f, 3.0f]);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.InRange(rows[0][i] - expected[i], -1e-5f, 1e-5f);
        }
    }

    /// <summary>
    /// Multi-DECLARE body smoke test. Historically this exercised the
    /// lowered-path synthesized-column-passthrough invariant (each
    /// intermediate ProjectOperator passing every prior <c>__mb_*</c>
    /// column through so later DECLAREs could reference earlier ones).
    /// That invariant is moot now — lowering was removed and bodies
    /// run row-at-a-time inside a single <see cref="ProceduralModelFunction"/>
    /// where DECLARE bindings live in a per-call <c>VariableScope</c>
    /// rather than as columns. The test stays as a smoke check that
    /// multi-DECLARE bodies still produce correct output via MIO.
    /// </summary>
    [Fact]
    public async Task Softmax_MultiDeclareBody_ProducesCorrectOutput()
    {
        string fixturePath = FixturePath();
        Assert.True(File.Exists(fixturePath), $"Fixture missing at {fixturePath}.");

        TableCatalog catalog = CreateCatalogWithRealDispatcher();
        catalog.Add(new InMemoryTableProvider(
            CreatePool(), "data", ["d"], [new object?[] { 1 }]));

        catalog.Plan(
            $"CREATE MODEL softmax_chained(x Float32[]) RETURNS Float32[] " +
            $"USING 'file://{fixturePath}' " +
            $"AS BEGIN " +
            $"  DECLARE original Float32[] = x; " +
            $"  DECLARE dummy1 Float32[] = original; " +
            $"  DECLARE dummy2 Float32[] = original; " +
            $"  DECLARE result Float32[] = infer(original); " +
            $"  RETURN result " +
            $"END");

        IQueryPlan plan = catalog.Plan(
            "SELECT models.softmax_chained([CAST(1.0 AS Float32), CAST(2.0 AS Float32), CAST(3.0 AS Float32)]) FROM data");

        ExplainPlanNode tree = plan.ExplainTree;
        bool sawMio = false;
        WalkPlan(tree, n => { if (n.OperatorName == "Model Invocation") sawMio = true; });
        Assert.True(sawMio, "Plan tree must contain a Model Invocation operator (lowering removed; only MIO path remains).");

        // Execute end-to-end — every DECLARE binding must resolve across
        // the body's row-at-a-time interpretation.
        List<float[]> rows = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                DataValue cell = batch[i][0];
                rows.Add(cell.AsArraySpan<float>(batch.Arena).ToArray());
            }
        }

        Assert.Single(rows);
        float[] expected = SoftmaxReference([1.0f, 2.0f, 3.0f]);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.InRange(rows[0][i] - expected[i], -1e-5f, 1e-5f);
        }
    }

    private sealed class RecordingTracer : IModelInvocationTracer
    {
        public int StartedCount { get; private set; }
        public int CompletedCount { get; private set; }
        public List<string> ObservedModels { get; } = new();

        public void OnDispatchStarted(
            string modelName,
            int rowCount,
            IReadOnlyList<IReadOnlyList<DatumIngest.Functions.ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<DatumIngest.Functions.ValueRef>> overrides)
        {
            StartedCount++;
            ObservedModels.Add(modelName);
        }

        public void OnDispatchCompleted(string modelName, int rowCount, TimeSpan elapsed)
        {
            CompletedCount++;
        }

        public void OnDispatchFailed(string modelName, int rowCount, TimeSpan elapsed, Exception exception)
        {
        }
    }
}

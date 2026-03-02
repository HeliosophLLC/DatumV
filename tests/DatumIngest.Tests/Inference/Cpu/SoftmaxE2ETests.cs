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
    /// proving the step-2 adapter wiring still works for bodies that
    /// step 3's lowerer can't take. Straight-line bodies lower into
    /// <see cref="InferOperator"/> directly and never hit MIO; that path
    /// is covered by the lowered-plan probe below.
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
    /// Step 3 probe: a straight-line SQL-defined model body lowers into
    /// a plan with <see cref="InferOperator"/> nodes and no
    /// <see cref="ModelInvocationOperator"/>. Walking the plan's
    /// <c>ExplainTree</c> proves the body lowered — without step 3's
    /// post-pass the same query would surface a "Model Invocation"
    /// operator instead.
    /// </summary>
    [Fact]
    public async Task Softmax_StraightLineBody_LowersToInferOperator()
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

        Assert.True(sawInfer, "Plan tree must contain an Infer operator after lowering a straight-line body.");
        Assert.False(sawMio, "Plan tree must NOT contain a Model Invocation operator for a straight-line SQL-defined body.");

        // Execute too — the lowered plan must produce the same output as
        // the un-lowered path. Compare against the canonical softmax.
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
    /// Regression test for the 2-arg <c>infer(value, shape)</c> path.
    /// The softmax fixture's input shape is a single dynamic dim
    /// (<c>[None]</c>), so a body that passes an explicit shape exercises
    /// the lowerer's 2-arg detection + the InferOperator's per-row
    /// shape column without needing a multi-dynamic-dim ONNX. The
    /// explicit shape <c>[3]</c> matches the 3-element softmax input,
    /// so output is identical to the 1-arg form.
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
        bool sawInfer = false;
        bool sawShapeProperty = false;
        WalkPlan(tree, n =>
        {
            if (n.OperatorName == "Infer")
            {
                sawInfer = true;
                if (n.Properties?.ContainsKey("shape") == true) sawShapeProperty = true;
            }
        });
        Assert.True(sawInfer, "Plan tree must contain an Infer operator.");
        Assert.True(sawShapeProperty, "Infer operator must record the shape column when 2-arg infer() is used.");

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
    /// Regression test for the synthesized-column-passthrough bug
    /// (2026-05-16). A SQL-defined model body that references an earlier
    /// DECLARE's value <em>across an intervening DECLARE</em> requires
    /// each Project step to pass the prior synthesized hidden column
    /// through. Without explicit passthrough, <c>SELECT *</c> expansion
    /// in <see cref="ProjectOperator"/> filters <c>__</c>-prefixed names
    /// out, so the column survives one step (the next Project's expression
    /// reads from the upstream row directly) but vanishes from the second-
    /// next Project's upstream — its expression can't find it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The shape that triggers it.</strong> The PP-OCR-det body
    /// has this pattern: <c>DECLARE resized = ...; DECLARE rh = image_height(resized);
    /// DECLARE rw = image_width(resized); DECLARE tensor = image_to_tensor_chw(resized, ...)</c>.
    /// <c>tensor</c> references <c>resized</c> after <c>rh</c> and
    /// <c>rw</c> were declared — <c>__mb_resized</c> is two and three
    /// Projects back by then. Without explicit passthrough each
    /// intervening Project drops it.
    /// </para>
    /// <para>
    /// <strong>This regression test mirrors that shape.</strong>
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Softmax_MultiDeclareBody_PreservesSynthesizedColumnsAcrossSteps()
    {
        string fixturePath = FixturePath();
        Assert.True(File.Exists(fixturePath), $"Fixture missing at {fixturePath}.");

        TableCatalog catalog = CreateCatalogWithRealDispatcher();
        catalog.Add(new InMemoryTableProvider(
            CreatePool(), "data", ["d"], [new object?[] { 1 }]));

        // `original` is referenced by `result` AFTER `dummy1` and `dummy2`
        // were declared. Without explicit passthrough, the Project for
        // `dummy2` drops `__mb_..._original` from its output, so the
        // Project for `result` can't evaluate `ColumnReference(__mb_..._original)`
        // against its upstream row.
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

        // Confirm we're actually testing the lowered path — without this
        // assertion the test would silently pass even if the model fell
        // back to MIO + ProceduralModelAdapter (which doesn't exercise the
        // synth-column passthrough).
        ExplainPlanNode tree = plan.ExplainTree;
        bool sawInfer = false;
        WalkPlan(tree, n => { if (n.OperatorName == "Infer") sawInfer = true; });
        Assert.True(sawInfer, "Test must run via the lowered path; saw no Infer operator in the plan tree.");

        // Execute end-to-end — the lowered Project chain must keep every
        // synthesized column alive long enough for every later reference
        // to resolve.
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

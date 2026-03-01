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
    /// Step 2 probe: a SQL-defined model must now route through
    /// <c>ModelInvocationOperator</c> just like a built-in. Attaching an
    /// <see cref="IModelInvocationTracer"/> to the catalog and observing
    /// at least one <c>OnDispatchStarted</c>/<c>OnDispatchCompleted</c>
    /// pair proves the hoister picked up the <c>ProceduralModelAdapter</c>
    /// — without step 2's plumbing, the call would have stayed on the
    /// per-row scalar pipeline and the tracer would never fire.
    /// </summary>
    [Fact]
    public async Task Softmax_SqlDefined_RoutesThroughMioWithTracer()
    {
        string fixturePath = FixturePath();
        Assert.True(File.Exists(fixturePath),
            $"Fixture missing at {fixturePath}.");

        TableCatalog catalog = CreateCatalogWithRealDispatcher();
        catalog.Add(new InMemoryTableProvider(
            CreatePool(), "data", ["d"], [new object?[] { 1 }]));

        RecordingTracer tracer = new();
        catalog.ModelTracer = tracer;

        catalog.Plan(
            $"CREATE MODEL softmax(x Float32[]) RETURNS Float32[] " +
            $"USING 'file://{fixturePath}' " +
            $"AS BEGIN RETURN infer(x) END");

        IQueryPlan plan = catalog.Plan(
            "SELECT models.softmax([CAST(1.0 AS Float32), CAST(2.0 AS Float32), CAST(3.0 AS Float32)]) FROM data");

        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            // Drain — we care about the tracer events, not the values.
        }

        Assert.True(tracer.StartedCount > 0,
            "Tracer never fired OnDispatchStarted — the SQL-defined model was not hoisted into ModelInvocationOperator.");
        Assert.Equal(tracer.StartedCount, tracer.CompletedCount);
        Assert.Contains(tracer.ObservedModels, name =>
            string.Equals(name, "softmax", StringComparison.OrdinalIgnoreCase));
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

namespace Heliosoph.DatumV.Tests.Execution;

using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;

/// <summary>
/// Verifies that <see cref="ModelInvocationOperator"/> respects each
/// model's <see cref="IModel.PreferredBatchSize"/> by splitting upstream
/// batches into sub-batches and emitting one output <c>RowBatch</c> per
/// chunk. This is the streaming-UX optimisation: expensive models
/// (LLMs, image generators) emit results as each chunk completes rather
/// than waiting for a full 1024-row upstream batch.
/// </summary>
public sealed class PreferredBatchSizeTests : ServiceTestBase
{
    /// <summary>
    /// Synthetic model that records each batch size it receives. Lets
    /// tests assert "the operator split the upstream batch into chunks
    /// of size N" without needing a real ML backend.
    /// </summary>
    private sealed class RecordingModel : IModel
    {
        public string Name => "rec";
        public bool IsDeterministic => true;
        public IReadOnlyList<DataKind> InputKinds => [DataKind.String];
        public DataKind OutputKind => DataKind.String;
        public int? PreferredBatchSize { get; init; }

        public List<int> BatchSizesSeen { get; } = new();

        public Task<IReadOnlyList<ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
            CancellationToken cancellationToken)
        {
            _ = overrides;
            BatchSizesSeen.Add(inputs.Count);

            // Echo the input string back as the result (trivial transform).
            ValueRef[] results = new ValueRef[inputs.Count];
            for (int i = 0; i < inputs.Count; i++)
            {
                string text = inputs[i][0].AsString();
                results[i] = ValueRef.FromString($"echo:{text}");
            }
            return Task.FromResult<IReadOnlyList<ValueRef>>(results);
        }
    }

    private (TableCatalog, RecordingModel) BuildCatalogWithRecording(
        int rowCount, int? preferredBatchSize)
    {
        // Build a row set of the requested size.
        object?[][] rows = new object?[rowCount][];
        for (int i = 0; i < rowCount; i++)
        {
            rows[i] = new object?[] { $"row_{i}" };
        }

        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            rows: rows);

        RecordingModel model = new() { PreferredBatchSize = preferredBatchSize };
        ModelCatalog models = new(modelDirectory: System.IO.Path.GetTempPath());
        // These tests assert MIO's sub-batching shape directly, so they
        // need the deterministic StaticBatchSizePolicy rather than the
        // production DoublingBatchSizePolicy default. The doubling tuner
        // can't extract meaningful VRAM deltas from synthetic CPU-only
        // models and would settle at batch=1 across the board — correct
        // policy behaviour, wrong fixture for these tests.
        models.BatchSizePolicy = StaticBatchSizePolicy.Instance;
        models.Register(new ModelCatalogEntry(
            Name: "rec",
            Backend: "rec",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => model,
            OptionalArgKinds: null));
        catalog.Models = models;

        return (catalog, model);
    }

    /// <summary>
    /// Baseline: <c>PreferredBatchSize == null</c> means "process the whole
    /// upstream batch in one call" — preserves the pre-rebatching behaviour
    /// for cheap models (classifiers, detectors).
    /// </summary>
    [Fact]
    public async Task NullPreferredBatchSize_ProcessesWholeBatchAtOnce()
    {
        (TableCatalog catalog, RecordingModel model) = BuildCatalogWithRecording(
            rowCount: 10, preferredBatchSize: null);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT models.rec(name) FROM t", catalog);

        Assert.Equal(10, rows.Count);
        // One InferBatchAsync call with all 10 rows — no rebatching.
        Assert.Equal([10], model.BatchSizesSeen);
    }

    /// <summary>
    /// <c>PreferredBatchSize == 3</c> with 10 source rows yields 4 chunks
    /// of sizes [3, 3, 3, 1] — each becomes one InferBatchAsync call.
    /// Same total work, four streaming-friendly emissions instead of one.
    /// </summary>
    [Fact]
    public async Task PreferredBatchSize3_With10Rows_SplitsInto3_3_3_1()
    {
        (TableCatalog catalog, RecordingModel model) = BuildCatalogWithRecording(
            rowCount: 10, preferredBatchSize: 3);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT models.rec(name) FROM t", catalog);

        Assert.Equal(10, rows.Count);
        Assert.Equal([3, 3, 3, 1], model.BatchSizesSeen);
    }

    /// <summary>
    /// <c>PreferredBatchSize</c> larger than the upstream batch gracefully
    /// degrades to "process the whole batch as one chunk" — no error,
    /// no edge-case behaviour.
    /// </summary>
    [Fact]
    public async Task PreferredBatchSizeLargerThanUpstream_ProcessesAsOneChunk()
    {
        (TableCatalog catalog, RecordingModel model) = BuildCatalogWithRecording(
            rowCount: 5, preferredBatchSize: 100);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT models.rec(name) FROM t", catalog);

        Assert.Equal(5, rows.Count);
        // Only 5 rows available; chunk size caps at the available count.
        Assert.Equal([5], model.BatchSizesSeen);
    }

    /// <summary>
    /// <c>PreferredBatchSize == 1</c> (image-generator setting) with N
    /// rows yields N single-row InferBatchAsync calls. Each result
    /// streams back to the user as soon as it's produced — the whole
    /// point of the per-row sub-batch for ~1-2s per-call models.
    /// </summary>
    [Fact]
    public async Task PreferredBatchSize1_With4Rows_DispatchesPerRow()
    {
        (TableCatalog catalog, RecordingModel model) = BuildCatalogWithRecording(
            rowCount: 4, preferredBatchSize: 1);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT models.rec(name) FROM t", catalog);

        Assert.Equal(4, rows.Count);
        Assert.Equal([1, 1, 1, 1], model.BatchSizesSeen);
    }

    /// <summary>
    /// Sub-batching interacts correctly with <c>LIMIT</c> — when the
    /// downstream limit caps total rows, the operator stops dispatching
    /// after enough chunks to cover the limit. No wasted model calls.
    /// </summary>
    [Fact]
    public async Task PreferredBatchSize3_WithLimit5_StopsAfter5Rows()
    {
        (TableCatalog catalog, RecordingModel model) = BuildCatalogWithRecording(
            rowCount: 100, preferredBatchSize: 3);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT models.rec(name) FROM t LIMIT 5", catalog);

        Assert.Equal(5, rows.Count);
        // First chunk takes 3 rows, second takes 2 (capped by remaining-
        // to-limit). The operator stops after the second chunk emits.
        Assert.Equal([3, 2], model.BatchSizesSeen);
    }
}

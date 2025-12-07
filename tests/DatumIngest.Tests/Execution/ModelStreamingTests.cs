namespace DatumIngest.Tests.Execution;

using System.Runtime.CompilerServices;
using System.Text;

using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Pooling;

/// <summary>
/// End-to-end tests for the streaming path through
/// <see cref="IQueryPlan.ExecuteAsync(CancellationToken, IModelStreamingSink?)"/>.
/// Synthetic <see cref="EchoStreamingModel"/> yields each input character as
/// its own chunk so we can assert chunks arrive in order without depending on
/// real-model nondeterminism.
/// </summary>
public sealed class ModelStreamingTests : ServiceTestBase
{
    /// <summary>
    /// Streaming sink attached → operator drives <c>InferStreamingAsync</c>,
    /// chunks arrive at the sink in order, and the synthetic SELECT row's
    /// output column carries the concatenation.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithSink_ChunksArriveInOrderAndRowIsBuilt()
    {
        EchoStreamingModel model = new();
        ModelCatalog models = BuildModelCatalog(model);

        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "abc" });
        catalog.Models = models;

        IQueryPlan plan = catalog.Plan("SELECT models.echo_stream(name) FROM t");

        RecordingSink sink = new();
        List<string> rowOutputs = [];
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None, sink))
        {
            Arena arena = batch.Arena;
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                rowOutputs.Add(row[0].AsString(arena));
            }
        }

        // Three character chunks, in order.
        Assert.Equal(["a", "b", "c"], sink.Chunks);
        Assert.Equal(1, sink.CompletedCount);
        Assert.Null(sink.FailureException);

        // Synthetic SELECT row carries the concatenated value.
        Assert.Single(rowOutputs);
        Assert.Equal("abc", rowOutputs[0]);
    }

    /// <summary>
    /// No sink → operator stays on <c>InferBatchAsync</c>. The result row
    /// still carries the concatenated value because <c>InferBatchAsync</c>
    /// in <see cref="EchoStreamingModel"/> collects over its own streaming
    /// method (mirroring how <c>LlamaModel</c> works after Stage 1).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithoutSink_StillProducesCollectedRow()
    {
        EchoStreamingModel model = new();
        ModelCatalog models = BuildModelCatalog(model);

        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "hi" });
        catalog.Models = models;

        IQueryPlan plan = catalog.Plan("SELECT models.echo_stream(name) FROM t");

        List<string> rowOutputs = [];
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            Arena arena = batch.Arena;
            for (int i = 0; i < batch.Count; i++)
            {
                rowOutputs.Add(batch[i][0].AsString(arena));
            }
        }

        Assert.Single(rowOutputs);
        Assert.Equal("hi", rowOutputs[0]);
    }

    /// <summary>
    /// Sink attached but the model has no streaming override (default
    /// interface impl yields one chunk via <c>InferBatchAsync</c>): sink
    /// fires <see cref="IModelStreamingSink.OnChunk"/> exactly once, the
    /// row passes through unchanged. Confirms non-streaming models work
    /// transparently when EXEC is pointed at them.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithSink_NonStreamingModel_FiresExactlyOneChunk()
    {
        // EchoModel is the non-streaming singleton — inherits the default
        // IModel.InferStreamingAsync (single yield via InferBatchAsync).
        ModelCatalog models = new(modelDirectory: Path.GetTempPath());
        models.Register(new ModelCatalogEntry(
            Name: "echo",
            Backend: "echo",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => EchoModel.Instance));

        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" });
        catalog.Models = models;

        IQueryPlan plan = catalog.Plan("SELECT models.echo(name) FROM t");

        RecordingSink sink = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None, sink))
        {
            // Drain; row content is asserted via the sink's chunk record.
        }

        Assert.Single(sink.Chunks);
        Assert.Equal("alice", sink.Chunks[0]);
        Assert.Equal(1, sink.CompletedCount);
    }

    /// <summary>
    /// Sink attached but no model in the call (e.g. <c>EXEC upper('hi')</c>
    /// lowered to <c>SELECT upper('hi')</c>): no model invocation, no
    /// chunks. The plan still produces its synthetic row, which the shell's
    /// EXEC fallback path renders via <c>TableFormatter</c>.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithSink_NoModelInPlan_FiresNoChunks()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" });

        IQueryPlan plan = catalog.Plan("SELECT upper(name) FROM t");

        RecordingSink sink = new();
        List<string> rowOutputs = [];
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None, sink))
        {
            Arena arena = batch.Arena;
            for (int i = 0; i < batch.Count; i++)
            {
                rowOutputs.Add(batch[i][0].AsString(arena));
            }
        }

        Assert.Empty(sink.Chunks);
        Assert.Equal(0, sink.CompletedCount);
        Assert.Single(rowOutputs);
        Assert.Equal("ALICE", rowOutputs[0]);
    }

    private static ModelCatalog BuildModelCatalog(EchoStreamingModel model)
    {
        ModelCatalog catalog = new(modelDirectory: Path.GetTempPath());
        catalog.Register(new ModelCatalogEntry(
            Name: "echo_stream",
            Backend: "echo",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => model));
        return catalog;
    }

    /// <summary>
    /// Test double model: emits each character of its single string input
    /// as its own chunk, exercising the multi-chunk streaming path without
    /// requiring a real LLM. <see cref="InferBatchAsync"/> collects over
    /// the streaming path so non-sink callers (plain SELECT) still get
    /// the concatenated value.
    /// </summary>
    private sealed class EchoStreamingModel : IModel
    {
        public string Name => "echo_stream";
        public bool IsDeterministic => true;
        public IReadOnlyList<DataKind> InputKinds { get; } = [DataKind.String];
        public DataKind OutputKind => DataKind.String;

        public async IAsyncEnumerable<ValueRef> InferStreamingAsync(
            IReadOnlyList<ValueRef> rowInputs,
            IReadOnlyList<ValueRef> rowOverrides,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = rowOverrides;
            if (rowInputs.Count != 1) throw new InvalidOperationException("EchoStreamingModel expects one input");

            string text = rowInputs[0].AsString();
            for (int i = 0; i < text.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ValueRef.FromString(text[i].ToString());
                await Task.Yield();
            }
        }

        public async Task<IReadOnlyList<ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
            CancellationToken cancellationToken)
        {
            ValueRef[] outputs = new ValueRef[inputs.Count];
            for (int row = 0; row < inputs.Count; row++)
            {
                IReadOnlyList<ValueRef> rowOverrides = overrides.Count > row ? overrides[row] : [];
                StringBuilder sb = new();
                await foreach (ValueRef chunk in InferStreamingAsync(inputs[row], rowOverrides, cancellationToken))
                {
                    sb.Append(chunk.AsString());
                }
                outputs[row] = ValueRef.FromString(sb.ToString());
            }
            return outputs;
        }
    }

    /// <summary>
    /// Test double sink that records every chunk and lifecycle event so
    /// assertions can check both ordering and dispatch counts.
    /// </summary>
    private sealed class RecordingSink : IModelStreamingSink
    {
        public List<string> Chunks { get; } = [];
        public int CompletedCount { get; private set; }
        public Exception? FailureException { get; private set; }

        public void OnChunk(string modelName, ValueRef chunk)
            => Chunks.Add(chunk.AsString());

        public void OnCompleted(string modelName) => CompletedCount++;

        public void OnFailed(string modelName, Exception exception)
            => FailureException = exception;
    }
}

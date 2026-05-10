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
/// Tests for the model streaming surface — <see cref="IModel.InferStreamingAsync"/>
/// invoked directly, mirroring how external streaming consumers (e.g.
/// <c>LlamaLlmDriver</c> in the web layer) drive the path. SQL execution
/// always runs through the batched path; streaming consumers bypass SQL
/// and talk to the model interface directly.
/// </summary>
public sealed class ModelStreamingTests : ServiceTestBase
{
    /// <summary>
    /// Direct <c>InferStreamingAsync</c> call yields chunks in arrival order.
    /// This is the path real consumers take — no SQL, no operator chain,
    /// just iterating the async-enumerable on the model.
    /// </summary>
    [Fact]
    public async Task InferStreamingAsync_YieldsChunksInOrder()
    {
        EchoStreamingModel model = new();
        ValueRef[] rowInputs = [ValueRef.FromString("abc")];

        List<string> chunks = [];
        await foreach (ValueRef chunk in model.InferStreamingAsync(rowInputs, [], CancellationToken.None))
        {
            chunks.Add(chunk.AsString());
        }

        Assert.Equal(["a", "b", "c"], chunks);
    }

    /// <summary>
    /// Cancellation mid-stream stops the async-enumerable promptly. The
    /// streaming surface honours the token between yields — important for
    /// interactive consumers cancelling a long-running LLM generation.
    /// </summary>
    [Fact]
    public async Task InferStreamingAsync_HonoursCancellationBetweenChunks()
    {
        EchoStreamingModel model = new();
        ValueRef[] rowInputs = [ValueRef.FromString("hello")];

        using CancellationTokenSource cts = new();
        List<string> chunks = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (ValueRef chunk in model.InferStreamingAsync(rowInputs, [], cts.Token))
            {
                chunks.Add(chunk.AsString());
                if (chunks.Count == 2) cts.Cancel();
            }
        });

        Assert.Equal(2, chunks.Count);
    }

    /// <summary>
    /// SQL execution routes through the batched <c>InferBatchAsync</c> path
    /// (streaming was removed from SQL execution); the model's
    /// <c>InferBatchAsync</c> implementation collects over its own streaming
    /// method so the row carries the concatenated value end-to-end.
    /// </summary>
    [Fact]
    public async Task SqlSelect_RoutesThroughBatchedPath_ProducesCollectedRow()
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
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
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
    /// the streaming path so SQL execution still gets the concatenated value.
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
}

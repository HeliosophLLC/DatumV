using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Inference;
using Heliosoph.DatumV.Inference.OnnxRuntime;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.ModelLibrary;

using Microsoft.Extensions.Logging.Abstractions;

namespace Heliosoph.DatumV.Tests.Inference.Cpu;

/// <summary>
/// End-to-end test for the SQL-defined BGE reranker (XLM-RoBERTa cross-encoder).
/// Exercises the <c>tokenizer.encode_xlm_roberta_pair</c> path (SentencePiece
/// Unigram, <c>&lt;s&gt; q &lt;/s&gt;&lt;/s&gt; p &lt;/s&gt;</c> layout, 2-field
/// struct with no token_type_ids), the 2-input infer() on the reranker session,
/// and the <c>TextPairScorer</c> task contract.
/// </summary>
/// <remarks>
/// Skips when the ONNX file / tokenizer aren't downloaded. The relevance
/// assertion (a topically-matching passage must outscore an unrelated one) is
/// weight-driven but robust: a broken tokenizer would feed the cross-encoder
/// meaningless ids and collapse the ranking, so this doubles as a tokenizer
/// correctness check without pinning exact logit values.
/// </remarks>
[Trait("Category", "CpuInference")]
public sealed class BgeRerankerSqlE2ETests : ServiceTestBase
{
    private const string ModelId = "bge-reranker-base";
    private const string OnnxRelativePath = "onnx/model.onnx";

    private async Task<string?> TryEnsureModelAvailableAsync()
    {
        string onnxPath = GetDownloadedModelPath(ModelId, OnnxRelativePath);
        if (File.Exists(onnxPath))
        {
            return onnxPath;
        }

        try
        {
            await EnsureModelDownloadedAsync(ModelId);
        }
        catch
        {
            return null;
        }

        return File.Exists(onnxPath) ? onnxPath : null;
    }

    private string LoadCanonicalSql()
    {
        IManifestStore store = GetService<IManifestStore>();
        CatalogVariant model = store.Manifest.Entries.SelectMany(e => e.Variants).First(v => v.Id == ModelId);
        if (string.IsNullOrEmpty(model.InstallSql))
        {
            throw new InvalidOperationException(
                $"Catalog entry '{ModelId}' has no installSql; can't run the SQL E2E test.");
        }
        string sqlPath = Path.Combine(store.ManifestDirectory, model.InstallSql);
        return File.ReadAllText(sqlPath);
    }

    private TableCatalog CreateReadyCatalog()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Models = new Heliosoph.DatumV.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);
        catalog.Plan(LoadCanonicalSql());
        return catalog;
    }

    [Fact]
    public async Task BgeReranker_CreateModelStatement_ParsesAndRegisters()
    {
        string? onnxPath = await TryEnsureModelAvailableAsync();
        if (onnxPath is null) return;

        TableCatalog catalog = CreateReadyCatalog();

        Assert.True(catalog.DeclaredModels.TryGet(
            new QualifiedName("models", "bge_reranker_base"), out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("TextPairScorer", descriptor!.ImplementsTaskName);
        Assert.Equal("Float32", descriptor.ReturnTypeName);
    }

    [Fact]
    public async Task BgeReranker_RanksRelevantPassageAboveIrrelevant()
    {
        string? onnxPath = await TryEnsureModelAvailableAsync();
        if (onnxPath is null) return;

        TableCatalog catalog = CreateReadyCatalog();

        const string query = "how does photosynthesis work?";
        const string relevant =
            "Photosynthesis is the process by which green plants convert sunlight into chemical energy.";
        const string irrelevant =
            "The central bank raised interest rates again after the latest inflation report.";

        // Two rows in a fixed order: relevant first (rid 0), irrelevant second
        // (rid 1). Select rid alongside the score so the ordering is explicit
        // regardless of row-batch iteration.
        catalog.Add(new Heliosoph.DatumV.Catalog.Providers.InMemoryTableProvider(
            CreatePool(),
            "data",
            ["rid", "q", "p"],
            [DataKind.Int32, DataKind.String, DataKind.String],
            [
                new object?[] { 0, query, relevant },
                new object?[] { 1, query, irrelevant },
            ]));

        StatementPlan plan = catalog.Plan(
            "SELECT rid, models.bge_reranker_base(q, p) FROM data");

        Dictionary<int, float> scores = [];
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                int rid = batch[i][0].AsInt32();
                DataValue scoreCell = batch[i][1];
                Assert.Equal(DataKind.Float32, scoreCell.Kind);
                Assert.False(scoreCell.IsNull);
                scores[rid] = scoreCell.AsFloat32();
            }
        }

        Assert.True(scores.ContainsKey(0) && scores.ContainsKey(1), "expected both rows scored");
        Assert.True(scores[0] > scores[1],
            $"expected relevant passage to outscore irrelevant one, " +
            $"got relevant={scores[0]}, irrelevant={scores[1]}");
    }
}

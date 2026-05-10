using DatumIngest.Catalog;
using DatumIngest.Catalog.Registries;
using DatumIngest.Execution;
using DatumIngest.Inference;
using DatumIngest.Inference.OnnxRuntime;
using DatumIngest.Model;
using DatumIngest.ModelLibrary;

using Microsoft.Extensions.Logging.Abstractions;

namespace DatumIngest.Tests.Inference.Cpu;

/// <summary>
/// End-to-end test for the SQL-defined Twitter-RoBERTa sentiment classifier.
/// First RoBERTa-family migration: validates the new
/// <c>tokenizer.encode_roberta</c> path (BPE + <s>/</s> wrapping, 2-field
/// struct without token_type_ids), the multi-input infer() on a 2-input
/// session, and the <c>LabeledTextClassifier</c> task contract.
/// </summary>
/// <remarks>
/// Skips when the ONNX file isn't downloaded. Doesn't assert a specific
/// sentiment classification (depends on weights) — only that the output
/// shape and label vocabulary are correct.
/// </remarks>
[Trait("Category", "CpuInference")]
public sealed class TwitterRobertaSentimentSqlE2ETests : ServiceTestBase
{
    private const string ModelId = "twitter-roberta-sentiment";
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
        CatalogModel model = store.Manifest.Models.First(m => m.Id == ModelId);
        if (string.IsNullOrEmpty(model.InstallSql))
        {
            throw new InvalidOperationException(
                $"Catalog entry '{ModelId}' has no installSql; can't run the SQL E2E test.");
        }
        string sqlPath = Path.Combine(store.ManifestDirectory, model.InstallSql);
        return File.ReadAllText(sqlPath);
    }

    [Fact]
    public async Task TwitterRoberta_CreateModelStatement_ParsesAndRegisters()
    {
        string? onnxPath = await TryEnsureModelAvailableAsync();
        if (onnxPath is null) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new DatumIngest.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql());

        Assert.True(
            catalog.DeclaredModels.TryGet(new QualifiedName("models", "twitter_roberta_sentiment"), out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("LabeledTextClassifier", descriptor!.ImplementsTaskName);
        Assert.Equal("ScoredLabel", descriptor.ReturnTypeName);
    }

    [Fact]
    public async Task TwitterRoberta_SelectThroughMIO_ReturnsScoredLabel()
    {
        string? onnxPath = await TryEnsureModelAvailableAsync();
        if (onnxPath is null) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new DatumIngest.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql());

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(),
            "data",
            ["t"],
            [DataKind.String],
            [new object?[] { "I love this product, it's amazing!" }]));

        StatementPlan plan = catalog.Plan("SELECT models.twitter_roberta_sentiment(t) FROM data");

        bool sawRow = false;
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                sawRow = true;
                DataValue cell = batch[i][0];
                Assert.Equal(DataKind.Struct, cell.Kind);
                Assert.False(cell.IsNull);
                Assert.NotEqual(0, cell.TypeId);

                // Output is ScoredLabel: {label: String, score: Float32}.
                TypeDescriptor? desc = batch.Types!.GetDescriptor(cell.TypeId);
                Assert.NotNull(desc);
                Assert.NotNull(desc!.Fields);
                Assert.Equal(new[] { "label", "score" },
                    desc.Fields!.Select(f => f.Name).ToArray());

                DataValue[] fields = cell.AsStruct(batch.Arena);
                Assert.Equal(2, fields.Length);
                string label = fields[0].AsString();
                float score = fields[1].AsFloat32();

                // Label must be one of Cardiff NLP's TweetEval canonical
                // sentiment labels; score must be a valid softmax prob.
                Assert.Contains(label, new[] { "negative", "neutral", "positive" });
                Assert.InRange(score, 0f, 1f);
            }
        }
        Assert.True(sawRow, "expected at least one output row");
    }
}

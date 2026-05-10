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
/// End-to-end test for the SQL-defined Toxic-BERT 6-label classifier.
/// First multi-label migration: validates the new <c>multilabel_classify</c>
/// scalar (sigmoid + threshold + zip with labels) and the
/// <c>LabeledTextMultiClassifier</c> task contract returning
/// <c>Array&lt;ScoredLabel&gt;</c>.
/// </summary>
[Trait("Category", "CpuInference")]
public sealed class ToxicBertSqlE2ETests : ServiceTestBase
{
    private const string ModelId = "toxic-bert";
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
    public async Task ToxicBert_CreateModelStatement_ParsesAndRegisters()
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
            catalog.DeclaredModels.TryGet(new QualifiedName("models", "toxic_bert"), out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("LabeledTextMultiClassifier", descriptor!.ImplementsTaskName);
        Assert.Equal("Array<ScoredLabel>", descriptor.ReturnTypeName);
    }

    [Fact]
    public async Task ToxicBert_SelectThroughMIO_ReturnsArrayOfScoredLabel()
    {
        string? onnxPath = await TryEnsureModelAvailableAsync();
        if (onnxPath is null) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new DatumIngest.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql());

        // Two rows: one benign, one with overt toxicity. We don't assert
        // *which* labels fire (depends on weights + threshold) — only that
        // the output shape is correct and elements (when present) match
        // ScoredLabel.
        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(),
            "data",
            ["t"],
            [DataKind.String],
            [
                new object?[] { "Have a wonderful day everyone!" },
                new object?[] { "You are a stupid idiot and I hate you." },
            ]));

        IQueryPlan plan = catalog.Plan("SELECT models.toxic_bert(t) FROM data");

        HashSet<string> validLabels =
        [
            "toxic", "severe_toxic", "obscene", "threat", "insult", "identity_hate"
        ];

        int rowsSeen = 0;
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rowsSeen++;
                DataValue cell = batch[i][0];
                Assert.True(cell.IsArray, "expected Array<ScoredLabel>");
                Assert.Equal(DataKind.Struct, cell.Kind);
                Assert.False(cell.IsNull);

                DataValue[] elements = cell.AsStructArray(batch.Arena);
                foreach (DataValue element in elements)
                {
                    Assert.Equal(DataKind.Struct, element.Kind);
                    Assert.NotEqual(0, element.TypeId);

                    TypeDescriptor? desc = batch.Types!.GetDescriptor(element.TypeId);
                    Assert.NotNull(desc);
                    Assert.Equal(new[] { "label", "score" },
                        desc!.Fields!.Select(f => f.Name).ToArray());

                    DataValue[] fields = element.AsStruct(batch.Arena);
                    string label = fields[0].AsString();
                    float score = fields[1].AsFloat32();

                    Assert.Contains(label, validLabels);
                    // Above threshold 0.5 means score ∈ [0.5, 1].
                    Assert.InRange(score, 0.5f, 1f);
                }
            }
        }
        Assert.Equal(2, rowsSeen);
    }
}

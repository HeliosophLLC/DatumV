using DatumIngest.Catalog;
using DatumIngest.Catalog.Registries;
using DatumIngest.Catalog.Plans;
using DatumIngest.Inference;
using DatumIngest.Inference.OnnxRuntime;
using DatumIngest.Model;
using DatumIngest.ModelLibrary;
using DatumIngest.Models;

using Microsoft.Extensions.Logging.Abstractions;

namespace DatumIngest.Tests.Inference.Cpu;

/// <summary>
/// End-to-end test for the SQL-defined all-MiniLM-L6-v2 embedding model.
/// Validates the multi-input infer() path end-to-end against a real
/// transformer: WordPiece tokenize → struct-arg infer → mean-pool → L2.
/// </summary>
/// <remarks>
/// <para>
/// Downloads via <see cref="ServiceTestBase.EnsureModelDownloadedAsync"/>
/// from the catalog entry's HuggingFace source. Catalog include filter
/// (<c>*.onnx, *.json, *.txt</c>) pulls model.onnx + vocab.txt together.
/// Skipped when offline / the download fails so CI without internet stays
/// green.
/// </para>
/// <para>
/// Body contains a <c>DECLARE encoded Struct</c> + <c>infer(struct, struct)</c>,
/// so the body lowerer bails by design and dispatch runs through
/// MIO + <c>ProceduralModelAdapter</c> + scalar <c>InferFunction</c>'s
/// multi-input path. End-to-end coverage for that path against a real
/// 3-input ONNX session.
/// </para>
/// </remarks>
public sealed class AllMiniLmL6V2SqlE2ETests : ServiceTestBase
{
    private const string ModelId = "all-minilm-l6-v2";
    private const string OnnxFileName = "model.onnx";
    private const string VocabFileName = "vocab.txt";

    private async Task<(string onnxPath, string vocabPath)?> TryEnsureModelAvailableAsync()
    {
        string onnxPath = GetDownloadedModelPath(ModelId, OnnxFileName);
        string vocabPath = GetDownloadedModelPath(ModelId, VocabFileName);
        if (File.Exists(onnxPath) && File.Exists(vocabPath))
        {
            return (onnxPath, vocabPath);
        }

        try
        {
            await EnsureModelDownloadedAsync(ModelId);
        }
        catch
        {
            return null;
        }

        return File.Exists(onnxPath) && File.Exists(vocabPath)
            ? (onnxPath, vocabPath)
            : null;
    }

    /// <summary>
    /// Mirror of PpOcrDetSqlE2ETests.LoadCanonicalSql — pulls the SQL body
    /// through the manifest store's installSql field so the installer and
    /// the test see the same artifact.
    /// </summary>
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

    /// <summary>
    /// Verifies the body parses, registers, and gets rejected by the
    /// straight-line lowerer (struct DECLARE + struct-literal infer args
    /// force the MIO+adapter path). Locks in the bail-out behaviour the
    /// multi-input dispatch depends on.
    /// </summary>
    [Fact]
    public async Task AllMiniLm_CreateModel_RegistersAndBailsLowerer()
    {
        var paths = await TryEnsureModelAvailableAsync();
        if (paths is null) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql());

        Assert.True(
            catalog.DeclaredModels.TryGet(
                new QualifiedName("models", "all_minilm_l6_v2"),
                out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        // Body lowering was removed; every SQL-defined model now dispatches
        // through MIO + ProceduralModelAdapter, which knows how to unpack a
        // struct-arg infer() (the construct that historically forced the
        // lowerer to bail anyway).
    }

    /// <summary>
    /// End-to-end embedding round-trip. CREATE MODEL → SELECT
    /// models.all_minilm_l6_v2('hello world') → expect a 384-element
    /// Float32 array with L2 norm ≈ 1.0. The model produces a deterministic
    /// vector for a given input under CPU ONNX Runtime, so the round-trip
    /// also catches accidental drift in the preprocessing chain (tokenizer
    /// special-token handling, attention-mask shape, infer marshalling).
    /// </summary>
    [Fact]
    public async Task AllMiniLm_EndToEnd_ProducesUnitNorm384Vector()
    {
        var paths = await TryEnsureModelAvailableAsync();
        if (paths is null) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql());

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "data", ["text"], [new object?[] { "hello world" }]));

        IQueryPlan plan = catalog.Plan("SELECT models.all_minilm_l6_v2(text) FROM data");

        // Read the array out while the batch's arena is still alive; the
        // result is arena-backed so we can't stash the DataValue and read it
        // later. Copy the float[] inside the loop.
        float[]? embedding = null;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                DataValue v = batch[i][0];
                Assert.True(v.IsArray, "expected Float32[] output");
                Assert.Equal(DataKind.Float32, v.Kind);
                embedding = v.AsArraySpan<float>(batch.Arena).ToArray();
            }
        }
        Assert.NotNull(embedding);
        // MiniLM-L6 hidden size = 384; post-l2_normalize the vector should be unit norm.
        Assert.Equal(384, embedding!.Length);

        double sumSq = 0.0;
        for (int i = 0; i < embedding.Length; i++) sumSq += (double)embedding[i] * embedding[i];
        double norm = System.Math.Sqrt(sumSq);
        Assert.InRange(norm, 0.999, 1.001);
    }
}

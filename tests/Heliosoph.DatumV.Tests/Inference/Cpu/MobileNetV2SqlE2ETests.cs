using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Inference;
using Heliosoph.DatumV.Inference.OnnxRuntime;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.ModelLibrary;

using Microsoft.Extensions.Logging.Abstractions;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Inference.Cpu;

/// <summary>
/// End-to-end tests for the SQL-defined MobileNetV2 image classifier.
/// Locks in the CREATE MODEL body shape, the MIO+adapter routing
/// (catalog-relative labels file forces the bail), and the
/// <c>ScoredLabel = Struct&lt;label: String, score: Float32&gt;</c>
/// output roundtrip.
/// </summary>
/// <remarks>
/// Deletion-candidate test for the MobileNetV2 migration: when this
/// passes, <c>Models/Onnx/MobileNetV2Model.cs</c> has already been
/// removed. Self-skips when the ONNX bundle isn't downloaded so CI
/// without internet stays green.
/// </remarks>
[Trait("Category", "CpuInference")]
public sealed class MobileNetV2SqlE2ETests : ServiceTestBase
{
    private const string ModelId = "mobilenetv2";
    private const string OnnxFileName = "mobilenetv2-12.onnx";

    private async Task<string?> TryEnsureModelAvailableAsync()
    {
        string onnxPath = GetDownloadedModelPath(ModelId, OnnxFileName);
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

    private static SKBitmap MakeSyntheticImage(int width = 224, int height = 224)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(bmp);
        canvas.Clear(SKColors.LightYellow);
        // A simple rectangle gives the classifier something to react to;
        // we don't assert on the predicted label (depends on weights).
        using SKPaint p = new() { Color = SKColors.SaddleBrown };
        canvas.DrawRect(new SKRect(40, 40, 184, 184), p);
        return bmp;
    }

    [Fact]
    public async Task MobileNetV2_CreateModelStatement_ParsesAndBailsToMioAdapter()
    {
        string? onnxPath = await TryEnsureModelAvailableAsync();
        if (onnxPath is null) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new Heliosoph.DatumV.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql());

        Assert.True(
            catalog.DeclaredModels.TryGet(new QualifiedName("models", "mobilenetv2"), out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("LabeledImageClassifier", descriptor!.ImplementsTaskName);
        Assert.Equal("ScoredLabel", descriptor.ReturnTypeName);
        // 5 DECLAREs (tensor, logits, probs, top, labels) + 1 RETURN.
        Assert.Equal(6, descriptor.StatementBody.Count);
        // Body lowering was removed; every SQL-defined model now dispatches
        // through MIO + ProceduralModelAdapter, which carries frame.CurrentModel
        // through for catalog-relative scalars like read_string_list.
    }

    [Fact]
    public async Task MobileNetV2_SelectThroughMIO_PreservesScoredLabelShape()
    {
        string? onnxPath = await TryEnsureModelAvailableAsync();
        if (onnxPath is null) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new Heliosoph.DatumV.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql());

        using SKBitmap bmp = MakeSyntheticImage();
        using SKData encoded = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90);
        byte[] imageBytes = encoded.ToArray();

        catalog.Add(new Heliosoph.DatumV.Catalog.Providers.InMemoryTableProvider(
            CreatePool(),
            "data",
            ["img"],
            [DataKind.Image],
            [new object?[] { imageBytes }]));

        StatementPlan plan = catalog.Plan("SELECT models.mobilenetv2(img) FROM data");

        bool sawRow = false;
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                sawRow = true;
                DataValue cell = batch[i][0];
                Assert.False(cell.IsNull, "expected non-null ScoredLabel output");
                Assert.Equal(DataKind.Struct, cell.Kind);
                Assert.NotEqual(0, cell.TypeId);

                // The output is a ScoredLabel struct: {label: String, score: Float32}.
                TypeDescriptor? desc = batch.Types!.GetDescriptor(cell.TypeId);
                Assert.NotNull(desc);
                Assert.NotNull(desc!.Fields);
                Assert.Equal(new[] { "label", "score" },
                    desc.Fields!.Select(f => f.Name).ToArray());

                // Label is non-empty (came from imagenet-classes.json via
                // read_string_list); score is in [0, 1] (post-softmax).
                DataValue[] fields = cell.AsStruct(batch.Arena);
                Assert.Equal(2, fields.Length);
                Assert.False(string.IsNullOrEmpty(fields[0].AsString()),
                    "label should resolve to an ImageNet class name from the bundled JSON");
                float score = fields[1].AsFloat32();
                Assert.InRange(score, 0f, 1f);
            }
        }
        Assert.True(sawRow, "expected at least one output row");
    }
}

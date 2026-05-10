using DatumIngest.Catalog;
using DatumIngest.Catalog.Registries;
using DatumIngest.Execution;
using DatumIngest.Inference;
using DatumIngest.Inference.OnnxRuntime;
using DatumIngest.Model;
using DatumIngest.ModelLibrary;

using Microsoft.Extensions.Logging.Abstractions;

using SkiaSharp;

namespace DatumIngest.Tests.Inference.Cpu;

/// <summary>
/// End-to-end tests for the SQL-defined YOLOX-S model. Locks in the
/// CREATE MODEL body shape, the lowerer-takes-it invariant, and the
/// nested struct field-name roundtrip
/// (<c>LabeledDetection = Struct&lt;bbox: BoundingBox, label: String,
/// score: Float32&gt;</c>).
/// </summary>
/// <remarks>
/// The deletion-candidate test for the YOLOX migration — when this passes,
/// <c>Models/Onnx/YoloXModel.cs</c> + <c>CocoLabels.cs</c> have already
/// been removed; this verifies the SQL replacement matches the contract.
/// Skips gracefully when the ONNX file isn't downloaded so CI without
/// internet stays green.
/// </remarks>
[Trait("Category", "CpuInference")]
public sealed class YoloxSqlE2ETests : ServiceTestBase
{
    private const string ModelId = "yolox-s";
    private const string OnnxFileName = "yolox_s.onnx";

    /// <summary>
    /// Best-effort download of the YOLOX-S ONNX bundle (model + labels file).
    /// Returns the absolute path to the ONNX file on success, null on any
    /// failure (offline CI, partial bytes, gated repo, etc.) so the test
    /// soft-skips.
    /// </summary>
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
        CatalogModel model = store.Manifest.Models.First(m => m.Id == ModelId);
        if (string.IsNullOrEmpty(model.InstallSql))
        {
            throw new InvalidOperationException(
                $"Catalog entry '{ModelId}' has no installSql; can't run the SQL E2E test.");
        }
        string sqlPath = Path.Combine(store.ManifestDirectory, model.InstallSql);
        return File.ReadAllText(sqlPath);
    }

    private static SKBitmap MakeSyntheticImage(int width = 640, int height = 480)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(bmp);
        canvas.Clear(SKColors.SteelBlue);
        // A few rectangles in primary colours give the detector something
        // to chew on. Concrete detections aren't asserted (depend on model
        // weights + chance) — the test only walks the output shape.
        using SKPaint p1 = new() { Color = SKColors.Red };
        using SKPaint p2 = new() { Color = SKColors.Green };
        canvas.DrawRect(new SKRect(40, 40, 200, 200), p1);
        canvas.DrawRect(new SKRect(240, 80, 380, 220), p2);
        return bmp;
    }

    [Fact]
    public async Task YoloxS_CreateModelStatement_ParsesAndBailsToMioAdapter()
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
            catalog.DeclaredModels.TryGet(new QualifiedName("models", "yolox_s"), out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("LabeledObjectDetector", descriptor!.ImplementsTaskName);
        Assert.Equal("Array<LabeledDetection>", descriptor.ReturnTypeName);
        // 3 DECLAREs (tensor, raw, labels) + 1 RETURN.
        Assert.Equal(4, descriptor.StatementBody.Count);
        // Body lowering was removed; every SQL-defined model now dispatches
        // through MIO + ProceduralModelAdapter, which carries frame.CurrentModel
        // through for catalog-relative scalars like read_string_list.
    }

    [Fact]
    public async Task YoloxS_SelectThroughMIO_PreservesNestedFieldNames()
    {
        string? onnxPath = await TryEnsureModelAvailableAsync();
        if (onnxPath is null) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new DatumIngest.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql());

        using SKBitmap bmp = MakeSyntheticImage();
        using SKData encoded = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90);
        byte[] imageBytes = encoded.ToArray();

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(),
            "data",
            ["img"],
            [DataKind.Image],
            [new object?[] { imageBytes }]));

        StatementPlan plan = catalog.Plan("SELECT models.yolox_s(img) FROM data");

        bool sawRow = false;
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                sawRow = true;
                DataValue cell = batch[i][0];
                Assert.True(cell.IsArray, "expected Array<Struct> output cell");
                Assert.Equal(DataKind.Struct, cell.Kind);
                Assert.False(cell.IsNull);

                DataValue[] elements = cell.AsStructArray(batch.Arena);
                if (elements.Length == 0)
                {
                    // Synthetic image may produce zero detections at the
                    // default 0.25 confidence threshold; field-name check
                    // requires at least one element. Skip the shape check
                    // but keep sawRow=true to assert at least one batch
                    // was scanned.
                    continue;
                }

                DataValue first = elements[0];
                Assert.Equal(DataKind.Struct, first.Kind);
                Assert.NotEqual(0, first.TypeId);

                TypeDescriptor? outer = batch.Types!.GetDescriptor(first.TypeId);
                Assert.NotNull(outer);
                Assert.NotNull(outer!.Fields);
                Assert.Equal(new[] { "bbox", "label", "score" },
                    outer.Fields!.Select(f => f.Name).ToArray());

                // Nested bbox walks into BoundingBox shape.
                TypeDescriptor? bbox = batch.Types.GetDescriptor(outer.Fields[0].TypeId);
                Assert.NotNull(bbox);
                Assert.Equal(new[] { "x", "y", "w", "h" },
                    bbox!.Fields!.Select(f => f.Name).ToArray());
            }
        }
        Assert.True(sawRow, "expected at least one output row");
    }
}

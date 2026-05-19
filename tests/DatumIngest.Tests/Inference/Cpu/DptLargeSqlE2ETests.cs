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
/// End-to-end tests for the SQL-defined DPT-Large monocular depth
/// estimator. Locks in the CREATE MODEL body shape, the MIO+adapter
/// bail (catalog-relative resolution is required for the ONNX file
/// lookup inside the model body's USING path) and the
/// <c>RETURNS Image</c> output roundtrip.
/// </summary>
/// <remarks>
/// Deletion-candidate test for the depth migration. Self-skips when
/// the ONNX file isn't downloaded. The companion MiDaS-small SQL file
/// shares the same shape; covering DPT-Large gives us the lowering +
/// scatter parity for both, plus the `image_to_tensor_chw_bgr` path
/// is covered separately at the unit level.
/// </remarks>
[Trait("Category", "CpuInference")]
public sealed class DptLargeSqlE2ETests : ServiceTestBase
{
    private const string ModelId = "dpt-large";
    private const string OnnxFileName = "dpt_large_384.onnx";

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

    private static SKBitmap MakeSyntheticImage(int width = 384, int height = 384)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(bmp);
        canvas.Clear(SKColors.LightGray);
        // Draw a few rectangles to give the depth estimator something to
        // chew on — concrete depth values aren't asserted, only shape.
        using SKPaint p1 = new() { Color = SKColors.DarkSlateBlue };
        using SKPaint p2 = new() { Color = SKColors.WhiteSmoke };
        canvas.DrawRect(new SKRect(40, 40, 200, 200), p1);
        canvas.DrawRect(new SKRect(220, 80, 360, 320), p2);
        return bmp;
    }

    [Fact]
    public async Task DptLarge_CreateModelStatement_ParsesAndRegisters()
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
            catalog.DeclaredModels.TryGet(new QualifiedName("models", "dpt_large"), out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("DepthEstimator", descriptor!.ImplementsTaskName);
        Assert.Equal("Image", descriptor.ReturnTypeName);
        // 2 DECLAREs (tensor, depth) + 1 RETURN.
        Assert.Equal(3, descriptor.StatementBody.Count);
    }

    [Fact]
    public async Task DptLarge_SelectThroughMIO_ProducesResizedDepthImage()
    {
        string? onnxPath = await TryEnsureModelAvailableAsync();
        if (onnxPath is null) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new Heliosoph.DatumV.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql());

        // 480x320 input — depth_map_to_image should resize the 384x384
        // network-resolution output back to 480x320.
        using SKBitmap bmp = MakeSyntheticImage(width: 480, height: 320);
        using SKData encoded = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90);
        byte[] imageBytes = encoded.ToArray();

        catalog.Add(new Heliosoph.DatumV.Catalog.Providers.InMemoryTableProvider(
            CreatePool(),
            "data",
            ["img"],
            [DataKind.Image],
            [new object?[] { imageBytes }]));

        StatementPlan plan = catalog.Plan("SELECT models.dpt_large(img) FROM data");

        bool sawRow = false;
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                sawRow = true;
                DataValue cell = batch[i][0];
                Assert.Equal(DataKind.Image, cell.Kind);
                Assert.False(cell.IsNull);

                // Decode + check dimensions match the input image.
                byte[] outBytes = cell.AsImage(batch.Arena);
                using SKBitmap depthImage = SKBitmap.Decode(outBytes);
                Assert.NotNull(depthImage);
                Assert.Equal(480, depthImage.Width);
                Assert.Equal(320, depthImage.Height);
            }
        }
        Assert.True(sawRow, "expected at least one output row");
    }
}

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
/// End-to-end test for the SQL-defined U²-Net Lite (u2netp) salient-object
/// segmentation model. Picks the lite variant (~5 MB, ~10x faster than
/// full u2net) for CI cycle time; full u2net shares the same SQL body
/// modulo the ONNX path so coverage of one variant covers the other's
/// composition.
/// </summary>
/// <remarks>
/// Deletion-candidate test for the U²-Net migration. Verifies the body
/// composes existing scalars without any new C# (image_to_tensor_chw +
/// infer + depth_map_to_image) and produces a mask image at the input's
/// original dimensions. Self-skips when the ONNX file isn't downloaded.
/// </remarks>
[Trait("Category", "CpuInference")]
public sealed class U2NetpSqlE2ETests : ServiceTestBase
{
    private const string ModelId = "u2netp";
    private const string OnnxFileName = "u2netp.onnx";

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

    private static SKBitmap MakeSyntheticImage(int width = 320, int height = 320)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(bmp);
        canvas.Clear(SKColors.LightSkyBlue);
        // A foreground subject (filled circle on a contrasting background) gives
        // the salient-object detector something to lock onto. Concrete mask
        // values aren't asserted — only the output shape.
        using SKPaint p = new() { Color = SKColors.SaddleBrown, IsAntialias = true };
        canvas.DrawCircle(width / 2f, height / 2f, MathF.Min(width, height) / 4f, p);
        return bmp;
    }

    [Fact]
    public async Task U2Netp_CreateModelStatement_ParsesAndRegisters()
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
            catalog.DeclaredModels.TryGet(new QualifiedName("models", "u2netp"), out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("BackgroundRemover", descriptor!.ImplementsTaskName);
        Assert.Equal("Image", descriptor.ReturnTypeName);
        // 2 DECLAREs (tensor, mask) + 1 RETURN.
        Assert.Equal(3, descriptor.StatementBody.Count);
    }

    [Fact]
    public async Task U2Netp_SelectThroughMIO_ProducesResizedMaskImage()
    {
        string? onnxPath = await TryEnsureModelAvailableAsync();
        if (onnxPath is null) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new DatumIngest.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql());

        // 400x300 input — depth_map_to_image should resize the 320x320
        // network-resolution mask back to 400x300.
        using SKBitmap bmp = MakeSyntheticImage(width: 400, height: 300);
        using SKData encoded = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90);
        byte[] imageBytes = encoded.ToArray();

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(),
            "data",
            ["img"],
            [DataKind.Image],
            [new object?[] { imageBytes }]));

        StatementPlan plan = catalog.Plan("SELECT models.u2netp(img) FROM data");

        bool sawRow = false;
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                sawRow = true;
                DataValue cell = batch[i][0];
                Assert.Equal(DataKind.Image, cell.Kind);
                Assert.False(cell.IsNull);

                byte[] outBytes = cell.AsImage(batch.Arena);
                using SKBitmap maskImage = SKBitmap.Decode(outBytes);
                Assert.NotNull(maskImage);
                Assert.Equal(400, maskImage.Width);
                Assert.Equal(300, maskImage.Height);
            }
        }
        Assert.True(sawRow, "expected at least one output row");
    }
}

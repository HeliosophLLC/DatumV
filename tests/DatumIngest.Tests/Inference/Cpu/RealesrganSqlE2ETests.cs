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
/// End-to-end test for the SQL-defined Real-ESRGAN-x4v3 super-resolution
/// model. Validates the dynamic-spatial-dim infer() flow (input shape
/// passed as <c>[1, 3, ih, iw]</c> derived from the row's image dims),
/// the no-resize <c>image_to_tensor_chw</c> path (target_size = original
/// HxW), and the 4x output dimensions through <c>tensor_to_image_chw</c>.
/// </summary>
/// <remarks>
/// Deletion-candidate test for the Real-ESRGAN migration. Pure
/// composition of existing scalars — no new model-specific functions.
/// Self-skips when the ONNX file isn't downloaded.
/// </remarks>
[Trait("Category", "CpuInference")]
public sealed class RealesrganSqlE2ETests : ServiceTestBase
{
    private const string ModelId = "realesrgan-x4v3";
    private const string OnnxFileName = "realesr-general-x4v3.onnx";

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

    private static SKBitmap MakeSyntheticImage(int width = 64, int height = 48)
    {
        // Tiny input — the 4x upscale keeps the test fast (256x192 output).
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(bmp);
        canvas.Clear(SKColors.LightYellow);
        using SKPaint p = new() { Color = SKColors.DarkRed, IsAntialias = true };
        canvas.DrawCircle(width / 2f, height / 2f, MathF.Min(width, height) / 3f, p);
        return bmp;
    }

    [Fact]
    public async Task Realesrgan_CreateModelStatement_ParsesAndRegisters()
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
            catalog.DeclaredModels.TryGet(new QualifiedName("models", "realesrgan_x4v3"), out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("ImageUpscaler", descriptor!.ImplementsTaskName);
        Assert.Equal("Image", descriptor.ReturnTypeName);
        // 4 DECLAREs (iw, ih, tensor, upscaled) + 1 RETURN.
        Assert.Equal(5, descriptor.StatementBody.Count);
    }

    [Fact]
    public async Task Realesrgan_SelectThroughMIO_Produces4xImage()
    {
        string? onnxPath = await TryEnsureModelAvailableAsync();
        if (onnxPath is null) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new DatumIngest.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql());

        // 64x48 input → 256x192 output (4x on each axis).
        using SKBitmap bmp = MakeSyntheticImage(width: 64, height: 48);
        using SKData encoded = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90);
        byte[] imageBytes = encoded.ToArray();

        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(),
            "data",
            ["img"],
            [DataKind.Image],
            [new object?[] { imageBytes }]));

        StatementPlan plan = catalog.Plan("SELECT models.realesrgan_x4v3(img) FROM data");

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
                using SKBitmap upscaled = SKBitmap.Decode(outBytes);
                Assert.NotNull(upscaled);
                Assert.Equal(256, upscaled.Width);  // 64 x 4
                Assert.Equal(192, upscaled.Height); // 48 x 4
            }
        }
        Assert.True(sawRow, "expected at least one output row");
    }
}

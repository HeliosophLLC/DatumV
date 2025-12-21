namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;
using DatumIngest.Pooling;

using SkiaSharp;

/// <summary>
/// Smoke tests for SD-Turbo. These tests run actual diffusion inference
/// against the local model directory; they self-skip when the files
/// aren't present so CI machines without the artefacts don't fail.
/// </summary>
public sealed class StableDiffusionTurboModelTests : ServiceTestBase
{
    private static string ModelDirectory => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.SdTurboFolder);

    private static string AnchorFilePath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.SdTurboAnchor);

    private static bool ModelAvailable => File.Exists(AnchorFilePath);

    /// <summary>
    /// Cheapest signal: all three ONNX sessions plus the CLIP tokenizer
    /// load without exception. Verifies the diffusers folder layout
    /// resolution is correct.
    /// </summary>
    [Fact]
    public void Load_RealSdTurbo_ExposesExpectedSignature()
    {
        if (!ModelAvailable) return;

        using StableDiffusionTurboModel model = new(name: "sd_turbo", modelDirectory: ModelDirectory);

        Assert.Equal("sd_turbo", model.Name);
        Assert.False(model.IsDeterministic);
        Assert.Equal(DataKind.Image, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.String, model.InputKinds[0]);
        Assert.Equal(1, model.PreferredBatchSize);
    }

    /// <summary>
    /// End-to-end on a real prompt: the full pipeline (tokenize → text encode →
    /// noise sample → UNet → VAE decode) produces a 512×512 SKBitmap.
    /// </summary>
    /// <remarks>
    /// This is slow — ~2-5 seconds per generation depending on hardware. We
    /// fix the seed for reproducibility but don't pin specific pixel values
    /// (those vary across ONNX Runtime versions and GPU drivers). The check
    /// is "did the pipeline produce a valid 512×512 image."
    /// </remarks>
    [Fact]
    public async Task InferBatch_SimplePrompt_ReturnsValid512x512Image()
    {
        if (!ModelAvailable) return;

        // Fixed seed → reproducible noise pattern (helps debug if the test
        // fails on specific pipeline outputs).
        using StableDiffusionTurboModel model = new(
            name: "sd_turbo",
            modelDirectory: ModelDirectory,
            seed: 42);

        DatumIngest.Functions.ValueRef[][] inputs =
            [[DatumIngest.Functions.ValueRef.FromString("a red apple on a wooden table")]];
        IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
            inputs, overrides: [], cancellationToken: CancellationToken.None);

        DatumIngest.Functions.ValueRef result = Assert.Single(outputs);
        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);

        SKBitmap bitmap = result.AsImage();
        Assert.NotNull(bitmap);
        Assert.Equal(512, bitmap.Width);
        Assert.Equal(512, bitmap.Height);
    }

    /// <summary>
    /// Two-row batch: confirms the per-prompt loop in <see cref="StableDiffusionTurboModel.InferBatchAsync"/>
    /// produces one image per row. Different prompts should produce
    /// different pixel data (with the same seed, different prompts mean
    /// different text embeddings → different UNet output).
    /// </summary>
    [Fact]
    public async Task InferBatch_TwoDifferentPrompts_ProducesTwoDistinctImages()
    {
        if (!ModelAvailable) return;

        using StableDiffusionTurboModel model = new(
            name: "sd_turbo",
            modelDirectory: ModelDirectory,
            seed: 42);

        DatumIngest.Functions.ValueRef[][] inputs =
        [
            [DatumIngest.Functions.ValueRef.FromString("a sunset over mountains")],
            [DatumIngest.Functions.ValueRef.FromString("a cat sitting on a chair")],
        ];

        IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
            inputs, overrides: [], cancellationToken: CancellationToken.None);

        Assert.Equal(2, outputs.Count);
        SKBitmap image1 = outputs[0].AsImage();
        SKBitmap image2 = outputs[1].AsImage();

        Assert.Equal(512, image1.Width);
        Assert.Equal(512, image2.Width);
        // Different prompts must produce different pixel data.
        Assert.NotEqual(image1.Bytes, image2.Bytes);
    }

    /// <summary>
    /// Catalog round-trip: registering SD-Turbo via <see cref="BuiltinModels"/>
    /// resolves to a usable <see cref="StableDiffusionTurboModel"/> with
    /// the correct metadata.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolve_YieldsSdTurboModel()
    {
        if (!ModelAvailable) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterSdTurbo(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("sd_turbo");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.SdTurboAnchor, entry.RelativePath);
        Assert.Equal("generator", entry.Category);
        Assert.NotNull(entry.Modalities);
        Assert.Contains("text", entry.Modalities!);
        Assert.Contains("image", entry.Modalities!);
        Assert.False(entry.IsDeterministic);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("sd_turbo");
        IModel model = lease.Model;
        Assert.IsType<StableDiffusionTurboModel>(model);
        Assert.Equal(DataKind.Image, model.OutputKind);
    }
}

namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;

using SkiaSharp;

/// <summary>
/// Smoke tests for the Realistic Vision V6 + Hyper-SD registration. The
/// pipeline shape is identical to SD-Turbo (same StableDiffusionTurboModel
/// loader), so these tests exercise the catalog wiring and confirm the
/// SD 1.5 export loads end-to-end. They self-skip when the files aren't
/// present so CI machines without the artefacts don't fail.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class RealisticVisionHyperModelTests : ServiceTestBase
{
    private static string ModelDirectory => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.RealisticVisionHyperFolder);

    private static string AnchorFilePath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.RealisticVisionHyperAnchor);

    private static bool ModelAvailable => File.Exists(AnchorFilePath);

    [Fact]
    public void Load_RealisticVisionHyper_ExposesExpectedSignature()
    {
        if (!ModelAvailable) return;

        using StableDiffusionTurboModel model = new(name: "realistic_vision_hyper", modelDirectory: ModelDirectory);

        Assert.Equal("realistic_vision_hyper", model.Name);
        Assert.False(model.IsDeterministic);
        Assert.Equal(DataKind.Image, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.String, model.InputKinds[0]);
        Assert.Equal(1, model.PreferredBatchSize);
    }

    /// <summary>
    /// End-to-end: tokenize → encode (CLIP-L, 768-dim) → noise → UNet → VAE
    /// → 512×512 SKBitmap. The 768 vs 1024 hidden-dim difference vs SD-Turbo
    /// is invisible to the C# pipeline; this test confirms ORT accepts the
    /// shape that the SD 1.5 text encoder export produces.
    /// </summary>
    [Fact]
    public async Task InferBatch_SimplePrompt_ReturnsValid512x512Image()
    {
        if (!ModelAvailable) return;

        using StableDiffusionTurboModel model = new(
            name: "realistic_vision_hyper",
            modelDirectory: ModelDirectory,
            seed: 42);

        DatumIngest.Functions.ValueRef[][] inputs =
            [[DatumIngest.Functions.ValueRef.FromString("a portrait of a halfling rogue, dark hair, leather armor")]];
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

    [Fact]
    public async Task InferBatch_TwoDifferentPrompts_ProducesTwoDistinctImages()
    {
        if (!ModelAvailable) return;

        using StableDiffusionTurboModel model = new(
            name: "realistic_vision_hyper",
            modelDirectory: ModelDirectory,
            seed: 42);

        DatumIngest.Functions.ValueRef[][] inputs =
        [
            [DatumIngest.Functions.ValueRef.FromString("a wizard casting a spell in a dimly lit library")],
            [DatumIngest.Functions.ValueRef.FromString("a dwarf blacksmith hammering a glowing blade")],
        ];

        IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
            inputs, overrides: [], cancellationToken: CancellationToken.None);

        Assert.Equal(2, outputs.Count);
        SKBitmap image1 = outputs[0].AsImage();
        SKBitmap image2 = outputs[1].AsImage();

        Assert.Equal(512, image1.Width);
        Assert.Equal(512, image2.Width);
        Assert.NotEqual(image1.Bytes, image2.Bytes);
    }

    [Fact]
    public void Catalog_RegisterAndResolve_YieldsRealisticVisionHyperModel()
    {
        if (!ModelAvailable) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterRealisticVisionHyper(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("realistic_vision_hyper");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.RealisticVisionHyperAnchor, entry.RelativePath);
        Assert.Equal("generator", entry.Category);
        Assert.NotNull(entry.Modalities);
        Assert.Contains("text", entry.Modalities!);
        Assert.Contains("image", entry.Modalities!);
        Assert.False(entry.IsDeterministic);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("realistic_vision_hyper");
        IModel model = lease.Model;
        Assert.IsType<StableDiffusionTurboModel>(model);
        Assert.Equal(DataKind.Image, model.OutputKind);
    }
}

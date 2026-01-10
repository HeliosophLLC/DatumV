namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;

using SkiaSharp;

/// <summary>
/// Smoke tests for the Mo Di Diffusion + Hyper-SD registration. Same SD 1.5
/// pipeline shape as the other SD-1.5+Hyper exports (StableDiffusionTurboModel
/// loader). Self-skip when the files aren't present.
/// </summary>
/// <remarks>
/// Prompts in these tests use the <c>"modern disney style"</c> trigger token
/// to fully exercise Mo Di's characteristic look — without it the model
/// produces reasonable but not-particularly-Disney images.
/// </remarks>
public sealed class MoDiHyperModelTests : ServiceTestBase
{
    private static string ModelDirectory => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.MoDiHyperFolder);

    private static string AnchorFilePath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.MoDiHyperAnchor);

    private static bool ModelAvailable => File.Exists(AnchorFilePath);

    [Fact]
    public void Load_MoDiHyper_ExposesExpectedSignature()
    {
        if (!ModelAvailable) return;

        using StableDiffusionTurboModel model = new(name: "mo_di_hyper", modelDirectory: ModelDirectory);

        Assert.Equal("mo_di_hyper", model.Name);
        Assert.False(model.IsDeterministic);
        Assert.Equal(DataKind.Image, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.String, model.InputKinds[0]);
        Assert.Equal(1, model.PreferredBatchSize);
    }

    [Fact]
    public async Task InferBatch_SimplePrompt_ReturnsValid512x512Image()
    {
        if (!ModelAvailable) return;

        using StableDiffusionTurboModel model = new(
            name: "mo_di_hyper",
            modelDirectory: ModelDirectory,
            seed: 42);

        DatumIngest.Functions.ValueRef[][] inputs =
            [[DatumIngest.Functions.ValueRef.FromString("modern disney style, halfling rogue with a sly grin, leather armor")]];
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
            name: "mo_di_hyper",
            modelDirectory: ModelDirectory,
            seed: 42);

        DatumIngest.Functions.ValueRef[][] inputs =
        [
            [DatumIngest.Functions.ValueRef.FromString("modern disney style, friendly innkeeper polishing a mug")],
            [DatumIngest.Functions.ValueRef.FromString("modern disney style, mischievous goblin holding a stolen ring")],
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
    public void Catalog_RegisterAndResolve_YieldsMoDiHyperModel()
    {
        if (!ModelAvailable) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterMoDiHyper(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("mo_di_hyper");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.MoDiHyperAnchor, entry.RelativePath);
        Assert.Equal("generator", entry.Category);
        Assert.NotNull(entry.Modalities);
        Assert.Contains("text", entry.Modalities!);
        Assert.Contains("image", entry.Modalities!);
        Assert.False(entry.IsDeterministic);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("mo_di_hyper");
        IModel model = lease.Model;
        Assert.IsType<StableDiffusionTurboModel>(model);
        Assert.Equal(DataKind.Image, model.OutputKind);
    }
}

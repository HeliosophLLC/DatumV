namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;

using SkiaSharp;

/// <summary>
/// Smoke tests for the AbsoluteReality + Hyper-SD registration. Same SD 1.5
/// pipeline shape as the other SD-1.5+Hyper exports (StableDiffusionTurboModel
/// loader). Self-skip when the files aren't present.
/// </summary>
public sealed class AbsoluteRealityHyperModelTests : ServiceTestBase
{
    private static string ModelDirectory => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.AbsoluteRealityHyperFolder);

    private static string AnchorFilePath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.AbsoluteRealityHyperAnchor);

    private static bool ModelAvailable => File.Exists(AnchorFilePath);

    [Fact]
    public void Load_AbsoluteRealityHyper_ExposesExpectedSignature()
    {
        if (!ModelAvailable) return;

        using StableDiffusionTurboModel model = new(name: "absolute_reality_hyper", modelDirectory: ModelDirectory);

        Assert.Equal("absolute_reality_hyper", model.Name);
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
            name: "absolute_reality_hyper",
            modelDirectory: ModelDirectory,
            seed: 42);

        DatumIngest.Functions.ValueRef[][] inputs =
            [[DatumIngest.Functions.ValueRef.FromString("a seasoned ranger leaning on a longbow at the forest edge")]];
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
            name: "absolute_reality_hyper",
            modelDirectory: ModelDirectory,
            seed: 42);

        DatumIngest.Functions.ValueRef[][] inputs =
        [
            [DatumIngest.Functions.ValueRef.FromString("a cloaked stranger entering a stone-walled tavern")],
            [DatumIngest.Functions.ValueRef.FromString("a sailor at a misty harbor at dawn, gulls overhead")],
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
    public void Catalog_RegisterAndResolve_YieldsAbsoluteRealityHyperModel()
    {
        if (!ModelAvailable) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterAbsoluteRealityHyper(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("absolute_reality_hyper");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.AbsoluteRealityHyperAnchor, entry.RelativePath);
        Assert.Equal("generator", entry.Category);
        Assert.NotNull(entry.Modalities);
        Assert.Contains("text", entry.Modalities!);
        Assert.Contains("image", entry.Modalities!);
        Assert.False(entry.IsDeterministic);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("absolute_reality_hyper");
        IModel model = lease.Model;
        Assert.IsType<StableDiffusionTurboModel>(model);
        Assert.Equal(DataKind.Image, model.OutputKind);
    }
}

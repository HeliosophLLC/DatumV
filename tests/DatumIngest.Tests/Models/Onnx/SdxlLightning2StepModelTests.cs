namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;

using SkiaSharp;

/// <summary>
/// Smoke tests for the SDXL-Lightning 2-step registration. Loads via the
/// shared <see cref="SdxlTurboModel"/> with <see cref="PredictionType.Sample"/>
/// and <c>steps=2</c>, exercising the sample-prediction Euler branch that
/// SDXL-Turbo / Juggernaut never hit. Self-skips when artefacts are absent.
/// </summary>
public sealed class SdxlLightning2StepModelTests : ServiceTestBase
{
    private static string ModelDirectory => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.SdxlLightning2StepFolder);

    private static string AnchorFilePath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.SdxlLightning2StepAnchor);

    private static bool ModelAvailable => File.Exists(AnchorFilePath);

    [Fact]
    public void Load_SdxlLightning2Step_ExposesExpectedSignature()
    {
        if (!ModelAvailable) return;

        using SdxlTurboModel model = new(
            name: "sdxl_lightning_2step",
            modelDirectory: ModelDirectory,
            steps: 2,
            predictionType: PredictionType.Sample);

        Assert.Equal("sdxl_lightning_2step", model.Name);
        Assert.False(model.IsDeterministic);
        Assert.Equal(DataKind.Image, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.String, model.InputKinds[0]);
        Assert.Equal(1, model.PreferredBatchSize);
    }

    /// <summary>
    /// End-to-end with sample prediction. The Euler branch under
    /// <see cref="PredictionType.Sample"/> uses
    /// <c>x_next = x · (σ_next/σ) + pred_x0 · (1 − σ_next/σ)</c> instead of
    /// the epsilon update, so a successful generation here proves both the
    /// new branch and the SDXL pipeline plumbing are correct.
    /// </summary>
    [Fact]
    public async Task InferBatch_SimplePrompt_ReturnsValid1024x1024Image()
    {
        if (!ModelAvailable) return;

        using SdxlTurboModel model = new(
            name: "sdxl_lightning_2step",
            modelDirectory: ModelDirectory,
            seed: 42,
            steps: 2,
            predictionType: PredictionType.Sample);

        DatumIngest.Functions.ValueRef[][] inputs =
            [[DatumIngest.Functions.ValueRef.FromString("a halfling rogue, dark hair, leather armor, cinematic lighting")]];
        IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
            inputs, overrides: [], cancellationToken: CancellationToken.None);

        DatumIngest.Functions.ValueRef result = Assert.Single(outputs);
        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);

        SKBitmap bitmap = result.AsImage();
        Assert.NotNull(bitmap);
        Assert.Equal(1024, bitmap.Width);
        Assert.Equal(1024, bitmap.Height);
    }

    [Fact]
    public async Task InferBatch_TwoDifferentPrompts_ProducesTwoDistinctImages()
    {
        if (!ModelAvailable) return;

        using SdxlTurboModel model = new(
            name: "sdxl_lightning_2step",
            modelDirectory: ModelDirectory,
            seed: 42,
            steps: 2,
            predictionType: PredictionType.Sample);

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

        Assert.Equal(1024, image1.Width);
        Assert.Equal(1024, image2.Width);
        Assert.NotEqual(image1.Bytes, image2.Bytes);
    }

    [Fact]
    public void Catalog_RegisterAndResolve_YieldsSdxlLightningModel()
    {
        if (!ModelAvailable) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterSdxlLightning2Step(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("sdxl_lightning_2step");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.SdxlLightning2StepAnchor, entry.RelativePath);
        Assert.Equal("generator", entry.Category);
        Assert.NotNull(entry.Modalities);
        Assert.Contains("text", entry.Modalities!);
        Assert.Contains("image", entry.Modalities!);
        Assert.False(entry.IsDeterministic);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("sdxl_lightning_2step");
        IModel model = lease.Model;
        Assert.IsType<SdxlTurboModel>(model);
        Assert.Equal(DataKind.Image, model.OutputKind);
    }
}

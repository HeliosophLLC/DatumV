namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;
using DatumIngest.Pooling;

using SkiaSharp;

/// <summary>
/// Smoke tests for SDXL-Turbo. Self-skip when the model files aren't
/// present so CI machines without the artefacts don't fail.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class SdxlTurboModelTests : ServiceTestBase
{
    private static string ModelDirectory => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.SdxlTurboFolder);

    private static string AnchorFilePath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.SdxlTurboAnchor);

    private static bool ModelAvailable => File.Exists(AnchorFilePath);

    /// <summary>
    /// Cheapest signal: all four ONNX sessions plus the CLIP tokenizer
    /// load without exception. Catches missing-file or shape-mismatch
    /// regressions without exercising generation.
    /// </summary>
    [Fact]
    public void Load_RealSdxlTurbo_ExposesExpectedSignature()
    {
        if (!ModelAvailable) return;

        using SdxlTurboModel model = new(name: "sdxl_turbo", modelDirectory: ModelDirectory);

        Assert.Equal("sdxl_turbo", model.Name);
        Assert.False(model.IsDeterministic);
        Assert.Equal(DataKind.Image, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.String, model.InputKinds[0]);
        Assert.Equal(1, model.PreferredBatchSize);
    }

    /// <summary>
    /// End-to-end on a real prompt: the full pipeline (tokenize → dual
    /// text encoders → noise sample → UNet with added_cond_kwargs → VAE
    /// decode) produces a 1024×1024 SKBitmap.
    /// </summary>
    /// <remarks>
    /// Substantially slower than SD-Turbo: SDXL-Turbo's UNet is ~3× larger
    /// and the output is 16× more pixels. Expect ~10-30s per image
    /// depending on hardware. We don't pin specific pixel values; check
    /// is "did the pipeline produce a valid 1024×1024 image."
    /// </remarks>
    [Fact]
    public async Task InferBatch_SimplePrompt_ReturnsValid1024x1024Image()
    {
        if (!ModelAvailable) return;

        using SdxlTurboModel model = new(
            name: "sdxl_turbo",
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
        Assert.Equal(1024, bitmap.Width);
        Assert.Equal(1024, bitmap.Height);
    }

    /// <summary>
    /// Catalog round-trip: registering SDXL-Turbo via <see cref="BuiltinModels"/>
    /// resolves to a usable <see cref="SdxlTurboModel"/> with the
    /// correct metadata.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolve_YieldsSdxlTurboModel()
    {
        if (!ModelAvailable) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterSdxlTurbo(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("sdxl_turbo");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.SdxlTurboAnchor, entry.RelativePath);
        Assert.Equal("generator", entry.Category);
        Assert.NotNull(entry.Modalities);
        Assert.Contains("text", entry.Modalities!);
        Assert.Contains("image", entry.Modalities!);
        Assert.False(entry.IsDeterministic);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("sdxl_turbo");
        IModel model = lease.Model;
        Assert.IsType<SdxlTurboModel>(model);
        Assert.Equal(DataKind.Image, model.OutputKind);
    }
}

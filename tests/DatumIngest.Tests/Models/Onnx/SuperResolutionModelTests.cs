namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;
using DatumIngest.Pooling;

using SkiaSharp;

/// <summary>
/// Smoke tests for Real-ESRGAN-General x4 via <see cref="SuperResolutionModel"/>.
/// Self-skip when the ONNX file is absent so CI machines without the
/// model artefact don't fail.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class SuperResolutionModelTests : ServiceTestBase
{
    private static string ModelPath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.RealesrganGeneralX4DefaultFilename);

    private static bool ModelAvailable => File.Exists(ModelPath);

    private static byte[] MakeSolidPng(int width, int height, SKColor color)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// Cheapest signal: the ONNX session loads, declares the expected
    /// signature (Image → Image), and the input has the dynamic
    /// [N, 3, H, W] shape Real-ESRGAN-Compact uses.
    /// </summary>
    [Fact]
    public void Load_RealRealesrgan_ExposesExpectedSignature()
    {
        if (!ModelAvailable) return;

        using SuperResolutionModel model = new(name: "realesrgan_general_x4", modelFilePath: ModelPath);

        Assert.Equal("realesrgan_general_x4", model.Name);
        Assert.True(model.IsDeterministic);
        Assert.Equal(DataKind.Image, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
    }

    /// <summary>
    /// End-to-end: feed a 64×64 solid colour PNG through the model and
    /// verify the result decodes to a 256×256 image (4× upscale).
    /// </summary>
    [Fact]
    public async Task InferBatch_SolidImage_Returns4xLargerImage()
    {
        if (!ModelAvailable) return;

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using SuperResolutionModel model = new(name: "realesrgan_general_x4", modelFilePath: ModelPath);

            byte[] png = MakeSolidPng(64, 64, SKColors.SteelBlue);

            DatumIngest.Functions.ValueRef[][] inputs =
                [[DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, png)]];
            DatumIngest.Functions.ValueRef[][] overrides = [[]];
            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs,
                overrides,
                cancellationToken: CancellationToken.None);

            DatumIngest.Functions.ValueRef result = Assert.Single(outputs);
            Assert.False(result.IsNull);
            Assert.Equal(DataKind.Image, result.Kind);

            SKBitmap upscaled = result.AsImage();
            Assert.NotNull(upscaled);
            Assert.Equal(256, upscaled.Width);
            Assert.Equal(256, upscaled.Height);
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Catalog round-trip: registering Real-ESRGAN-General x4 via
    /// <see cref="BuiltinModels"/> resolves to a usable
    /// <see cref="SuperResolutionModel"/>.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolve_YieldsSuperResolutionModel()
    {
        if (!ModelAvailable) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterRealesrganGeneralX4(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("realesrgan_general_x4");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.RealesrganGeneralX4DefaultFilename, entry.RelativePath);
        Assert.Equal(DataKind.Image, entry.OutputKind);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("realesrgan_general_x4");
        IModel model = lease.Model;
        Assert.IsType<SuperResolutionModel>(model);
        Assert.Equal(DataKind.Image, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
    }
}

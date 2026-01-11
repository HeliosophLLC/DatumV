namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;
using DatumIngest.Pooling;

using SkiaSharp;

/// <summary>
/// Smoke tests for the YOLOX detector. Self-skip when the ONNX file is
/// absent so CI machines without the model artefact don't fail.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class YoloXModelTests : ServiceTestBase
{
    private static string ModelPath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.YoloXSFilename);

    private static bool ModelAvailable => File.Exists(ModelPath);

    private static byte[] MakeSolidPng(int width, int height, SKColor color)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// Cheapest signal: the ONNX session loads, signature matches, input
    /// size auto-detection produces 640 (the standard for YOLOX-S).
    /// </summary>
    [Fact]
    public void Load_RealYoloXSmall_ExposesExpectedSignature()
    {
        if (!ModelAvailable) return;

        using YoloXModel model = new(name: "yolox_s", modelFilePath: ModelPath);

        Assert.Equal("yolox_s", model.Name);
        Assert.True(model.IsDeterministic);
        Assert.Equal(DataKind.Struct, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
        Assert.Equal(640, model.InputSize);
    }

    /// <summary>
    /// End-to-end on a synthetic image: feed a uniform-colour PNG and
    /// verify the model returns a well-formed <c>Array&lt;Struct&gt;</c>
    /// without crashing. Detection count is unpredictable on solid colour
    /// inputs (might be 0, might be hallucinated boxes); we only assert
    /// that the dispatch + decode pipeline doesn't throw.
    /// </summary>
    [Fact]
    public async Task InferBatch_SolidImage_ReturnsArrayOfStructs()
    {
        if (!ModelAvailable) return;

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using YoloXModel model = new(name: "yolox_s", modelFilePath: ModelPath);

            byte[] png = MakeSolidPng(640, 480, SKColors.SteelBlue);

            DatumIngest.Functions.ValueRef[][] inputs =
                [[DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, png)]];
            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs, overrides: [], cancellationToken: CancellationToken.None);

            DatumIngest.Functions.ValueRef result = Assert.Single(outputs);
            Assert.False(result.IsNull);
            Assert.Equal(DataKind.Struct, result.Kind);
            Assert.True(result.IsArray);
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Catalog round-trip: registering YOLOX-S via <see cref="BuiltinModels"/>
    /// resolves to a usable <see cref="YoloXModel"/> with the expected
    /// metadata. License is Apache-2.0.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolve_YieldsYoloXModel()
    {
        if (!ModelAvailable) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterYoloXSmall(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("yolox_s");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.YoloXSFilename, entry.RelativePath);
        Assert.Equal("Apache-2.0", entry.License);
        Assert.Equal("Megvii", entry.LicenseHolder);
        Assert.Equal("detector", entry.Category);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("yolox_s");
        IModel model = lease.Model;
        Assert.IsType<YoloXModel>(model);
        Assert.Equal(DataKind.Struct, model.OutputKind);
    }

    /// <summary>
    /// Verifies that the seven-variant registration helper produces all
    /// seven entries with distinct catalog names. Doesn't load any
    /// models — just exercises the registration plumbing.
    /// </summary>
    [Fact]
    public void RegisterAllYoloX_Adds7Entries()
    {
        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterAllYoloX(catalog);

        string[] expectedNames = ["yolox_n", "yolox_t", "yolox_s", "yolox_m", "yolox_l", "yolox_x", "yolox_darknet"];
        foreach (string name in expectedNames)
        {
            ModelCatalogEntry? entry = catalog.TryGetEntry(name);
            Assert.NotNull(entry);
            Assert.Equal("Apache-2.0", entry!.License);
            Assert.Equal("detector", entry.Category);
        }
    }
}

namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;
using DatumIngest.Pooling;

using SkiaSharp;

/// <summary>
/// Smoke tests for the YOLOv8 detector. Self-skip when the ONNX file is
/// absent so CI machines without the model artefact don't fail.
/// </summary>
public sealed class YoloModelTests : ServiceTestBase
{
    private static string ModelPath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.YoloDefaultFilename);

    private static bool ModelAvailable => File.Exists(ModelPath);

    private static byte[] MakeSolidPng(int width, int height, SKColor color)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// Cheapest signal: the ONNX session loads, signature is the expected
    /// shape (Image → Array). Doesn't require the model to actually detect
    /// anything — that depends on the input image.
    /// </summary>
    [Fact]
    public void Load_RealYolov8n_ExposesExpectedSignature()
    {
        if (!ModelAvailable) return;

        using YoloModel model = new(name: "detect", modelFilePath: ModelPath);

        Assert.Equal("detect", model.Name);
        Assert.True(model.IsDeterministic);
        Assert.Equal(DataKind.Array, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
    }

    /// <summary>
    /// End-to-end: feed a solid colour PNG through the model. Detection
    /// count is unpredictable (the model doesn't expect uniform-colour
    /// inputs) but the output should still be a well-formed
    /// <see cref="DataKind.Array"/> DataValue and not throw.
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
            using YoloModel model = new(name: "detect", modelFilePath: ModelPath);

            byte[] png = MakeSolidPng(640, 480, SKColors.SteelBlue);

            DatumIngest.Functions.ValueRef[][] inputs =
                [[DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, png)]];
            DatumIngest.Functions.ValueRef[][] overrides = [[]];
            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs,
                overrides,
                cancellationToken: CancellationToken.None);

            DatumIngest.Functions.ValueRef result = Assert.Single(outputs);
            Assert.False(result.IsNull);
            Assert.Equal(DataKind.Array, result.Kind);
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Catalog round-trip: registering YOLOv8 via <see cref="BuiltinModels"/>
    /// resolves to a usable <see cref="YoloModel"/>.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolve_YieldsYoloModel()
    {
        if (!ModelAvailable) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterYolo(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("detect");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.YoloDefaultFilename, entry.RelativePath);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("detect");
        IModel model = lease.Model;
        Assert.IsType<YoloModel>(model);
        Assert.Equal(DataKind.Array, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
    }
}

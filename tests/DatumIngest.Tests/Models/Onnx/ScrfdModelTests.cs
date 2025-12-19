namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;
using DatumIngest.Pooling;

using SkiaSharp;

/// <summary>
/// Smoke tests for the SCRFD-10G face detector. Self-skip when the ONNX
/// file is absent so CI machines without the model artefact don't fail.
/// </summary>
public sealed class ScrfdModelTests : ServiceTestBase
{
    private static string ModelPath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.Scrfd10gDefaultFilename);

    private static bool ModelAvailable => File.Exists(ModelPath);

    private static byte[] MakeSolidPng(int width, int height, SKColor color)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// Cheapest signal: the ONNX session loads and the 9 outputs resolve to
    /// the expected score / bbox / kps trio per stride at 640×640. Catches
    /// re-exports at non-standard input sizes early.
    /// </summary>
    [Fact]
    public void Load_RealScrfd_ExposesExpectedSignature()
    {
        if (!ModelAvailable) return;

        using ScrfdModel model = new(name: "scrfd_10g", modelFilePath: ModelPath);

        Assert.Equal("scrfd_10g", model.Name);
        Assert.True(model.IsDeterministic);
        Assert.Equal(DataKind.Struct, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
    }

    /// <summary>
    /// End-to-end: feed a solid colour PNG through the model. SCRFD won't
    /// find faces in a uniform image, so the output should be a well-formed
    /// (typically empty) Array&lt;Struct&gt; ValueRef rather than throw.
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
            using ScrfdModel model = new(name: "scrfd_10g", modelFilePath: ModelPath);

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
    /// Catalog round-trip: registering SCRFD-10G via <see cref="BuiltinModels"/>
    /// resolves to a usable <see cref="ScrfdModel"/>.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolve_YieldsScrfdModel()
    {
        if (!ModelAvailable) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterScrfd10g(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("scrfd_10g");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.Scrfd10gDefaultFilename, entry.RelativePath);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("scrfd_10g");
        IModel model = lease.Model;
        Assert.IsType<ScrfdModel>(model);
        Assert.Equal(DataKind.Struct, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
    }
}

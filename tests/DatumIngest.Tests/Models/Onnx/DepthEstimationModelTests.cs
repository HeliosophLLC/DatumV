namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;
using DatumIngest.Pooling;

using SkiaSharp;

/// <summary>
/// Smoke tests for MiDaS-small and DPT-Large depth estimators via
/// <see cref="DepthEstimationModel"/>. Self-skip when the ONNX file is absent
/// so CI machines without the model artefact don't fail.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class DepthEstimationModelTests : ServiceTestBase
{
    private const int MidasInputSize = 256;
    private const int DptInputSize = 384;

    private static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];
    private static readonly float[] HalfHalfMean = [0.5f, 0.5f, 0.5f];
    private static readonly float[] HalfHalfStd = [0.5f, 0.5f, 0.5f];

    public static IEnumerable<object[]> Variants() =>
    [
        ["midas_small", BuiltinModels.MidasSmall256Filename, MidasInputSize, true,  ImageNetMean, ImageNetStd],
        ["dpt_large",   BuiltinModels.DptLarge384Filename,   DptInputSize,   false, HalfHalfMean, HalfHalfStd],
    ];

    private static string ResolvePath(string filename) =>
        Path.Combine(ModelCatalog.DefaultModelDirectory, filename);

    private static byte[] MakeSolidPng(int width, int height, SKColor color)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// Cheapest signal: the ONNX session loads and declares the expected
    /// signature (Image → Image, deterministic).
    /// </summary>
    [Theory]
    [MemberData(nameof(Variants))]
    public void Load_ExposesExpectedSignature(
        string modelName, string filename, int inputSize, bool bgr, float[] mean, float[] std)
    {
        string path = ResolvePath(filename);
        if (!File.Exists(path)) return;

        using DepthEstimationModel model = new(
            name: modelName, modelFilePath: path,
            inputSize: inputSize, bgr: bgr,
            channelMean: mean, channelStd: std);

        Assert.Equal(modelName, model.Name);
        Assert.True(model.IsDeterministic);
        Assert.Equal(DataKind.Image, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
    }

    /// <summary>
    /// End-to-end: feed a non-square solid-colour PNG through the model and
    /// verify the result is a same-sized image (depth map resized back to input
    /// dims, not the network's internal working size).
    /// </summary>
    [Theory]
    [MemberData(nameof(Variants))]
    public async Task InferBatch_PreservesInputDimensions(
        string modelName, string filename, int inputSize, bool bgr, float[] mean, float[] std)
    {
        string path = ResolvePath(filename);
        if (!File.Exists(path)) return;

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using DepthEstimationModel model = new(
                name: modelName, modelFilePath: path,
                inputSize: inputSize, bgr: bgr,
                channelMean: mean, channelStd: std);

            byte[] png = MakeSolidPng(200, 150, SKColors.SteelBlue);

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

            SKBitmap depth = result.AsImage();
            Assert.NotNull(depth);
            Assert.Equal(200, depth.Width);
            Assert.Equal(150, depth.Height);
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Catalog round-trip: registering MiDaS / DPT via <see cref="BuiltinModels"/>
    /// resolves to a usable <see cref="DepthEstimationModel"/>.
    /// </summary>
    [Theory]
    [InlineData("midas_small", BuiltinModels.MidasSmall256Filename)]
    [InlineData("dpt_large",   BuiltinModels.DptLarge384Filename)]
    public void Catalog_RegisterAndResolve_YieldsDepthEstimationModel(
        string modelName, string filename)
    {
        string path = ResolvePath(filename);
        if (!File.Exists(path)) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        if (modelName == "midas_small")
        {
            BuiltinModels.RegisterMidasSmall(catalog);
        }
        else
        {
            BuiltinModels.RegisterDptLarge(catalog);
        }

        ModelCatalogEntry? entry = catalog.TryGetEntry(modelName);
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(filename, entry.RelativePath);
        Assert.Equal(DataKind.Image, entry.OutputKind);

        using ModelLease lease = catalog.ResolveLeaseSynchronously(modelName);
        IModel model = lease.Model;
        Assert.IsType<DepthEstimationModel>(model);
        Assert.Equal(DataKind.Image, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
    }
}

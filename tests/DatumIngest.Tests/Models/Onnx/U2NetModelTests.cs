namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;
using DatumIngest.Pooling;

using SkiaSharp;

/// <summary>
/// Smoke tests for U²-Net (full and lite) via <see cref="U2NetModel"/>.
/// Self-skip when the ONNX file is absent so CI machines without the
/// model artefact don't fail.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class U2NetModelTests : ServiceTestBase
{
    public static IEnumerable<object[]> Variants() =>
    [
        ["u2net", BuiltinModels.U2NetDefaultFilename],
        ["u2netp", BuiltinModels.U2NetpDefaultFilename],
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
    public void Load_ExposesExpectedSignature(string modelName, string filename)
    {
        string path = ResolvePath(filename);
        if (!File.Exists(path)) return;

        using U2NetModel model = new(name: modelName, modelFilePath: path);

        Assert.Equal(modelName, model.Name);
        Assert.True(model.IsDeterministic);
        Assert.Equal(DataKind.Image, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
    }

    /// <summary>
    /// End-to-end: feed a non-square solid-colour PNG through the model and
    /// verify the result is a same-sized image (mask resized back to input dims,
    /// not the network's internal 320×320 working size).
    /// </summary>
    [Theory]
    [MemberData(nameof(Variants))]
    public async Task InferBatch_PreservesInputDimensions(string modelName, string filename)
    {
        string path = ResolvePath(filename);
        if (!File.Exists(path)) return;

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using U2NetModel model = new(name: modelName, modelFilePath: path);

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

            SKBitmap mask = result.AsImage();
            Assert.NotNull(mask);
            Assert.Equal(200, mask.Width);
            Assert.Equal(150, mask.Height);
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Catalog round-trip: registering U²-Net via <see cref="BuiltinModels"/>
    /// resolves to a usable <see cref="U2NetModel"/>.
    /// </summary>
    [Theory]
    [InlineData("u2net", BuiltinModels.U2NetDefaultFilename)]
    [InlineData("u2netp", BuiltinModels.U2NetpDefaultFilename)]
    public void Catalog_RegisterAndResolve_YieldsU2NetModel(string modelName, string filename)
    {
        string path = ResolvePath(filename);
        if (!File.Exists(path)) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        if (modelName == "u2net")
        {
            BuiltinModels.RegisterU2Net(catalog);
        }
        else
        {
            BuiltinModels.RegisterU2Netp(catalog);
        }

        ModelCatalogEntry? entry = catalog.TryGetEntry(modelName);
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(filename, entry.RelativePath);
        Assert.Equal(DataKind.Image, entry.OutputKind);

        using ModelLease lease = catalog.ResolveLeaseSynchronously(modelName);
        IModel model = lease.Model;
        Assert.IsType<U2NetModel>(model);
        Assert.Equal(DataKind.Image, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
    }
}

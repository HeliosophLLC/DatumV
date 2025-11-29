namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;
using DatumIngest.Pooling;

using SkiaSharp;

/// <summary>
/// Phase B end-to-end tests for the ONNX-backed MobileNetV2 classifier.
/// </summary>
/// <remarks>
/// <para>
/// These tests touch a real ONNX model file at the user's local
/// <see cref="ModelCatalog.DefaultModelDirectory"/>. They self-skip when the file
/// is absent so CI machines without the model artefact don't fail. To enable
/// them locally, drop <c>mobilenetv2-12.onnx</c> from the ONNX model zoo into
/// <c>E:\models</c> (or wherever the user's catalog points).
/// </para>
/// <para>
/// The end-to-end SQL plumbing (parser → planner hoist → operator gather/scatter)
/// is covered by <c>ModelInvocationTests</c> against the synthetic
/// <see cref="EchoModel"/>. These tests focus specifically on the ONNX wiring:
/// session loading, batched dispatch, image preprocessing, and argmax / label
/// lookup — exercising the model directly with arena-resident inputs.
/// </para>
/// </remarks>
public sealed class MobileNetV2ModelTests : ServiceTestBase
{
    private static string ModelPath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.MobileNetV2DefaultFilename);

    private static bool ModelAvailable => File.Exists(ModelPath);

    private static byte[] MakeSolidPng(int width, int height, SKColor color)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// Loading the ONNX session should succeed when the file exists, expose the
    /// declared input/output kinds, and dispose cleanly. Cheapest signal that
    /// ONNX Runtime is wired in correctly.
    /// </summary>
    [Fact]
    public void Load_RealMobileNetV2_ExposesExpectedSignature()
    {
        if (!ModelAvailable)
        {
            return;
        }

        using MobileNetV2Model model = new(name: "mobilenetv2", modelFilePath: ModelPath, labels: null);

        Assert.Equal("mobilenetv2", model.Name);
        Assert.True(model.IsDeterministic);
        Assert.Equal(DataKind.String, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
    }

    /// <summary>
    /// One-row inference: encode a synthetic PNG, place its bytes into an arena,
    /// build a single Image DataValue, and dispatch through
    /// <see cref="IModel.InferBatchAsync"/>. The result should be a non-null
    /// <c>class_&lt;0..999&gt;</c> string materialised into the target arena.
    /// </summary>
    [Fact]
    public async Task InferBatch_SingleSolidImage_ReturnsImageNetClassLabel()
    {
        if (!ModelAvailable)
        {
            return;
        }

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using MobileNetV2Model model = new(name: "mobilenetv2", modelFilePath: ModelPath, labels: null);

            byte[] png = MakeSolidPng(64, 64, SKColors.Red);

            DatumIngest.Functions.ValueRef[][] inputs =
                [[DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, png)]];
            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs,
                overrides: [],
                cancellationToken: CancellationToken.None);

            Assert.Single(outputs);
            DatumIngest.Functions.ValueRef label = outputs[0];
            Assert.False(label.IsNull);
            string text = label.AsString();
            Assert.StartsWith("class_", text);
            int idx = int.Parse(text["class_".Length..]);
            Assert.InRange(idx, 0, 999);
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Three-row batched inference: confirms the model packs multiple images into
    /// a single tensor and emits one label per input row. Distinct input colours
    /// give the model some signal that the rows differ; we don't assert specific
    /// classes (no labels file in CI), only that all three results are well-formed.
    /// </summary>
    [Fact]
    public async Task InferBatch_MultipleImages_ReturnsOneLabelPerRow()
    {
        if (!ModelAvailable)
        {
            return;
        }

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using MobileNetV2Model model = new(name: "mobilenetv2", modelFilePath: ModelPath, labels: null);

            DatumIngest.Functions.ValueRef[][] inputs =
            [
                [DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, MakeSolidPng(64, 64, SKColors.Red))],
                [DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, MakeSolidPng(64, 64, SKColors.Green))],
                [DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, MakeSolidPng(64, 64, SKColors.Blue))],
            ];

            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs,
                overrides: [],
                cancellationToken: CancellationToken.None);

            Assert.Equal(3, outputs.Count);
            for (int i = 0; i < outputs.Count; i++)
            {
                DatumIngest.Functions.ValueRef label = outputs[i];
                Assert.False(label.IsNull, $"row {i} returned a null label");
                string text = label.AsString();
                Assert.StartsWith("class_", text);
                int idx = int.Parse(text["class_".Length..]);
                Assert.InRange(idx, 0, 999);
            }
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// When the catalog resolves the model and an <c>imagenet-classes.json</c>
    /// labels file sits next to the ONNX file, predictions should land as
    /// real ImageNet category strings (e.g. <c>"goldfish"</c>) rather than the
    /// <c>class_&lt;index&gt;</c> fallback. We don't pin the exact label —
    /// MobileNetV2's prediction for a solid colour PNG isn't a meaningful
    /// signal — only that the result is one of the 1000 known labels and not
    /// the placeholder shape.
    /// </summary>
    [Fact]
    public async Task InferBatch_WithLabelsFile_ReturnsImageNetLabelString()
    {
        if (!ModelAvailable)
        {
            return;
        }

        string labelsPath = Path.Combine(
            ModelCatalog.DefaultModelDirectory, BuiltinModels.ImageNetLabelsDefaultFilename);
        if (!File.Exists(labelsPath))
        {
            return;
        }

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterMobileNetV2(catalog);
        using ModelLease lease = catalog.ResolveLeaseSynchronously("mobilenetv2");
        IModel model = lease.Model;

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            DatumIngest.Functions.ValueRef[][] inputs =
            [
                [DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, MakeSolidPng(64, 64, SKColors.Red))],
            ];

            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs,
                overrides: [],
                cancellationToken: CancellationToken.None);

            DatumIngest.Functions.ValueRef label = Assert.Single(outputs);
            Assert.False(label.IsNull);
            string text = label.AsString();
            Assert.False(string.IsNullOrEmpty(text));
            Assert.DoesNotContain("class_", text);
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Catalog round-trip: registering MobileNetV2 via <see cref="BuiltinModels"/>
    /// and resolving it through <see cref="ModelCatalog.GetModel"/> should yield a
    /// usable <see cref="MobileNetV2Model"/> with the expected signature.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolve_YieldsMobileNetV2()
    {
        if (!ModelAvailable)
        {
            return;
        }

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterMobileNetV2(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("mobilenetv2");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.MobileNetV2DefaultFilename, entry.RelativePath);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("mobilenetv2");
        IModel model = lease.Model;
        Assert.IsType<MobileNetV2Model>(model);
        Assert.Equal(DataKind.String, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
    }
}

namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;

using SkiaSharp;

/// <summary>
/// Smoke tests for Phi-3.5-vision via ORT GenAI. Self-skip when the
/// GenAI bundle isn't present so CI machines without the artefacts
/// don't fail.
/// </summary>
public sealed class Phi35VisionModelTests : ServiceTestBase
{
    private static string BundleDirectory => Path.Combine(
        ModelCatalog.DefaultModelDirectory,
        BuiltinModels.Phi35VisionGpuSubfolder);

    private static bool ModelAvailable =>
        File.Exists(Path.Combine(BundleDirectory, "genai_config.json"));

    private static byte[] MakeSolidPng(int width, int height, SKColor color)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// ORT GenAI loads the bundle directory as a single unit. This
    /// catches missing-config / unsupported-quantization errors without
    /// running generation.
    /// </summary>
    [Fact]
    public void Load_RealPhi35Vision_ExposesExpectedSignature()
    {
        if (!ModelAvailable) return;

        using Phi35VisionModel model = new(
            name: "phi35_vision",
            modelDirectory: BundleDirectory);

        Assert.Equal("phi35_vision", model.Name);
        Assert.True(model.IsDeterministic);
        Assert.Equal(DataKind.String, model.OutputKind);
        Assert.Equal(2, model.InputKinds.Count);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
        Assert.Equal(DataKind.String, model.InputKinds[1]);
    }

    /// <summary>
    /// End-to-end on a synthetic image with a real prompt: ORT GenAI's
    /// generative loop produces a non-empty answer with no special
    /// tokens leaking through. With IO binding the cost should be a
    /// small fraction of the equivalent hand-rolled
    /// <see cref="Moondream2Model"/> path.
    /// </summary>
    [Fact]
    public async Task InferBatch_SolidImage_ReturnsNonEmptyAnswer()
    {
        if (!ModelAvailable) return;

        using Phi35VisionModel model = new(
            name: "phi35_vision",
            modelDirectory: BundleDirectory,
            // 32 tokens is enough to confirm generation runs end-to-end.
            // GenAI-bundled max_length will cap higher; the smaller cap
            // here keeps the test under the typical CI budget.
            maxTokens: 32);

        byte[] png = MakeSolidPng(378, 378, SKColors.LightSlateGray);
        ValueRef[][] inputs =
        [
            [
                ValueRef.FromBytes(DataKind.Image, png),
                ValueRef.FromString("Describe this image in one short sentence."),
            ],
        ];

        IReadOnlyList<ValueRef> outputs = await model.InferBatchAsync(
            inputs, overrides: [], cancellationToken: CancellationToken.None);

        ValueRef answer = Assert.Single(outputs);
        Assert.False(answer.IsNull);
        string text = answer.AsString();
        Assert.False(string.IsNullOrWhiteSpace(text), "Answer was empty / whitespace.");
        // Phi-3 chat-template tokens shouldn't leak through.
        Assert.DoesNotContain("<|user|>", text);
        Assert.DoesNotContain("<|assistant|>", text);
        Assert.DoesNotContain("<|end|>", text);
        Assert.DoesNotContain("<|image_1|>", text);
    }

    /// <summary>
    /// Catalog round-trip: <see cref="BuiltinModels.RegisterPhi35Vision"/>
    /// resolves to a usable <see cref="Phi35VisionModel"/> with the
    /// expected (Image, String) → String shape.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolve_YieldsPhi35VisionModel()
    {
        if (!ModelAvailable) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterPhi35Vision(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("phi35_vision");
        Assert.NotNull(entry);
        Assert.Equal("vlm", entry!.Category);
        Assert.Equal("onnx_genai", entry.Backend);
        Assert.Equal(2, entry.InputKinds.Count);
        Assert.Equal(DataKind.Image, entry.InputKinds[0]);
        Assert.Equal(DataKind.String, entry.InputKinds[1]);
        Assert.Equal(DataKind.String, entry.OutputKind);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("phi35_vision");
        Assert.IsType<Phi35VisionModel>(lease.Model);
    }
}

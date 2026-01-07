namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;

using SkiaSharp;

/// <summary>
/// Smoke tests for Moondream2. Self-skip when the fp16 ONNX bundle
/// isn't present so CI machines without the artefacts don't fail.
/// </summary>
public sealed class Moondream2ModelTests : ServiceTestBase
{
    private static string VisionEncoderPath => Path.Combine(
        ModelCatalog.DefaultModelDirectory,
        BuiltinModels.Moondream2VisionAnchor);

    private static bool ModelAvailable => File.Exists(VisionEncoderPath);

    private static byte[] MakeSolidPng(int width, int height, SKColor color)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// All three sessions plus the Phi-2 tokenizer load without exception.
    /// Catches missing-file / wrong-shape errors without exercising
    /// generation.
    /// </summary>
    [Fact]
    public void Load_RealMoondream2_ExposesExpectedSignature()
    {
        if (!ModelAvailable) return;

        using Moondream2Model model = new(
            name: "moondream2",
            visionEncoderModelFilePath: VisionEncoderPath);

        Assert.Equal("moondream2", model.Name);
        Assert.True(model.IsDeterministic);
        Assert.Equal(DataKind.String, model.OutputKind);
        Assert.Equal(2, model.InputKinds.Count);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
        Assert.Equal(DataKind.String, model.InputKinds[1]);
    }

    /// <summary>
    /// End-to-end on a synthetic image with a real prompt: confirms the
    /// three-session pipeline plus the KV-cached decoder loop produces a
    /// non-empty answer with no BPE-mojibake leaking through.
    /// </summary>
    [Fact]
    public async Task InferBatch_SolidImage_ReturnsNonEmptyAnswer()
    {
        if (!ModelAvailable) return;

        using Moondream2Model model = new(
            name: "moondream2",
            visionEncoderModelFilePath: VisionEncoderPath,
            // Cap output low for the test — 32 tokens is enough to confirm
            // generation runs end-to-end without running for minutes.
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
        // ByteLevelBpeDecoder must have reversed the GPT-2 byte-to-unicode mapping.
        Assert.DoesNotContain('Ġ', text);
        Assert.DoesNotContain('Ċ', text);
        // Special tokens shouldn't leak through.
        Assert.DoesNotContain("<|endoftext|>", text);
    }

    /// <summary>
    /// Catalog round-trip: <see cref="BuiltinModels.RegisterMoondream2"/>
    /// resolves to a usable <see cref="Moondream2Model"/> with the
    /// expected (Image, String) → String shape.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolve_YieldsMoondream2Model()
    {
        if (!ModelAvailable) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterMoondream2(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("moondream2");
        Assert.NotNull(entry);
        Assert.Equal("vlm", entry!.Category);
        Assert.Equal("onnx", entry.Backend);
        Assert.Equal(2, entry.InputKinds.Count);
        Assert.Equal(DataKind.Image, entry.InputKinds[0]);
        Assert.Equal(DataKind.String, entry.InputKinds[1]);
        Assert.Equal(DataKind.String, entry.OutputKind);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("moondream2");
        Assert.IsType<Moondream2Model>(lease.Model);
    }
}

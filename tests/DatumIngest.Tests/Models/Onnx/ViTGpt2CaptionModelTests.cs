namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;
using DatumIngest.Pooling;

using SkiaSharp;

/// <summary>
/// Smoke tests for the ViT-GPT2 image captioner. Self-skip when the model
/// folder is absent so CI machines without the converted ONNX artefacts
/// don't fail.
/// </summary>
public sealed class ViTGpt2CaptionModelTests : ServiceTestBase
{
    private static string EncoderPath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.ViTGpt2CaptionEncoderRelativePath);

    private static bool ModelAvailable => File.Exists(EncoderPath);

    private static byte[] MakeSolidPng(int width, int height, SKColor color)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// Cheapest signal: encoder + decoder + tokenizer all load. Catches
    /// missing-file or malformed-file regressions without exercising
    /// generation.
    /// </summary>
    [Fact]
    public void Load_RealViTGpt2_ExposesExpectedSignature()
    {
        if (!ModelAvailable) return;

        using ViTGpt2CaptionModel model = new(name: "vit_gpt2_caption", encoderModelFilePath: EncoderPath);

        Assert.Equal("vit_gpt2_caption", model.Name);
        Assert.True(model.IsDeterministic);
        Assert.Equal(DataKind.String, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
        Assert.True(model.MaxTokens > 0);
    }

    /// <summary>
    /// End-to-end on a synthetic image: confirms encoder→decoder→tokenizer
    /// pipeline produces a non-empty caption. We don't pin the exact text —
    /// captions for a uniform-colour PNG are nonsensical, but they should be
    /// well-formed English-ish strings.
    /// </summary>
    [Fact]
    public async Task InferBatch_SolidImage_ReturnsNonEmptyCaption()
    {
        if (!ModelAvailable) return;

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using ViTGpt2CaptionModel model = new(name: "vit_gpt2_caption", encoderModelFilePath: EncoderPath);

            byte[] png = MakeSolidPng(256, 256, SKColors.LightSteelBlue);

            DatumIngest.Functions.ValueRef[][] inputs =
                [[DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, png)]];
            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs,
                overrides: [],
                cancellationToken: CancellationToken.None);

            DatumIngest.Functions.ValueRef caption = Assert.Single(outputs);
            Assert.False(caption.IsNull);
            string text = caption.AsString();
            Assert.False(string.IsNullOrWhiteSpace(text), "Caption was empty / whitespace.");
            // Sanity check: caption should be tokenizer-decoded text, not raw
            // BPE artefacts. The Ġ (Ġ) marker should never leak through.
            Assert.DoesNotContain('Ġ', text);
            Assert.DoesNotContain('Ċ', text);  // Ċ (newline marker)
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Multi-image batch: confirms the encoder packs N images correctly and
    /// each row gets its own per-image caption. Distinct colours give the
    /// model some signal that the inputs differ; we don't pin captions per
    /// row, only that all three are well-formed and present.
    /// </summary>
    [Fact]
    public async Task InferBatch_MultipleImages_ReturnsOneCaptionPerRow()
    {
        if (!ModelAvailable) return;

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using ViTGpt2CaptionModel model = new(name: "vit_gpt2_caption", encoderModelFilePath: EncoderPath);

            DatumIngest.Functions.ValueRef[][] inputs =
            [
                [DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, MakeSolidPng(256, 256, SKColors.IndianRed))],
                [DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, MakeSolidPng(256, 256, SKColors.SeaGreen))],
                [DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, MakeSolidPng(256, 256, SKColors.Goldenrod))],
            ];

            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs,
                overrides: [],
                cancellationToken: CancellationToken.None);

            Assert.Equal(3, outputs.Count);
            for (int i = 0; i < outputs.Count; i++)
            {
                DatumIngest.Functions.ValueRef caption = outputs[i];
                Assert.False(caption.IsNull, $"row {i} returned a null caption");
                string text = caption.AsString();
                Assert.False(string.IsNullOrWhiteSpace(text), $"row {i} caption was empty");
            }
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Catalog round-trip: registering ViT-GPT2 via <see cref="BuiltinModels"/>
    /// resolves to a usable <see cref="ViTGpt2CaptionModel"/> with the
    /// expected metadata.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolve_YieldsViTGpt2CaptionModel()
    {
        if (!ModelAvailable) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterViTGpt2Caption(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("vit_gpt2_caption");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.ViTGpt2CaptionEncoderRelativePath, entry.RelativePath);
        Assert.Equal("captioner", entry.Category);
        Assert.NotNull(entry.Modalities);
        Assert.Contains("image", entry.Modalities!);
        Assert.Contains("text", entry.Modalities!);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("vit_gpt2_caption");
        IModel model = lease.Model;
        Assert.IsType<ViTGpt2CaptionModel>(model);
        Assert.Equal(DataKind.String, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
    }
}

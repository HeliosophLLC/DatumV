namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;
using DatumIngest.Pooling;

using SkiaSharp;

/// <summary>
/// Smoke tests for the Florence-2 captioner. Self-skip when the fp16
/// model folder isn't present so CI machines without the artefacts don't
/// fail.
/// </summary>
public sealed class Florence2ModelTests : ServiceTestBase
{
    private static string Fp16EncoderPath => Path.Combine(
        ModelCatalog.DefaultModelDirectory,
        BuiltinModels.Florence2Fp16Folder,
        "vision_encoder_fp16.onnx");

    private static bool ModelAvailable => File.Exists(Fp16EncoderPath);

    private static byte[] MakeSolidPng(int width, int height, SKColor color)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// Cheapest signal: all four ONNX sessions plus the tokenizer load
    /// without exception. Catches missing-file or shape-mismatch errors
    /// without exercising generation.
    /// </summary>
    [Fact]
    public void Load_RealFlorence2_ExposesExpectedSignature()
    {
        if (!ModelAvailable) return;

        using Florence2Model model = new(
            name: "florence2_caption",
            visionEncoderPath: Fp16EncoderPath,
            taskPrompt: "<CAPTION>");

        Assert.Equal("florence2_caption", model.Name);
        Assert.True(model.IsDeterministic);
        Assert.Equal(DataKind.String, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
        Assert.Equal("<CAPTION>", model.TaskPromptDescription);
    }

    /// <summary>
    /// End-to-end on a synthetic image: confirms the four-session pipeline
    /// produces a non-empty caption with no BPE artefacts leaking through.
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
            using Florence2Model model = new(
                name: "florence2_caption",
                visionEncoderPath: Fp16EncoderPath,
                taskPrompt: "<CAPTION>");

            byte[] png = MakeSolidPng(512, 512, SKColors.LightSlateGray);

            DatumIngest.Functions.ValueRef[][] inputs =
                [[DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, png)]];
            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs, overrides: [], cancellationToken: CancellationToken.None);

            DatumIngest.Functions.ValueRef caption = Assert.Single(outputs);
            Assert.False(caption.IsNull);
            string text = caption.AsString();
            Assert.False(string.IsNullOrWhiteSpace(text), "Caption was empty / whitespace.");
            // BPE markers must not leak through after byte-level decode.
            Assert.DoesNotContain('Ġ', text);
            Assert.DoesNotContain('Ċ', text);
            // Task token shouldn't appear in cleaned output.
            Assert.DoesNotContain("<CAPTION>", text);
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Verifies that two different task prompts both run end-to-end and
    /// produce non-empty captions. We don't assert "detailed is longer
    /// than short" here because Florence-2 produces an "unanswerable"
    /// abstain response for synthetic / non-photographic inputs (a solid
    /// colour PNG isn't really an image it can describe), and that
    /// abstain text is the same regardless of the requested detail level.
    /// On real photos the lengths do differ; on synthetic test fixtures
    /// they often don't. The looser assertion is that *both prompts run
    /// without crashing and produce some text* — that's the smoke test.
    /// </summary>
    [Fact]
    public async Task InferBatch_DifferentPrompts_BothRunSuccessfully()
    {
        if (!ModelAvailable) return;

        byte[] png = MakeSolidPng(512, 512, SKColors.MediumSeaGreen);
        DatumIngest.Functions.ValueRef[][] inputs =
            [[DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, png)]];

        using Florence2Model shortModel = new(
            name: "florence2_caption",
            visionEncoderPath: Fp16EncoderPath,
            taskPrompt: "<CAPTION>",
            maxTokens: 50);
        IReadOnlyList<DatumIngest.Functions.ValueRef> shortOutputs = await shortModel.InferBatchAsync(
            inputs, overrides: [], cancellationToken: CancellationToken.None);
        string shortCaption = shortOutputs[0].AsString();

        using Florence2Model longModel = new(
            name: "florence2_more_detailed_caption",
            visionEncoderPath: Fp16EncoderPath,
            taskPrompt: "<MORE_DETAILED_CAPTION>",
            maxTokens: 300);
        IReadOnlyList<DatumIngest.Functions.ValueRef> longOutputs = await longModel.InferBatchAsync(
            inputs, overrides: [], cancellationToken: CancellationToken.None);
        string longCaption = longOutputs[0].AsString();

        Assert.False(string.IsNullOrWhiteSpace(shortCaption));
        Assert.False(string.IsNullOrWhiteSpace(longCaption));
        // No special-token markers leaking through.
        Assert.DoesNotContain("<s>", shortCaption);
        Assert.DoesNotContain("</s>", shortCaption);
        Assert.DoesNotContain("<s>", longCaption);
        Assert.DoesNotContain("</s>", longCaption);
    }

    /// <summary>
    /// Catalog round-trip: registering Florence-2 via <see cref="BuiltinModels"/>
    /// resolves to a usable <see cref="Florence2Model"/> with the expected
    /// metadata.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolve_YieldsFlorence2Model()
    {
        if (!ModelAvailable) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterFlorence2Caption(catalog);
        BuiltinModels.RegisterFlorence2DetailedCaption(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("florence2_caption");
        Assert.NotNull(entry);
        Assert.Equal("captioner", entry!.Category);
        Assert.NotNull(entry.Files);
        // 11 files per Florence-2 install: 4 ONNX + 7 tokenizer/configs.
        Assert.Equal(11, entry.Files!.Count);

        ModelCatalogEntry? detailed = catalog.TryGetEntry("florence2_detailed_caption");
        Assert.NotNull(detailed);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("florence2_caption");
        IModel model = lease.Model;
        Assert.IsType<Florence2Model>(model);
    }
}

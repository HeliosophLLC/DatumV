using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// Stability AI SDXL-Turbo — text-to-image diffusion model. Generates
/// 1024×1024 images with notably better composition and prompt adherence
/// than SD-Turbo, at modestly higher latency. Implements the SDXL
/// pipeline directly across four ONNX sessions (two text encoders, UNet,
/// VAE decoder).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Differences from <see cref="StableDiffusionTurboModel"/>.</strong>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <strong>Dual text encoders.</strong> SDXL uses CLIP-L (768 hidden)
///     for cross-attention shape and OpenCLIP-G (1280 hidden) for both
///     cross-attention shape and pooled embedding. The same CLIP BPE
///     tokenizer feeds both. UNet's <c>encoder_hidden_states</c> input
///     is the concatenation of the two encoder outputs along the last
///     dim (<c>768 + 1280 = 2048</c>).
///   </description></item>
///   <item><description>
///     <strong><c>added_cond_kwargs</c>.</strong> SDXL's UNet takes two
///     extra conditioning inputs that SD's didn't:
///     <c>text_embeds</c> (pooled output from text encoder 2, shape
///     <c>[1, 1280]</c>) and <c>time_ids</c> (image dimension/crop info,
///     shape <c>[1, 6]</c>). Without them the model produces garbled
///     output.
///   </description></item>
///   <item><description>
///     <strong>1024×1024 output</strong> with 128×128 latent (vs SD's
///     512×512 / 64×64). 16× more pixels, 16× more latent compute.
///   </description></item>
///   <item><description>
///     <strong>VAE scaling factor 0.13025</strong> (vs SD 2.x's 0.18215).
///     Tiny detail; getting it wrong washes out the colours.
///   </description></item>
/// </list>
/// <para>
/// Same single-step Euler scheduler at sigma_max as SD-Turbo. SDXL-Turbo
/// supports up to 4 inference steps for higher quality; we expose the
/// 1-step path for the lowest latency. Adding multi-step support is a
/// follow-up if needed.
/// </para>
/// </remarks>
public sealed class SdxlTurboModel : IModel, IDisposable
{
    // SDXL-Turbo dimensions.
    private const int LatentChannels = 4;
    private const int ImageHeight = 1024;
    private const int ImageWidth = 1024;
    private const int LatentHeight = ImageHeight / 8;  // 128
    private const int LatentWidth = ImageWidth / 8;    // 128

    // Text encoder hidden dimensions (after concat fed to UNet).
    private const int Encoder1HiddenDim = 768;   // CLIP-L
    private const int Encoder2HiddenDim = 1280;  // OpenCLIP-G

    // CLIP tokenizer constants (shared with SD-Turbo).
    private const int CLIPMaxTokens = 77;
    private const int BosTokenId = 49406;
    private const int EosTokenId = 49407;

    // SDXL-specific scheduler constants.
    // Same beta schedule as SD-Turbo, so sigma_max at t=999 is identical.
    private const float SigmaMax = 14.6146f;
    private const float VaeScaleFactor = 0.13025f;  // SDXL's VAE — different from SD's 0.18215
    private const float TimestepValue = 999f;

    private readonly InferenceSession _textEncoder1Session;
    private readonly InferenceSession _textEncoder2Session;
    private readonly InferenceSession _unetSession;
    private readonly InferenceSession _vaeDecoderSession;
    private readonly BpeTokenizer _tokenizer;
    private readonly Random _rng;

    // The pooled-output tensor from text encoder 2 lives at a specific
    // output name. Diffusers ONNX export tags it consistently.
    private readonly string _encoder2PooledOutputName;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsDeterministic => false;

    /// <inheritdoc />
    public IReadOnlyList<DataKind> InputKinds { get; } = [DataKind.String];

    /// <inheritdoc />
    public DataKind OutputKind => DataKind.Image;

    /// <inheritdoc />
    /// <remarks>
    /// SDXL-Turbo at 1 step generates one image in ~3-5s on a consumer
    /// GPU. <c>PreferredBatchSize = 1</c> streams each image as soon as
    /// it's done.
    /// </remarks>
    public int? PreferredBatchSize => 1;

    /// <summary>
    /// Loads SDXL-Turbo from a directory in HuggingFace diffusers layout.
    /// Expects subfolders <c>text_encoder/</c>, <c>text_encoder_2/</c>,
    /// <c>unet/</c>, <c>vae_decoder/</c>, <c>tokenizer/</c>.
    /// </summary>
    /// <param name="name">Catalog-visible name.</param>
    /// <param name="modelDirectory">
    /// Absolute path to the SDXL-Turbo directory. The diffusers-standard
    /// folder layout produced by <c>optimum-cli export onnx
    /// --model stabilityai/sdxl-turbo</c>.
    /// </param>
    /// <param name="seed">
    /// Optional RNG seed for reproducible generation.
    /// </param>
    public SdxlTurboModel(string name, string modelDirectory, int? seed = null)
    {
        Name = name;

        string textEncoder1Path = Path.Combine(modelDirectory, "text_encoder", "model.onnx");
        string textEncoder2Path = Path.Combine(modelDirectory, "text_encoder_2", "model.onnx");
        string unetPath = Path.Combine(modelDirectory, "unet", "model.onnx");
        string vaeDecoderPath = Path.Combine(modelDirectory, "vae_decoder", "model.onnx");
        string vocabPath = Path.Combine(modelDirectory, "tokenizer", "vocab.json");
        string mergesPath = Path.Combine(modelDirectory, "tokenizer", "merges.txt");

        foreach (string path in new[] {
            textEncoder1Path, textEncoder2Path, unetPath, vaeDecoderPath, vocabPath, mergesPath
        })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"SDXL-Turbo component '{Path.GetFileName(path)}' not found at expected location. " +
                    "The model directory must follow HuggingFace diffusers layout: text_encoder/, " +
                    "text_encoder_2/, unet/, vae_decoder/, tokenizer/.",
                    path);
            }
        }

        _textEncoder1Session = OnnxSessionFactory.Create(textEncoder1Path);
        _textEncoder2Session = OnnxSessionFactory.Create(textEncoder2Path);
        _unetSession = OnnxSessionFactory.Create(unetPath);
        _vaeDecoderSession = OnnxSessionFactory.Create(vaeDecoderPath);

        using FileStream vocabStream = File.OpenRead(vocabPath);
        using FileStream mergesStream = File.OpenRead(mergesPath);
        _tokenizer = BpeTokenizer.Create(vocabStream, mergesStream);

        // Text encoder 2 has multiple outputs: the per-token hidden states
        // (used for cross-attention concat) and the pooled output (used for
        // SDXL's added_cond_kwargs.text_embeds). The latter is exposed
        // under the "text_embeds" output by diffusers ONNX exports — check
        // the metadata to find the right name.
        _encoder2PooledOutputName = _textEncoder2Session.OutputMetadata.Keys
            .FirstOrDefault(n => n.Contains("text_embeds", StringComparison.OrdinalIgnoreCase)
                              || n.Contains("pooled", StringComparison.OrdinalIgnoreCase))
            ?? _textEncoder2Session.OutputMetadata.Keys.Last(); // fallback: pooled is typically the last output

        _rng = seed.HasValue ? new Random(seed.Value) : new Random();

        // Diagnostic: print output names so we can verify correct selection.
        Console.Error.WriteLine("[SDXL diag] text_encoder_1 outputs: " +
            string.Join(", ", _textEncoder1Session.OutputMetadata.Keys));
        Console.Error.WriteLine("[SDXL diag] text_encoder_2 outputs: " +
            string.Join(", ", _textEncoder2Session.OutputMetadata.Keys));
        Console.Error.WriteLine("[SDXL diag] vae_decoder outputs: " +
            string.Join(", ", _vaeDecoderSession.OutputMetadata.Keys));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        CancellationToken cancellationToken)
    {
        _ = overrides;
        cancellationToken.ThrowIfCancellationRequested();
        if (inputs.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ValueRef>>([]);
        }

        return Task.Run<IReadOnlyList<ValueRef>>(() =>
        {
            ValueRef[] results = new ValueRef[inputs.Count];
            for (int i = 0; i < inputs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ValueRef promptRef = inputs[i][0];
                if (promptRef.IsNull)
                {
                    throw new InvalidOperationException(
                        $"SDXL-Turbo received a null prompt at row {i}; filter nulls upstream.");
                }
                string prompt = promptRef.AsString();
                byte[] pngBytes = GenerateImage(prompt, cancellationToken);
                results[i] = ValueRef.FromBytes(DataKind.Image, pngBytes);
            }
            return results;
        }, cancellationToken);
    }

    private byte[] GenerateImage(string prompt, CancellationToken cancellationToken)
    {
        // 1. Tokenize once. The same input_ids feed both text encoders.
        long[] inputIds = TokenizePrompt(prompt);

        cancellationToken.ThrowIfCancellationRequested();

        // 2. Run both text encoders. Encoder 1 → [1, 77, 768] hidden states.
        //    Encoder 2 → [1, 77, 1280] hidden states + [1, 1280] pooled.
        DenseTensor<float> textEmbeds1 = RunTextEncoder1(inputIds);
        AssertFinite(textEmbeds1, "text_encoder_1 output");
        (DenseTensor<float> textEmbeds2, DenseTensor<float> pooledTextEmbeds) =
            RunTextEncoder2(inputIds);
        AssertFinite(textEmbeds2, "text_encoder_2 hidden output");
        AssertFinite(pooledTextEmbeds, "text_encoder_2 pooled output");

        cancellationToken.ThrowIfCancellationRequested();

        // 3. Concatenate the two encoder outputs along the hidden dim →
        //    [1, 77, 2048]. This is the encoder_hidden_states fed to the UNet.
        DenseTensor<float> combinedTextEmbeds = ConcatenateLastDim(
            textEmbeds1, textEmbeds2, CLIPMaxTokens);

        // 4. Sample initial latent noise, scale to sigma_max.
        float[] noise = SampleNoise(LatentChannels * LatentHeight * LatentWidth);
        DenseTensor<float> noisyLatents = new(
            noise.Select(n => n * SigmaMax).ToArray(),
            [1, LatentChannels, LatentHeight, LatentWidth]);

        // 5. Scale model input — Euler scheduler convention.
        float scaleDivisor = MathF.Sqrt(SigmaMax * SigmaMax + 1f);
        float[] scaledLatentBuffer = new float[noisyLatents.Length];
        for (int i = 0; i < noisyLatents.Length; i++)
        {
            scaledLatentBuffer[i] = noisyLatents.Buffer.Span[i] / scaleDivisor;
        }
        DenseTensor<float> scaledLatents = new(
            scaledLatentBuffer,
            [1, LatentChannels, LatentHeight, LatentWidth]);

        cancellationToken.ThrowIfCancellationRequested();

        // 6. SDXL's added_cond_kwargs:
        //    - text_embeds = pooled output from encoder 2 [1, 1280]
        //    - time_ids = [orig_h, orig_w, crop_top, crop_left, target_h, target_w]
        //                 conventionally [1024, 1024, 0, 0, 1024, 1024] for full-frame.
        DenseTensor<float> timeIds = new(
            new float[] { ImageHeight, ImageWidth, 0f, 0f, ImageHeight, ImageWidth },
            [1, 6]);

        // 7. Run UNet.
        DenseTensor<float> noisePred = RunUnet(
            scaledLatents, combinedTextEmbeds, pooledTextEmbeds, timeIds);
        AssertFinite(noisePred, "UNet noise_pred output");

        // 8. Single Euler step from sigma_max to 0.
        float[] cleanLatentBuffer = new float[noisyLatents.Length];
        for (int i = 0; i < noisyLatents.Length; i++)
        {
            cleanLatentBuffer[i] = noisyLatents.Buffer.Span[i]
                - SigmaMax * noisePred.Buffer.Span[i];
        }

        // 9. Scale latents for VAE decoding (SDXL's scale factor).
        for (int i = 0; i < cleanLatentBuffer.Length; i++)
        {
            cleanLatentBuffer[i] /= VaeScaleFactor;
        }
        DenseTensor<float> cleanLatents = new(
            cleanLatentBuffer,
            [1, LatentChannels, LatentHeight, LatentWidth]);

        // fp16 max is 65504; after /vae_scale_factor (0.13025) the latent
        // values must stay below ~8532 or they will overflow to Inf when
        // cast to fp16 at the VAE decoder boundary, producing all-NaN output.
        AssertFinite(cleanLatents, "clean_latents (before VAE)");

        cancellationToken.ThrowIfCancellationRequested();

        // 10. VAE decode → RGB image in [-1, 1].
        DenseTensor<float> rgbImage = RunVaeDecoder(cleanLatents);
        AssertFinite(rgbImage, "VAE decoder output");

        // 11. Convert to PNG.
        return EncodeAsPng(rgbImage);
    }

    private long[] TokenizePrompt(string prompt)
    {
        IReadOnlyList<int> bpeTokens = _tokenizer.EncodeToIds(prompt.ToLowerInvariant());

        long[] result = new long[CLIPMaxTokens];
        result[0] = BosTokenId;
        int promptCount = Math.Min(bpeTokens.Count, CLIPMaxTokens - 2);
        for (int i = 0; i < promptCount; i++)
        {
            result[i + 1] = bpeTokens[i];
        }
        for (int i = promptCount + 1; i < CLIPMaxTokens; i++)
        {
            result[i] = EosTokenId;
        }
        return result;
    }

    private DenseTensor<float> RunTextEncoder1(long[] inputIds)
    {
        string inputName = _textEncoder1Session.InputMetadata.Keys.First();
        NamedOnnxValue input = OnnxTensorConversion.CreateAutoCastTokenInput(
            _textEncoder1Session, inputName, inputIds, [1, CLIPMaxTokens]);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            _textEncoder1Session.Run([input]);

        // optimum fp16 export order: [last_hidden_state, pooler_output, hidden_states.0..N]
        // Named lookup is explicit; fallback to index 0 in case the name differs.
        DisposableNamedOnnxValue hidden =
            outputs.FirstOrDefault(o => o.Name.Equals("last_hidden_state", StringComparison.OrdinalIgnoreCase))
            ?? outputs[0];
        return OnnxTensorConversion.ToFloatTensor(hidden);
    }

    /// <summary>
    /// Runs the second text encoder. Returns both the per-token hidden
    /// states (for cross-attention concat) and the pooled embedding (for
    /// SDXL's added_cond_kwargs.text_embeds).
    /// </summary>
    private (DenseTensor<float> Hidden, DenseTensor<float> Pooled) RunTextEncoder2(long[] inputIds)
    {
        string inputName = _textEncoder2Session.InputMetadata.Keys.First();
        NamedOnnxValue input = OnnxTensorConversion.CreateAutoCastTokenInput(
            _textEncoder2Session, inputName, inputIds, [1, CLIPMaxTokens]);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            _textEncoder2Session.Run([input]);

        // optimum fp16 export order: [text_embeds, last_hidden_state, hidden_states.0..N]
        // Explicit named lookups; fallback to first non-pooled position for portability.
        DisposableNamedOnnxValue? pooled = outputs.FirstOrDefault(o => o.Name == _encoder2PooledOutputName);
        DisposableNamedOnnxValue? hidden =
            outputs.FirstOrDefault(o => o.Name.Equals("last_hidden_state", StringComparison.OrdinalIgnoreCase))
            ?? outputs.FirstOrDefault(o => o.Name != _encoder2PooledOutputName);

        if (pooled is null || hidden is null)
        {
            throw new InvalidOperationException(
                $"Text encoder 2 didn't produce both pooled + hidden outputs " +
                $"(found: {string.Join(", ", outputs.Select(o => o.Name))}).");
        }

        return (
            OnnxTensorConversion.ToFloatTensor(hidden),
            OnnxTensorConversion.ToFloatTensor(pooled));
    }

    /// <summary>
    /// Concatenates two <c>[batch, seq, hidden_a/b]</c> tensors along the
    /// last (hidden) dim, producing <c>[batch, seq, hidden_a + hidden_b]</c>.
    /// SDXL's UNet expects this concat for cross-attention.
    /// </summary>
    private static DenseTensor<float> ConcatenateLastDim(
        DenseTensor<float> a, DenseTensor<float> b, int sequenceLength)
    {
        int hiddenA = a.Dimensions[2];
        int hiddenB = b.Dimensions[2];
        int totalHidden = hiddenA + hiddenB;

        float[] combined = new float[sequenceLength * totalHidden];
        ReadOnlySpan<float> aSpan = a.Buffer.Span;
        ReadOnlySpan<float> bSpan = b.Buffer.Span;

        for (int t = 0; t < sequenceLength; t++)
        {
            // Copy A's hidden_a values for this position.
            aSpan.Slice(t * hiddenA, hiddenA).CopyTo(combined.AsSpan(t * totalHidden, hiddenA));
            // Copy B's hidden_b values immediately after.
            bSpan.Slice(t * hiddenB, hiddenB)
                .CopyTo(combined.AsSpan(t * totalHidden + hiddenA, hiddenB));
        }

        return new DenseTensor<float>(combined, [1, sequenceLength, totalHidden]);
    }

    private DenseTensor<float> RunUnet(
        DenseTensor<float> scaledLatents,
        DenseTensor<float> combinedTextEmbeds,
        DenseTensor<float> pooledTextEmbeds,
        DenseTensor<float> timeIds)
    {
        // Timestep shape varies by export: optimum-cli exports use scalar [],
        // Microsoft's onnxruntime/sdxl-turbo export uses rank-1 [1].
        // Read the metadata so either model works without code changes.
        int[] timestepShape = _unetSession.InputMetadata["timestep"].Dimensions.Length == 1
            ? [1]
            : [];
        DenseTensor<float> timestepTensor = new(new float[] { TimestepValue }, timestepShape);

        var inputs = new List<NamedOnnxValue>
        {
            OnnxTensorConversion.CreateAutoCastInput(_unetSession, "sample", scaledLatents),
            OnnxTensorConversion.CreateAutoCastInput(_unetSession, "timestep", timestepTensor),
            OnnxTensorConversion.CreateAutoCastInput(_unetSession, "encoder_hidden_states", combinedTextEmbeds),
            OnnxTensorConversion.CreateAutoCastInput(_unetSession, "text_embeds", pooledTextEmbeds),
            OnnxTensorConversion.CreateAutoCastInput(_unetSession, "time_ids", timeIds),
        };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            _unetSession.Run(inputs);
        return OnnxTensorConversion.ToFloatTensor(outputs[0]);
    }

    private DenseTensor<float> RunVaeDecoder(DenseTensor<float> latents)
    {
        string inputName = _vaeDecoderSession.InputMetadata.Keys.First();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            _vaeDecoderSession.Run([
                OnnxTensorConversion.CreateAutoCastInput(_vaeDecoderSession, inputName, latents)]);
        return OnnxTensorConversion.ToFloatTensor(outputs[0]);
    }

    /// <summary>
    /// Box-Muller Gaussian sampler. Same impl as SD-Turbo.
    /// </summary>
    private float[] SampleNoise(int count)
    {
        float[] noise = new float[count];
        for (int i = 0; i < count; i += 2)
        {
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = 1.0 - _rng.NextDouble();
            double mag = Math.Sqrt(-2.0 * Math.Log(u1));
            double angle = 2.0 * Math.PI * u2;
            noise[i] = (float)(mag * Math.Cos(angle));
            if (i + 1 < count)
            {
                noise[i + 1] = (float)(mag * Math.Sin(angle));
            }
        }
        return noise;
    }

    private static byte[] EncodeAsPng(DenseTensor<float> rgbImage)
    {
        int[] shape = rgbImage.Dimensions.ToArray();
        if (shape.Length != 4 || shape[0] != 1 || shape[1] != 3
            || shape[2] != ImageHeight || shape[3] != ImageWidth)
        {
            throw new InvalidOperationException(
                $"VAE decoder produced unexpected shape [{string.Join(',', shape)}]. " +
                $"Expected [1, 3, {ImageHeight}, {ImageWidth}].");
        }

        ReadOnlySpan<float> flat = rgbImage.Buffer.Span;
        int planeSize = ImageHeight * ImageWidth;

        SKImageInfo info = new(ImageWidth, ImageHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap bitmap = new(info);
        nint pixelPtr = bitmap.GetPixels();
        unsafe
        {
            byte* dest = (byte*)pixelPtr;
            for (int yx = 0; yx < planeSize; yx++)
            {
                float r = flat[yx];
                float g = flat[planeSize + yx];
                float b = flat[2 * planeSize + yx];

                dest[yx * 4]     = NormalizeToByte(r);
                dest[yx * 4 + 1] = NormalizeToByte(g);
                dest[yx * 4 + 2] = NormalizeToByte(b);
                dest[yx * 4 + 3] = 255;
            }
        }

        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    // fp16 can represent values up to ±65504. Float32 values outside this
    // range silently become ±Inf when cast to Float16, which then produces
    // NaN inside any layer that does Inf-Inf arithmetic (attention softmax,
    // group norm, etc.).
    private const float Fp16Max = 65504f;

    private static void AssertFinite(DenseTensor<float> tensor, string label)
    {
        ReadOnlySpan<float> span = tensor.Buffer.Span;
        int nanCount = 0, infCount = 0, fp16OverflowCount = 0;
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < span.Length; i++)
        {
            float v = span[i];
            if (float.IsNaN(v)) { nanCount++; continue; }
            if (float.IsInfinity(v)) { infCount++; continue; }
            if (v < min) min = v;
            if (v > max) max = v;
            if (v > Fp16Max || v < -Fp16Max) fp16OverflowCount++;
        }

        // Always print to stderr so ranges are visible in the terminal
        // even when the check passes.
        Console.Error.WriteLine(
            $"[SDXL diag] {label} shape=[{string.Join(',', tensor.Dimensions.ToArray())}] " +
            $"range=[{min:G5}, {max:G5}] nan={nanCount} inf={infCount} fp16_overflow={fp16OverflowCount}");

        if (nanCount > 0 || infCount > 0)
            throw new InvalidOperationException(
                $"SDXL pipeline: {label} contains {nanCount} NaN and {infCount} Inf values " +
                $"out of {span.Length}. Finite range: [{min:G5}, {max:G5}].");

        if (fp16OverflowCount > 0)
            throw new InvalidOperationException(
                $"SDXL pipeline: {label} has {fp16OverflowCount} values outside fp16 range " +
                $"(±65504). Range: [{min:G5}, {max:G5}]. These become ±Inf when cast to fp16, " +
                $"causing NaN inside the next session.");
    }

    private static byte NormalizeToByte(float value)
    {
        float scaled = (value + 1f) * 0.5f * 255f;
        if (scaled < 0f) return 0;
        if (scaled > 255f) return 255;
        return (byte)Math.Round(scaled);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _textEncoder1Session.Dispose();
        _textEncoder2Session.Dispose();
        _unetSession.Dispose();
        _vaeDecoderSession.Dispose();
    }
}

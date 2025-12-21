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
/// The denoising loop uses the Euler discrete scheduler with a Karras
/// sigma sequence (ρ=7). <c>steps</c> controls quality: 1 step is fastest
/// (acceptable for abstract scenes), 4 steps is the recommended minimum
/// for faces and fine detail, 8 steps for hero outputs. SDXL-Turbo was
/// distilled for 1–4 steps; Juggernaut/Lightning models work best at 4–8.
/// </para>
/// </remarks>
public sealed class SdxlTurboModel : IModel, IDisposable
{
    // SDXL dimensions.
    private const int LatentChannels = 4;
    private const int ImageHeight = 1024;
    private const int ImageWidth = 1024;
    private const int LatentHeight = ImageHeight / 8;  // 128
    private const int LatentWidth = ImageWidth / 8;    // 128

    // CLIP tokenizer constants (shared with SD-Turbo).
    private const int CLIPMaxTokens = 77;
    private const int BosTokenId = 49406;
    private const int EosTokenId = 49407;

    private const float VaeScaleFactor = 0.13025f;  // SDXL's VAE — different from SD's 0.18215

    // fp16 max. Float32 values outside this range become ±Inf when cast,
    // which produces NaN inside attention softmax, group norm, etc.
    private const float Fp16Max = 65504f;

    private readonly InferenceSession _textEncoder1Session;
    private readonly InferenceSession _textEncoder2Session;
    private readonly InferenceSession _unetSession;
    private readonly InferenceSession _vaeDecoderSession;
    private readonly BpeTokenizer _tokenizer;
    private readonly Random _rng;

    // The pooled-output tensor from text encoder 2.
    private readonly string _encoder2PooledOutputName;

    // Euler+Karras denoising schedule precomputed in the constructor.
    private readonly int _steps;
    private readonly float[] _sigmas;      // length _steps+1; last entry is 0
    private readonly float[] _timesteps;   // length _steps; values in [0, 999]

    // Whether the UNet's "timestep" input is rank-1 [1] or scalar [].
    // Varies by export: optimum-cli → scalar; Microsoft onnxruntime build → [1].
    private readonly int[] _timestepShape;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsDeterministic => false;

    /// <inheritdoc />
    public IReadOnlyList<DataKind> InputKinds { get; } = [DataKind.String];

    /// <inheritdoc />
    public DataKind OutputKind => DataKind.Image;

    /// <inheritdoc />
    public int? PreferredBatchSize => 1;

    /// <summary>
    /// Loads an SDXL-based model from a directory in HuggingFace diffusers
    /// layout. Expects subfolders <c>text_encoder/</c>,
    /// <c>text_encoder_2/</c>, <c>unet/</c>, <c>vae_decoder/</c>,
    /// <c>tokenizer/</c>.
    /// </summary>
    /// <param name="name">Catalog-visible name.</param>
    /// <param name="modelDirectory">
    /// Absolute path to the model directory.
    /// </param>
    /// <param name="seed">
    /// Optional RNG seed for reproducible generation.
    /// </param>
    /// <param name="steps">
    /// Number of Euler denoising steps (default 4). Use 1 for maximum
    /// speed at the cost of face/detail quality; 4 is the recommended
    /// minimum for recognisable faces; 8 for hero outputs.
    /// </param>
    public SdxlTurboModel(string name, string modelDirectory, int? seed = null, int steps = 4)
    {
        Name = name;
        _steps = steps;

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
                    $"SDXL model component '{Path.GetFileName(path)}' not found at expected location. " +
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

        // Text encoder 2 has multiple outputs: per-token hidden states
        // (cross-attention concat) and the pooled output
        // (added_cond_kwargs.text_embeds). The latter is consistently
        // tagged "text_embeds" or "pooled" by diffusers ONNX exports.
        _encoder2PooledOutputName = _textEncoder2Session.OutputMetadata.Keys
            .FirstOrDefault(n => n.Contains("text_embeds", StringComparison.OrdinalIgnoreCase)
                              || n.Contains("pooled", StringComparison.OrdinalIgnoreCase))
            ?? _textEncoder2Session.OutputMetadata.Keys.Last();

        _rng = seed.HasValue ? new Random(seed.Value) : new Random();

        // Timestep tensor shape varies by export: optimum-cli → scalar [],
        // Microsoft onnxruntime/sdxl-turbo build → rank-1 [1]. Read once.
        _timestepShape = _unetSession.InputMetadata["timestep"].Dimensions.Length == 1
            ? [1] : [];

        (_sigmas, _timesteps) = ComputeSchedule(steps);
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
                        $"SDXL model received a null prompt at row {i}; filter nulls upstream.");
                }
                string prompt = promptRef.AsString();
                SKBitmap bitmap = GenerateImage(prompt, cancellationToken);
                results[i] = ValueRef.FromImage(bitmap);
            }
            return results;
        }, cancellationToken);
    }

    private SKBitmap GenerateImage(string prompt, CancellationToken cancellationToken)
    {
        // 1. Tokenize once — the same input_ids feed both text encoders.
        long[] inputIds = TokenizePrompt(prompt);

        cancellationToken.ThrowIfCancellationRequested();

        // 2. Run both text encoders.
        //    Encoder 1 → [1, 77, 768] hidden states.
        //    Encoder 2 → [1, 77, 1280] hidden states + [1, 1280] pooled.
        DenseTensor<float> textEmbeds1 = RunTextEncoder1(inputIds);
        ThrowIfNotFinite(textEmbeds1, "text_encoder_1 output");
        (DenseTensor<float> textEmbeds2, DenseTensor<float> pooledTextEmbeds) =
            RunTextEncoder2(inputIds);
        ThrowIfNotFinite(textEmbeds2, "text_encoder_2 hidden output");
        ThrowIfNotFinite(pooledTextEmbeds, "text_encoder_2 pooled output");

        cancellationToken.ThrowIfCancellationRequested();

        // 3. Concatenate encoder outputs along the hidden dim → [1, 77, 2048].
        DenseTensor<float> combinedTextEmbeds = ConcatenateLastDim(
            textEmbeds1, textEmbeds2, CLIPMaxTokens);

        // 4. SDXL's added_cond_kwargs.time_ids:
        //    [orig_h, orig_w, crop_top, crop_left, target_h, target_w].
        DenseTensor<float> timeIds = new(
            new float[] { ImageHeight, ImageWidth, 0f, 0f, ImageHeight, ImageWidth },
            [1, 6]);

        // 5. Initial noisy latent scaled to sigma_max.
        float sigmaMax = _sigmas[0];
        float[] latentBuffer = new float[LatentChannels * LatentHeight * LatentWidth];
        float[] rawNoise = SampleNoise(latentBuffer.Length);
        for (int i = 0; i < latentBuffer.Length; i++)
            latentBuffer[i] = rawNoise[i] * sigmaMax;

        // 6. Multi-step Euler denoising with Karras sigma schedule.
        //
        //    Each step:
        //      a. Scale model input by c_in = 1/sqrt(sigma^2+1)
        //      b. Run UNet at the training timestep nearest to this sigma
        //      c. Euler update: x_next = x + (sigma_next - sigma) * noise_pred
        //         sigma_next < sigma, so delta < 0 (removes noise each step)
        for (int step = 0; step < _steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            float sigma = _sigmas[step];
            float sigmaNext = _sigmas[step + 1];
            float scale = 1f / MathF.Sqrt(sigma * sigma + 1f);

            float[] scaledBuffer = new float[latentBuffer.Length];
            for (int i = 0; i < latentBuffer.Length; i++)
                scaledBuffer[i] = latentBuffer[i] * scale;
            DenseTensor<float> scaledLatents = new(
                scaledBuffer, [1, LatentChannels, LatentHeight, LatentWidth]);

            DenseTensor<float> noisePred = RunUnet(
                scaledLatents, combinedTextEmbeds, pooledTextEmbeds, timeIds,
                timestep: _timesteps[step]);
            ThrowIfNotFinite(noisePred, $"UNet noise_pred (step {step + 1}/{_steps})");

            float delta = sigmaNext - sigma;
            ReadOnlySpan<float> pred = noisePred.Buffer.Span;
            for (int i = 0; i < latentBuffer.Length; i++)
                latentBuffer[i] += delta * pred[i];
        }

        // 7. Scale latents for VAE decoding.
        for (int i = 0; i < latentBuffer.Length; i++)
            latentBuffer[i] /= VaeScaleFactor;
        DenseTensor<float> cleanLatents = new(
            latentBuffer, [1, LatentChannels, LatentHeight, LatentWidth]);
        ThrowIfNotFinite(cleanLatents, "clean_latents (before VAE)");

        cancellationToken.ThrowIfCancellationRequested();

        // 8. VAE decode → RGB image in [-1, 1].
        DenseTensor<float> rgbImage = RunVaeDecoder(cleanLatents);
        ThrowIfNotFinite(rgbImage, "VAE decoder output");

        // 9. Materialise into an SKBitmap; PNG encoding (if any) happens at
        //    the arena boundary in ValueRef.ToDataValue.
        return DecodeToBitmap(rgbImage);
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

        // Named lookup explicit; fallback to index 0 for portability.
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
            aSpan.Slice(t * hiddenA, hiddenA).CopyTo(combined.AsSpan(t * totalHidden, hiddenA));
            bSpan.Slice(t * hiddenB, hiddenB)
                .CopyTo(combined.AsSpan(t * totalHidden + hiddenA, hiddenB));
        }

        return new DenseTensor<float>(combined, [1, sequenceLength, totalHidden]);
    }

    private DenseTensor<float> RunUnet(
        DenseTensor<float> scaledLatents,
        DenseTensor<float> combinedTextEmbeds,
        DenseTensor<float> pooledTextEmbeds,
        DenseTensor<float> timeIds,
        float timestep)
    {
        DenseTensor<float> timestepTensor = new(new float[] { timestep }, _timestepShape);

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
    /// Box-Muller Gaussian sampler.
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

    private static SKBitmap DecodeToBitmap(DenseTensor<float> rgbImage)
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
        SKBitmap bitmap = new(info);
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

        return bitmap;
    }

    /// <summary>
    /// Precomputes the denoising schedule for <paramref name="numSteps"/>
    /// Euler steps, matching the <c>EulerDiscreteScheduler</c> config used
    /// by SDXL-Turbo, SD-Turbo, and SDXL-Lightning models.
    /// </summary>
    /// <remarks>
    /// Both ADD-distilled (SDXL-Turbo / SD-Turbo) and Lightning-distilled
    /// (Juggernaut XL Lightning) models use <c>timestep_spacing="trailing"</c>:
    /// timesteps are spaced evenly from the high-noise end of the training
    /// schedule.
    ///
    /// For 4 steps this gives <c>[999, 749, 499, 249]</c>; for 8 steps
    /// <c>[999, 874, 749, 624, 499, 374, 249, 124]</c>. These are the
    /// specific noise levels the models were distilled to denoise in N steps,
    /// so using any other spacing (e.g., Karras ρ-space) sends intermediate
    /// queries outside the distillation distribution and causes degenerate
    /// output (multiple overlapping faces, blurry noise clouds).
    /// </remarks>
    internal static (float[] Sigmas, float[] Timesteps) ComputeSchedule(int numSteps)
    {
        const float BetaStart = 0.00085f;
        const float BetaEnd = 0.012f;
        const int NumTrainSteps = 1000;

        // Precompute sigma for every training timestep from SDXL's
        // scaled-linear beta schedule.
        float[] alphasCumprod = new float[NumTrainSteps];
        float cumAlpha = 1f;
        for (int i = 0; i < NumTrainSteps; i++)
        {
            float t = (float)i / (NumTrainSteps - 1);
            float sqrtBeta = MathF.Sqrt(BetaStart) + t * (MathF.Sqrt(BetaEnd) - MathF.Sqrt(BetaStart));
            cumAlpha *= 1f - sqrtBeta * sqrtBeta;
            alphasCumprod[i] = cumAlpha;
        }

        float[] trainSigmas = new float[NumTrainSteps];
        for (int i = 0; i < NumTrainSteps; i++)
            trainSigmas[i] = MathF.Sqrt((1f - alphasCumprod[i]) / alphasCumprod[i]);

        // "trailing" timestep spacing: np.arange(1000, 0, -1000/N) - 1
        // For N=4: [999, 749, 499, 249]
        // For N=1: [999]
        float stepRatio = (float)NumTrainSteps / numSteps;
        float[] timesteps = new float[numSteps];
        float[] sigmas = new float[numSteps + 1];
        for (int i = 0; i < numSteps; i++)
        {
            int t = (int)MathF.Round(NumTrainSteps - i * stepRatio) - 1;
            t = Math.Clamp(t, 0, NumTrainSteps - 1);
            timesteps[i] = t;
            sigmas[i] = trainSigmas[t];
        }
        sigmas[numSteps] = 0f;

        return (sigmas, timesteps);
    }

    private static void ThrowIfNotFinite(DenseTensor<float> tensor, string label)
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

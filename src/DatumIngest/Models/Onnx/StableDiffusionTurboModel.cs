using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// Stability AI SD-Turbo — text-to-image diffusion model that generates
/// 512×512 images in a single denoising step. Implements the diffusers
/// pipeline directly across three ONNX sessions (text encoder, UNet,
/// VAE decoder).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why custom over OnnxStack.</strong> The diffusion pipeline
/// is multi-stage but each stage is a straightforward ONNX dispatch.
/// Implementing it ourselves keeps the integration consistent with
/// existing multi-session models (Florence-2, ViT-GPT2), uses the
/// CLIP BPE tokenizer via <see cref="BpeTokenizer"/> (already a
/// dependency), and avoids the abstraction layers a third-party
/// pipeline library introduces.
/// </para>
/// <para>
/// <strong>SD-Turbo specifics.</strong> Stability AI fine-tuned SD 2.1
/// for 1-step inference at the highest training-time noise level. The
/// scheduler config is essentially "skip the iterative denoising — go
/// from sigma_max directly to sigma=0 in one Euler step." Compared to
/// full SD's 25-50 steps, this is dramatically faster (~1-2s per image
/// on consumer GPUs).
/// </para>
/// <para>
/// <strong>Pipeline.</strong>
/// <code>
/// prompt        →  CLIP tokenizer    →  input_ids [1, 77]
///                                            ↓
///                                       text_encoder    →  text_embeds [1, 77, 1024]
///                                                                 ↓
/// random noise  →  scale to sigma_max ─→ noisy latent  →   UNet   →  noise_pred
///                                                                 ↓
///                              latent_clean = noisy − sigma_max × noise_pred
///                                                                 ↓
///                              latent_clean ÷ vae_scale_factor    ↓
///                                                       vae_decoder
///                                                                 ↓
///                                                       RGB image [-1, 1]
///                                                                 ↓
///                                                       SKBitmap (RGBA8888)
/// </code>
/// </para>
/// <para>
/// <strong>Streaming.</strong> <see cref="PreferredBatchSize"/> = 1 so
/// each generated image emits as soon as it's done — the user sees the
/// first result in ~1–2s rather than waiting for a multi-image batch.
/// Important for an interactive demo where seeing the first image fast
/// is the visceral payoff.
/// </para>
/// </remarks>
public sealed class StableDiffusionTurboModel : IModel, IDisposable
{
    // SD-Turbo / SD 2.1 latent + image dimensions.
    private const int LatentChannels = 4;
    private const int ImageHeight = 512;
    private const int ImageWidth = 512;
    private const int LatentHeight = ImageHeight / 8;  // = 64
    private const int LatentWidth = ImageWidth / 8;    // = 64

    // CLIP tokenizer constants (shared across SD 2.x).
    private const int CLIPMaxTokens = 77;
    private const int BosTokenId = 49406;  // <|startoftext|>
    private const int EosTokenId = 49407;  // <|endoftext|>
    // Pad with EOS to reach CLIPMaxTokens — standard CLIP convention.

    private const float VaeScaleFactor = 0.18215f; // SD 2.x VAE scaling

    private readonly InferenceSession _textEncoderSession;
    private readonly InferenceSession _unetSession;
    private readonly InferenceSession _vaeDecoderSession;
    private readonly BpeTokenizer _tokenizer;
    private readonly Random _rng;

    // Euler+Karras denoising schedule precomputed in the constructor.
    private readonly int _steps;
    private readonly float[] _sigmas;      // length _steps+1; last entry is 0
    private readonly float[] _timesteps;   // length _steps; values in [0, 999]

    /// <summary>The catalog name this model is registered under.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public bool IsDeterministic => false;

    /// <inheritdoc />
    public IReadOnlyList<DataKind> InputKinds { get; } = [DataKind.String];

    /// <inheritdoc />
    public DataKind OutputKind => DataKind.Image;

    /// <inheritdoc />
    /// <remarks>
    /// SD-Turbo emits one image per source row at ~1–2s per generation.
    /// Sub-batching at 1 means each image streams to the user as soon
    /// as it's done — first result in ~1.5s, not "all 5 images at the
    /// 7-second mark."
    /// </remarks>
    public int? PreferredBatchSize => 1;

    /// <summary>
    /// Loads SD-Turbo from a directory in HuggingFace diffusers layout
    /// (<c>text_encoder/</c>, <c>unet/</c>, <c>vae_decoder/</c>,
    /// <c>tokenizer/</c> subfolders).
    /// </summary>
    /// <param name="name">Catalog-visible name.</param>
    /// <param name="modelDirectory">
    /// Absolute path to the SD-Turbo model directory. Must contain the
    /// diffusers-standard subfolders for the four pipeline components.
    /// </param>
    /// <param name="seed">
    /// Optional RNG seed. <see langword="null"/> uses a time-based seed
    /// (different output every call); non-null produces reproducible
    /// outputs from the same prompt.
    /// </param>
    /// <param name="steps">
    /// Number of Euler denoising steps (default 4). Use 1 for maximum
    /// speed; 4 for good face/detail quality. SD-Turbo was distilled for
    /// 1–4 steps; beyond 4 quality gains are minimal.
    /// </param>
    public StableDiffusionTurboModel(string name, string modelDirectory, int? seed = null, int steps = 4)
    {
        Name = name;
        _steps = steps;

        string textEncoderPath = Path.Combine(modelDirectory, "text_encoder", "model.onnx");
        string unetPath = Path.Combine(modelDirectory, "unet", "model.onnx");
        string vaeDecoderPath = Path.Combine(modelDirectory, "vae_decoder", "model.onnx");
        string vocabPath = Path.Combine(modelDirectory, "tokenizer", "vocab.json");
        string mergesPath = Path.Combine(modelDirectory, "tokenizer", "merges.txt");

        foreach (string path in new[] { textEncoderPath, unetPath, vaeDecoderPath, vocabPath, mergesPath })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"SD-Turbo component '{Path.GetFileName(path)}' not found at expected location. " +
                    $"The model directory must follow HuggingFace diffusers layout: " +
                    $"text_encoder/model.onnx, unet/model.onnx, vae_decoder/model.onnx, " +
                    $"tokenizer/vocab.json, tokenizer/merges.txt.",
                    path);
            }
        }

        _textEncoderSession = OnnxSessionFactory.Create(textEncoderPath);
        _unetSession = OnnxSessionFactory.Create(unetPath);
        _vaeDecoderSession = OnnxSessionFactory.Create(vaeDecoderPath);

        using FileStream vocabStream = File.OpenRead(vocabPath);
        using FileStream mergesStream = File.OpenRead(mergesPath);
        _tokenizer = BpeTokenizer.Create(vocabStream, mergesStream);

        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
        (_sigmas, _timesteps) = SdxlTurboModel.ComputeSchedule(steps);
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

        // Diffusion is GPU-bound; wrap in Task.Run so the operator's async
        // loop doesn't block the calling thread for the whole dispatch.
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
                        $"SD-Turbo received a null prompt at row {i}; filter nulls upstream.");
                }
                string prompt = promptRef.AsString();
                SKBitmap bitmap = GenerateImage(prompt, cancellationToken);
                results[i] = ValueRef.FromImage(bitmap);
            }
            return results;
        }, cancellationToken);
    }

    /// <summary>
    /// Generates a single 512×512 <see cref="SKBitmap"/>. The full SD-Turbo
    /// pipeline: tokenize → encode text → sample noise → run UNet → decode
    /// VAE → write into a Skia bitmap. The caller owns the returned bitmap.
    /// </summary>
    private SKBitmap GenerateImage(string prompt, CancellationToken cancellationToken)
    {
        // 1. Tokenize. CLIP wraps the prompt with [BOS, ..., EOS] padded with
        //    EOS to exactly 77 tokens.
        long[] inputIds = TokenizePrompt(prompt);

        cancellationToken.ThrowIfCancellationRequested();

        // 2. Run text encoder. Output shape is [1, 77, hidden_dim]; for SD 2.1
        //    based SD-Turbo, hidden_dim = 1024.
        DenseTensor<float> textEmbeds = RunTextEncoder(inputIds);

        cancellationToken.ThrowIfCancellationRequested();

        // 3. Initial noisy latent scaled to sigma_max.
        float sigmaMax = _sigmas[0];
        float[] latentBuffer = new float[LatentChannels * LatentHeight * LatentWidth];
        float[] rawNoise = SampleNoise(latentBuffer.Length);
        for (int i = 0; i < latentBuffer.Length; i++)
            latentBuffer[i] = rawNoise[i] * sigmaMax;

        // 4. Multi-step Euler denoising with Karras sigma schedule.
        //    Each step: scale by c_in, run UNet at sigma's timestep, Euler update.
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

            DenseTensor<float> noisePred = RunUnet(scaledLatents, textEmbeds, _timesteps[step]);

            float delta = sigmaNext - sigma;
            ReadOnlySpan<float> pred = noisePred.Buffer.Span;
            for (int i = 0; i < latentBuffer.Length; i++)
                latentBuffer[i] += delta * pred[i];
        }

        // 5. Scale latents for VAE decoding.
        for (int i = 0; i < latentBuffer.Length; i++)
            latentBuffer[i] /= VaeScaleFactor;
        DenseTensor<float> cleanLatents = new(
            latentBuffer, [1, LatentChannels, LatentHeight, LatentWidth]);

        cancellationToken.ThrowIfCancellationRequested();

        // 6. VAE decode → RGB image in [-1, 1].
        DenseTensor<float> rgbImage = RunVaeDecoder(cleanLatents);

        // 7. Convert [-1, 1] floats to [0, 255] uint8 RGBA bytes in an SKBitmap.
        return DecodeToBitmap(rgbImage);
    }

    private long[] TokenizePrompt(string prompt)
    {
        // BPE-tokenize the prompt. CLIP's tokenizer doesn't add BOS/EOS or
        // pad to 77 — we do that here to match the diffusers reference
        // implementation.
        IReadOnlyList<int> bpeTokens = _tokenizer.EncodeToIds(prompt.ToLowerInvariant());

        // Build [BOS, ...prompt..., EOS, EOS, ..., EOS] of length 77.
        // If the prompt is too long, truncate to leave room for BOS + EOS.
        long[] result = new long[CLIPMaxTokens];
        result[0] = BosTokenId;
        int promptCount = Math.Min(bpeTokens.Count, CLIPMaxTokens - 2);
        for (int i = 0; i < promptCount; i++)
        {
            result[i + 1] = bpeTokens[i];
        }
        // Append EOS, then pad with EOS.
        for (int i = promptCount + 1; i < CLIPMaxTokens; i++)
        {
            result[i] = EosTokenId;
        }
        return result;
    }

    private DenseTensor<float> RunTextEncoder(long[] inputIds)
    {
        DenseTensor<long> inputTensor = new(inputIds, [1, CLIPMaxTokens]);
        string inputName = _textEncoderSession.InputMetadata.Keys.First();

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            _textEncoderSession.Run([NamedOnnxValue.CreateFromTensor(inputName, inputTensor)]);
        return OnnxTensorConversion.ToFloatTensor(outputs[0]);
    }

    private DenseTensor<float> RunUnet(
        DenseTensor<float> scaledLatents, DenseTensor<float> textEmbeds, float timestep)
    {
        // SD-Turbo's diffusers ONNX export expects timestep as a *scalar*
        // (rank-0 tensor, shape []) — not rank-1 [1]. Passing [1] triggers
        // a Split-node failure in the sinusoidal time-projection block.
        DenseTensor<float> timestepTensor = new(new float[] { timestep }, []);

        var inputs = new List<NamedOnnxValue>
        {
            OnnxTensorConversion.CreateAutoCastInput(_unetSession, "sample", scaledLatents),
            OnnxTensorConversion.CreateAutoCastInput(_unetSession, "timestep", timestepTensor),
            OnnxTensorConversion.CreateAutoCastInput(_unetSession, "encoder_hidden_states", textEmbeds),
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
    /// Samples Gaussian noise via Box-Muller transform. Matches the
    /// behaviour of <c>torch.randn</c> closely enough for SD-Turbo (which
    /// is robust to small distribution differences in the seed noise).
    /// </summary>
    private float[] SampleNoise(int count)
    {
        float[] noise = new float[count];
        for (int i = 0; i < count; i += 2)
        {
            // Box-Muller: produces two independent N(0, 1) samples per pair.
            double u1 = 1.0 - _rng.NextDouble();  // ensure (0, 1] to avoid log(0)
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

    /// <summary>
    /// Converts a VAE-decoded <c>[1, 3, 512, 512]</c> tensor in [-1, 1]
    /// range to an <see cref="SKBitmap"/>. Output is RGBA (alpha forced to
    /// fully opaque); range mapped (x + 1) / 2 × 255 with clamping. The
    /// caller owns the returned bitmap.
    /// </summary>
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
                float r = flat[yx];                  // R plane
                float g = flat[planeSize + yx];      // G plane
                float b = flat[2 * planeSize + yx];  // B plane

                // Map [-1, 1] → [0, 255] with clamp.
                dest[yx * 4]     = NormalizeToByte(r);
                dest[yx * 4 + 1] = NormalizeToByte(g);
                dest[yx * 4 + 2] = NormalizeToByte(b);
                dest[yx * 4 + 3] = 255;  // alpha
            }
        }

        return bitmap;
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
        _textEncoderSession.Dispose();
        _unetSession.Dispose();
        _vaeDecoderSession.Dispose();
    }
}

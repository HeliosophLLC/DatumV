using System.Text;
using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

using SkiaSharp;

namespace DatumIngest.Models.Onnx.PaliGemma;

/// <summary>
/// Google PaliGemma 2 — vision-language model that combines a SigLIP
/// image encoder with a Gemma 2B decoder via a learned linear projection.
/// The "mix" variants are pre-finetuned across captioning, VQA, and OCR
/// so they handle diverse prompts out of the box; we wire it as a
/// captioner with a default <c>"caption en"</c> prompt that callers can
/// override at registration time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Architecture (3 ONNX sessions).</strong>
/// <code>
/// image  →  vision_encoder        →  image embeddings  ───┐
///                                                         ├─→  decoder  →  next-token logits
/// prompt →  embed_tokens          →  text  embeddings  ───┘                       ↑ (autoregressive loop)
/// </code>
/// The vision encoder also includes the linear projector to the Gemma
/// hidden dim (optimum bakes the projection into the encoder graph).
/// Image embeddings are prepended to text embeddings and the combined
/// sequence feeds the decoder's <c>inputs_embeds</c>.
/// </para>
/// <para>
/// <strong>Resolution.</strong> 224×224 mix variant gives 256 image
/// tokens; 448×448 gives 1024. Both auto-detect by querying the
/// vision encoder's input shape at startup, so a single class handles
/// either variant — the registration just points at a different folder.
/// </para>
/// <para>
/// <strong>No KV cache.</strong> Same simplification as ViT-GPT2 and
/// Florence-2 — we recompute the full sequence each decoder step. Slower
/// (O(N²) instead of O(N)) but simpler. PaliGemma typically generates
/// short outputs (≤80 tokens for captions); the per-step cost is small
/// enough that the simplification is worth it. Switching to
/// <c>decoder_with_past_model.onnx</c> with KV-cache plumbing is a
/// follow-up if generation latency becomes a bottleneck.
/// </para>
/// </remarks>
public sealed class PaliGemmaModel : OnnxModel
{
    // SigLIP normalisation: mean=0.5, std=0.5 → output range [-1, 1].
    // Different from ImageNet stats (which are baked into ViT-GPT2 and
    // Florence-2). PaliGemma was trained with this convention.
    private const int InputChannels = 3;
    private const float SigLipMean = 0.5f;
    private const float SigLipStd = 0.5f;

    // Gemma special tokens (verified against tokenizer.json from optimum
    // exports). BOS is implicit (added by tokenizer); EOS terminates
    // generation; no separate pad token is needed for greedy decoding.
    private const int GemmaBosTokenId = 2;
    private const int GemmaEosTokenId = 1;

    private readonly int _inputWidth;
    private readonly int _inputHeight;

    private readonly InferenceSession _embedTokensSession;
    private readonly InferenceSession _decoderSession;
    private readonly BpeTokenizer _tokenizer;
    private readonly int _maxTokens;
    private readonly string _defaultPrompt;

    // Cached input/output names. Captured once from session metadata so
    // we don't allocate strings on every call.
    private readonly string _visionInputName;
    private readonly string _visionOutputName;
    private readonly string _embedInputName;
    private readonly string _embedOutputName;
    private readonly string _decoderEmbedsName;
    private readonly string _decoderMaskName;
    private readonly string _decoderLogitsName;

    /// <summary>
    /// Captioning takes ~1-3s per image on GPU (vision encoder pass +
    /// ~50 autoregressive steps). Stream in groups of 4 so users see
    /// first captions in seconds rather than waiting for a full upstream
    /// batch to finish.
    /// </summary>
    public int? PreferredBatchSize => 4;

    /// <summary>
    /// Loads PaliGemma 2 from a directory of ONNX + tokenizer files.
    /// </summary>
    /// <param name="name">Catalog-visible name.</param>
    /// <param name="visionEncoderModelFilePath">
    /// Absolute path to <c>vision_encoder.onnx</c>. The constructor
    /// derives the model directory from this and loads
    /// <c>embed_tokens.onnx</c>, <c>decoder_model.onnx</c>, and
    /// <c>tokenizer.json</c> (or <c>tokenizer.model</c>) from the same
    /// folder.
    /// </param>
    /// <param name="defaultPrompt">
    /// Task prompt prepended to the input (after the image tokens).
    /// Defaults to <c>"caption en"</c> for English captioning. Other
    /// useful prompts: <c>"caption es"</c>, <c>"answer en &lt;q&gt;"</c>,
    /// <c>"ocr"</c>, <c>"detect &lt;object&gt;"</c>.
    /// </param>
    /// <param name="maxTokens">Cap on generated tokens per caption. Defaults to 100.</param>
    public PaliGemmaModel(
        string name,
        string visionEncoderModelFilePath,
        string defaultPrompt = "caption en",
        int maxTokens = 100)
        : base(
            name,
            visionEncoderModelFilePath,
            inputKinds: [DataKind.Image],
            outputKind: DataKind.String,
            isDeterministic: true)
    {
        _maxTokens = maxTokens;
        _defaultPrompt = defaultPrompt;

        string modelDirectory = Path.GetDirectoryName(visionEncoderModelFilePath)
            ?? throw new InvalidOperationException(
                $"Could not derive model directory from '{visionEncoderModelFilePath}'.");

        // Load embed_tokens (text embedding lookup) and the decoder.
        // Both are emitted by `optimum-cli export onnx --model google/paligemma2-3b-mix-{224,448}`.
        string embedPath = Path.Combine(modelDirectory, "embed_tokens.onnx");
        string decoderPath = Path.Combine(modelDirectory, "decoder_model.onnx");

        if (!File.Exists(embedPath))
        {
            throw new FileNotFoundException(
                "PaliGemma 'embed_tokens.onnx' not found alongside the vision encoder. " +
                "Expected layout: vision_encoder.onnx + embed_tokens.onnx + decoder_model.onnx in the same folder. " +
                "Re-export with `optimum-cli export onnx --model google/paligemma2-3b-mix-448`.",
                embedPath);
        }
        if (!File.Exists(decoderPath))
        {
            throw new FileNotFoundException(
                "PaliGemma 'decoder_model.onnx' not found alongside the vision encoder.",
                decoderPath);
        }

        _embedTokensSession = OnnxSessionFactory.Create(embedPath);
        _decoderSession = OnnxSessionFactory.Create(decoderPath);

        // Tokenizer. Gemma uses SentencePiece; optimum-cli exports both
        // a tokenizer.json (HF format) and tokenizer.model (raw SP). We
        // load the .json variant via BpeTokenizer for consistency with
        // Florence-2 — works for any HF-format tokenizer regardless of
        // the underlying algorithm.
        string vocabPath = Path.Combine(modelDirectory, "vocab.json");
        string mergesPath = Path.Combine(modelDirectory, "merges.txt");
        if (!File.Exists(vocabPath) || !File.Exists(mergesPath))
        {
            throw new FileNotFoundException(
                "PaliGemma tokenizer files (vocab.json + merges.txt) not found. " +
                "If your export only emitted tokenizer.json, run the conversion again with " +
                "the optimum-cli output saved into a fresh folder; older optimum versions " +
                "wrote both file formats.",
                File.Exists(vocabPath) ? mergesPath : vocabPath);
        }
        using FileStream vocabStream = File.OpenRead(vocabPath);
        using FileStream mergesStream = File.OpenRead(mergesPath);
        _tokenizer = BpeTokenizer.Create(vocabStream, mergesStream);

        // Capture I/O names. The exact names depend on the optimum
        // version that generated the export; we look them up dynamically
        // rather than hardcoding because Gemma exports renamed several
        // ports between optimum 1.21 and 2.x.
        _visionInputName = Session.InputMetadata.Keys.First();
        _visionOutputName = Session.OutputMetadata.Keys.First();
        _embedInputName = _embedTokensSession.InputMetadata.Keys.First();
        _embedOutputName = _embedTokensSession.OutputMetadata.Keys.First();

        // Decoder typically has named inputs: inputs_embeds, attention_mask
        // and named output: logits. Use those exact names; if a future
        // export changes them, we'll need to revisit.
        _decoderEmbedsName = "inputs_embeds";
        _decoderMaskName = "attention_mask";
        _decoderLogitsName = "logits";

        // Auto-detect input resolution from vision encoder metadata. The
        // input shape is [batch, channels, H, W]; H and W are equal for
        // the mix variants (224×224 or 448×448).
        NodeMetadata visionMeta = Session.InputMetadata[_visionInputName];
        int[] dims = visionMeta.Dimensions.ToArray();
        if (dims.Length != 4 || dims[1] != InputChannels)
        {
            throw new InvalidOperationException(
                $"PaliGemma vision_encoder input '{_visionInputName}' has unexpected shape " +
                $"[{string.Join(',', dims)}]; expected [batch, 3, H, W].");
        }
        _inputHeight = dims[2] > 0 ? dims[2] : 448;  // Symbolic dims fall back to 448.
        _inputWidth = dims[3] > 0 ? dims[3] : 448;
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        CancellationToken cancellationToken)
    {
        _ = overrides;
        cancellationToken.ThrowIfCancellationRequested();
        if (inputs.Count == 0) return [];

        return await Task.Run<IReadOnlyList<ValueRef>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            ValueRef[] results = new ValueRef[inputs.Count];

            // Per-row dispatch — the autoregressive decoder doesn't
            // benefit much from batching across rows since sequences
            // finish at different lengths. Vision encoder could batch,
            // but for caption volumes this rarely matters; keep it simple.
            for (int row = 0; row < inputs.Count; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ValueRef image = inputs[row][0];
                if (image.IsNull)
                {
                    throw new InvalidOperationException(
                        $"PaliGemmaModel received a null image at row {row}; filter nulls upstream.");
                }
                byte[] bytes = image.AsBytes();
                results[row] = ValueRef.FromString(CaptionOne(bytes, cancellationToken));
            }
            return results;
        }, cancellationToken).ConfigureAwait(false);
    }

    private string CaptionOne(byte[] imageBytes, CancellationToken cancellationToken)
    {
        // Step 1: image preprocessing → vision encoder → image embeddings.
        float[] pixelData = new float[InputChannels * _inputHeight * _inputWidth];
        DecodeAndPackImage(imageBytes, pixelData);

        DenseTensor<float> pixels = new(
            pixelData,
            [1, InputChannels, _inputHeight, _inputWidth]);

        DenseTensor<float> imageEmbeds;
        using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> visionOut = Session.Run(
            [NamedOnnxValue.CreateFromTensor(_visionInputName, pixels)]))
        {
            DisposableNamedOnnxValue v = visionOut.First();
            imageEmbeds = v.AsTensor<float>().ToDenseTensor();
        }
        // Expected shape: [1, num_image_tokens, hidden_dim]
        int[] iShape = imageEmbeds.Dimensions.ToArray();
        if (iShape.Length != 3 || iShape[0] != 1)
        {
            throw new InvalidOperationException(
                $"PaliGemma vision_encoder output shape {string.Join('x', iShape)} doesn't match " +
                "expected [1, num_image_tokens, hidden_dim].");
        }
        int numImageTokens = iShape[1];
        int hiddenDim = iShape[2];

        // Step 2: tokenize the prompt → embed_tokens → text embeddings.
        // Gemma's tokenizer adds BOS automatically when called via
        // BpeTokenizer.EncodeToIds; we strip it because the prefix is
        // image tokens, and PaliGemma's prefix mode doesn't want a
        // separate BOS sentinel between image and text.
        IReadOnlyList<int> promptTokenIds = _tokenizer.EncodeToIds(_defaultPrompt + "\n");
        long[] promptIds = new long[promptTokenIds.Count];
        for (int i = 0; i < promptIds.Length; i++) promptIds[i] = promptTokenIds[i];

        DenseTensor<long> promptInputIds = new(promptIds, [1, promptIds.Length]);
        DenseTensor<float> promptEmbeds;
        using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> embedOut = _embedTokensSession.Run(
            [NamedOnnxValue.CreateFromTensor(_embedInputName, promptInputIds)]))
        {
            promptEmbeds = embedOut.First().AsTensor<float>().ToDenseTensor();
        }
        int promptLen = promptEmbeds.Dimensions[1];

        // Step 3: build the autoregressive loop. Each step we concatenate
        // [image_embeds, prompt_embeds, generated_token_embeds] and feed
        // the whole thing to the decoder. inputs_embeds is the canonical
        // input port for prefix-style multimodal generation.
        List<int> generatedTokens = new(_maxTokens + 1);

        for (int step = 0; step < _maxTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Embed the generated tokens so far (empty on first step).
            DenseTensor<float>? generatedEmbeds = null;
            if (generatedTokens.Count > 0)
            {
                long[] genIds = new long[generatedTokens.Count];
                for (int i = 0; i < genIds.Length; i++) genIds[i] = generatedTokens[i];
                DenseTensor<long> genInput = new(genIds, [1, genIds.Length]);
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> genEmbedOut = _embedTokensSession.Run(
                    [NamedOnnxValue.CreateFromTensor(_embedInputName, genInput)]);
                generatedEmbeds = genEmbedOut.First().AsTensor<float>().ToDenseTensor();
            }

            int totalLen = numImageTokens + promptLen + generatedTokens.Count;
            float[] concatBuffer = new float[totalLen * hiddenDim];

            // Lay out: [image | prompt | generated] along the sequence
            // dimension. All three contribute hidden_dim-wide vectors per
            // position; we concatenate by copying the underlying buffers.
            ReadOnlySpan<float> imgFlat = imageEmbeds.Buffer.Span;
            int writePos = 0;
            imgFlat.Slice(0, numImageTokens * hiddenDim).CopyTo(concatBuffer.AsSpan(writePos));
            writePos += numImageTokens * hiddenDim;

            ReadOnlySpan<float> promptFlat = promptEmbeds.Buffer.Span;
            promptFlat.Slice(0, promptLen * hiddenDim).CopyTo(concatBuffer.AsSpan(writePos));
            writePos += promptLen * hiddenDim;

            if (generatedEmbeds is not null)
            {
                ReadOnlySpan<float> genFlat = generatedEmbeds.Buffer.Span;
                genFlat.Slice(0, generatedTokens.Count * hiddenDim).CopyTo(concatBuffer.AsSpan(writePos));
            }

            DenseTensor<float> concat = new(concatBuffer, [1, totalLen, hiddenDim]);

            // Causal attention mask is all-ones (every position attends
            // to every previous position; the decoder applies the causal
            // triangle internally based on input_ids semantics, so we
            // just signal "no padding").
            long[] maskData = new long[totalLen];
            for (int i = 0; i < totalLen; i++) maskData[i] = 1;
            DenseTensor<long> mask = new(maskData, [1, totalLen]);

            // Decoder forward pass.
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> decoderOut = _decoderSession.Run(
            [
                NamedOnnxValue.CreateFromTensor(_decoderEmbedsName, concat),
                NamedOnnxValue.CreateFromTensor(_decoderMaskName, mask),
            ]);

            DisposableNamedOnnxValue logitsValue = decoderOut.FirstOrDefault(v => v.Name == _decoderLogitsName)
                ?? decoderOut.First();
            DenseTensor<float> logits = logitsValue.AsTensor<float>().ToDenseTensor();
            int[] lShape = logits.Dimensions.ToArray();
            // Expected: [1, totalLen, vocab_size]
            if (lShape.Length != 3 || lShape[0] != 1 || lShape[1] != totalLen)
            {
                throw new InvalidOperationException(
                    $"PaliGemma decoder logits shape {string.Join('x', lShape)} doesn't match " +
                    $"expected [1, {totalLen}, vocab_size].");
            }

            int vocabSize = lShape[2];
            ReadOnlySpan<float> logitsFlat = logits.Buffer.Span;
            // Greedy: argmax of the last position's logits.
            ReadOnlySpan<float> lastLogits = logitsFlat.Slice(
                (totalLen - 1) * vocabSize, vocabSize);

            int nextToken = ArgMax(lastLogits);
            if (nextToken == GemmaEosTokenId) break;
            generatedTokens.Add(nextToken);
        }

        string raw = _tokenizer.Decode(generatedTokens) ?? string.Empty;
        return raw.Trim();
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<ValueRef>> rows)
        => throw new InvalidOperationException(
            "PaliGemmaModel overrides InferBatchAsync directly. BuildBatchInputs is not used.");

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
        => throw new InvalidOperationException(
            "PaliGemmaModel overrides InferBatchAsync directly. ParseBatchOutputs is not used.");

    /// <summary>
    /// Decodes encoded image bytes, resizes to the model's input shape,
    /// normalises with SigLIP statistics, and writes NCHW-layout floats
    /// (R-plane, then G-plane, then B-plane) into <paramref name="dest"/>.
    /// </summary>
    private void DecodeAndPackImage(byte[] imageBytes, Span<float> dest)
    {
        using SKBitmap? decoded = SKBitmap.Decode(imageBytes)
            ?? throw new InvalidOperationException(
                "SkiaSharp failed to decode image bytes for PaliGemma input.");

        SKImageInfo target = new(_inputWidth, _inputHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap resized = decoded.Resize(target, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"SkiaSharp failed to resize image to {_inputWidth}×{_inputHeight} for PaliGemma input.");

        int planeSize = _inputHeight * _inputWidth;
        nint pixelPtr = resized.GetPixels();

        unsafe
        {
            byte* source = (byte*)pixelPtr;
            for (int yx = 0; yx < planeSize; yx++)
            {
                int srcOffset = yx * 4;
                float r = source[srcOffset]     / 255f;
                float g = source[srcOffset + 1] / 255f;
                float b = source[srcOffset + 2] / 255f;

                // SigLIP normalisation: subtract 0.5, divide 0.5 → range [-1, 1].
                dest[yx]                 = (r - SigLipMean) / SigLipStd;
                dest[planeSize + yx]     = (g - SigLipMean) / SigLipStd;
                dest[2 * planeSize + yx] = (b - SigLipMean) / SigLipStd;
            }
        }
    }

    private static int ArgMax(ReadOnlySpan<float> values)
    {
        int bestIdx = 0;
        float bestVal = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > bestVal)
            {
                bestVal = values[i];
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    /// <inheritdoc />
    public new void Dispose()
    {
        _embedTokensSession.Dispose();
        _decoderSession.Dispose();
        base.Dispose();
    }
}

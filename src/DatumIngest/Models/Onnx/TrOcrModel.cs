using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// Microsoft TrOCR (Vision Transformer encoder + RoBERTa decoder) for
/// printed-text OCR. Accepts a single <see cref="DataKind.Image"/> per
/// row and returns a <see cref="DataKind.String"/> transcription.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two ONNX sessions.</strong> Like <see cref="ViTGpt2CaptionModel"/>,
/// TrOCR is encoder + decoder. The encoder is ViT-base 384×384 producing
/// <c>[batch, 577, 768]</c> hidden states; the decoder is a RoBERTa-style
/// 12-layer transformer with cross-attention to those hidden states.
/// </para>
/// <para>
/// <strong>Merged-decoder KV cache.</strong> Unlike vit-gpt2 (no cache,
/// runs full decoder per step), TrOCR uses the
/// <c>decoder_model_merged.onnx</c> export which carries a
/// <c>use_cache_branch</c> bool input that switches between prefill
/// (full input_ids, empty caches) and incremental (single new token,
/// past caches fed back). Each step grows decoder K/V by one position
/// and reuses encoder K/V unchanged after the first step.
/// </para>
/// <para>
/// <strong>fp16 support.</strong> The constructor auto-detects whether
/// the decoder ONNX is fp16 by reading the first KV cache input's
/// dtype. KV caches stay in fp32 in managed memory; the per-step
/// <see cref="OnnxTensorConversion.CreateAutoCastInput"/> casts to
/// fp16 at the session boundary, and outputs come back through
/// <see cref="OnnxTensorConversion.ToFloatTensor"/>. The double cast
/// per step is wasteful for fp16 (a follow-up could keep caches as
/// <see cref="Float16"/> arrays directly) but lets a single class
/// drive both precisions correctly.
/// </para>
/// <para>
/// <strong>Tokenizer.</strong> RoBERTa byte-level BPE — same
/// byte-to-unicode mapping as GPT-2, so
/// <see cref="ByteLevelBpeDecoder"/> reverses the <c>Ġ</c> /
/// <c>Ċ</c> mojibake that <c>BpeTokenizer.Decode</c> leaves behind.
/// Decoder start, end-of-sequence, pad, and bos token IDs come from
/// <c>generation_config.json</c>: 2, 2, 1, 0 respectively.
/// </para>
/// <para>
/// <strong>Folder layout.</strong> One directory holds both fp32 and
/// fp16 ONNX files plus the shared tokenizer + configs. The catalog
/// entry's <c>RelativePath</c> picks one encoder file as the anchor;
/// the constructor loads the matching merged decoder by name from
/// the same directory.
/// </para>
/// </remarks>
public sealed class TrOcrModel : OnnxModel
{
    private const int InputWidth = 384;
    private const int InputHeight = 384;
    private const int InputChannels = 3;

    private static readonly float[] MeanValues = [0.5f, 0.5f, 0.5f];
    private static readonly float[] StdValues = [0.5f, 0.5f, 0.5f];

    private readonly InferenceSession _decoderSession;
    private readonly BpeTokenizer _tokenizer;
    private readonly int _maxTokens;
    private readonly int _decoderStartTokenId;
    private readonly int _eosTokenId;

    private readonly string _encoderInputName;
    private readonly int _decoderLayers;
    private readonly int _decoderAttentionHeads;
    private readonly int _decoderHeadDim;

    /// <summary>Maximum tokens the decoder will generate per image.</summary>
    public int MaxTokens => _maxTokens;

    /// <summary>
    /// Per-image cost is encoder-pass + ~10–20 single-token decoder
    /// passes (printed-text TrOCR rarely runs to 20 tokens). Stream in
    /// groups of 8 so users see first transcriptions in seconds.
    /// </summary>
    public int? PreferredBatchSize => 8;

    /// <summary>
    /// Loads TrOCR from a directory of ONNX + tokenizer files.
    /// </summary>
    /// <param name="name">Catalog-visible name.</param>
    /// <param name="encoderModelFilePath">
    /// Absolute path to the encoder ONNX (<c>encoder_model.onnx</c> or
    /// <c>encoder_model_fp16.onnx</c>). Used to derive the model
    /// directory; the matching merged decoder + tokenizer files load
    /// from the same folder.
    /// </param>
    /// <param name="decoderFileName">
    /// File name of the merged decoder relative to the model directory.
    /// Defaults to <c>decoder_model_merged.onnx</c>; pass
    /// <c>decoder_model_merged_fp16.onnx</c> for the fp16 variant.
    /// </param>
    /// <param name="maxTokens">
    /// Cap on generated tokens per image. trocr-base-printed's
    /// generation_config.json sets max_length=20 — fine for single
    /// printed lines; raise for multi-line / handwritten variants.
    /// </param>
    /// <param name="decoderStartTokenId">
    /// First token fed to the decoder. Token 2 (<c>&lt;/s&gt;</c>) per
    /// trocr-base-printed's generation_config.
    /// </param>
    /// <param name="eosTokenId">
    /// End-of-sequence token. Same value as start (2) for this model.
    /// </param>
    public TrOcrModel(
        string name,
        string encoderModelFilePath,
        string decoderFileName = "decoder_model_merged.onnx",
        int maxTokens = 20,
        int decoderStartTokenId = 2,
        int eosTokenId = 2)
        : base(
            name,
            encoderModelFilePath,
            inputKinds: [DataKind.Image],
            outputKind: DataKind.String,
            isDeterministic: true)
    {
        _maxTokens = maxTokens;
        _decoderStartTokenId = decoderStartTokenId;
        _eosTokenId = eosTokenId;

        string modelDirectory = Path.GetDirectoryName(encoderModelFilePath)
            ?? throw new InvalidOperationException(
                $"Could not derive model directory from '{encoderModelFilePath}'.");

        string decoderPath = Path.Combine(modelDirectory, decoderFileName);
        if (!File.Exists(decoderPath))
        {
            throw new FileNotFoundException(
                $"TrOCR decoder file '{decoderFileName}' not found alongside the encoder. " +
                "Encoder + decoder ONNX files must live in the same directory.",
                decoderPath);
        }
        _decoderSession = OnnxSessionFactory.Create(decoderPath);

        string vocabPath = Path.Combine(modelDirectory, "vocab.json");
        string mergesPath = Path.Combine(modelDirectory, "merges.txt");
        if (!File.Exists(vocabPath) || !File.Exists(mergesPath))
        {
            throw new FileNotFoundException(
                "TrOCR tokenizer files (vocab.json + merges.txt) not found alongside the model. " +
                "These ship with the optimum-cli ONNX export — verify the export completed.",
                File.Exists(vocabPath) ? mergesPath : vocabPath);
        }
        using FileStream vocabStream = File.OpenRead(vocabPath);
        using FileStream mergesStream = File.OpenRead(mergesPath);
        _tokenizer = BpeTokenizer.Create(vocabStream, mergesStream);

        _encoderInputName = Session.InputMetadata.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException("Encoder ONNX has no declared input — file is malformed.");

        // Discover decoder layer count + attention shape from the KV cache
        // input metadata. The optimum-cli merged export uses the names
        // past_key_values.{i}.{decoder|encoder}.{key|value}.
        int layerCount = 0;
        while (_decoderSession.InputMetadata.ContainsKey($"past_key_values.{layerCount}.decoder.key"))
        {
            layerCount++;
        }
        if (layerCount == 0)
        {
            throw new InvalidOperationException(
                "Decoder ONNX does not expose 'past_key_values.0.decoder.key'. " +
                "Expected a merged TrOCR decoder export from optimum-cli.");
        }
        _decoderLayers = layerCount;

        NodeMetadata firstKvMeta = _decoderSession.InputMetadata["past_key_values.0.decoder.key"];
        // Shape: [batch, num_heads, past_seq_len, head_dim]. Symbolic batch
        // and past_seq_len; num_heads + head_dim are concrete static dims.
        int[] kvShape = firstKvMeta.Dimensions;
        if (kvShape.Length != 4 || kvShape[1] <= 0 || kvShape[3] <= 0)
        {
            throw new InvalidOperationException(
                $"past_key_values.0.decoder.key has unexpected dims [{string.Join(',', kvShape)}]; " +
                "expected [batch, num_heads, past_seq_len, head_dim] with static num_heads + head_dim.");
        }
        _decoderAttentionHeads = kvShape[1];
        _decoderHeadDim = kvShape[3];
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

        int batchSize = inputs.Count;
        int planeSize = InputHeight * InputWidth;
        int perImageFloats = InputChannels * planeSize;
        float[] tensorData = new float[batchSize * perImageFloats];

        for (int row = 0; row < batchSize; row++)
        {
            ValueRef image = inputs[row][0];
            if (image.IsNull)
            {
                throw new InvalidOperationException(
                    $"TrOcrModel received a null image at row {row}; filter nulls upstream.");
            }
            SKBitmap decoded = image.AsImage();
            ResizeAndPackImage(decoded, tensorData.AsSpan(row * perImageFloats, perImageFloats));
        }

        return await Task.Run<IReadOnlyList<ValueRef>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Encoder: one batched call, output [batch, 577, 768].
            DenseTensor<float> pixelValues = new(
                tensorData,
                [batchSize, InputChannels, InputHeight, InputWidth]);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoderOutputs = Session.Run(
                [OnnxTensorConversion.CreateAutoCastInput(Session, _encoderInputName, pixelValues)]);

            DisposableNamedOnnxValue encoderOutput = encoderOutputs.FirstOrDefault()
                ?? throw new InvalidOperationException("ViT encoder produced no output.");
            DenseTensor<float> encoderHidden = OnnxTensorConversion.ToFloatTensor(encoderOutput);
            int[] hiddenShape = encoderHidden.Dimensions.ToArray();
            if (hiddenShape.Length != 3 || hiddenShape[0] != batchSize)
            {
                throw new InvalidOperationException(
                    $"ViT encoder output shape {string.Join('x', hiddenShape)} doesn't match " +
                    $"expected [{batchSize}, *, *]. Model file may not be TrOCR compatible.");
            }
            int seqLen = hiddenShape[1];
            int hiddenDim = hiddenShape[2];

            // Per-row greedy decode with KV cache. Each row is independent;
            // we don't bother batching the decoder calls because step counts
            // diverge across rows (a row that hits EOS at step 4 shouldn't
            // wait on a row generating 20 tokens).
            ReadOnlySpan<float> hiddenFlat = encoderHidden.Buffer.Span;
            int hiddenPerRow = seqLen * hiddenDim;
            ValueRef[] results = new ValueRef[batchSize];

            for (int row = 0; row < batchSize; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                float[] rowHidden = new float[hiddenPerRow];
                hiddenFlat.Slice(row * hiddenPerRow, hiddenPerRow).CopyTo(rowHidden);

                string text = GenerateGreedy(rowHidden, seqLen, hiddenDim, cancellationToken);
                results[row] = ValueRef.FromString(text);
            }

            return results;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<ValueRef>> rows)
        => throw new InvalidOperationException(
            "TrOcrModel overrides InferBatchAsync directly. BuildBatchInputs is not used.");

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
        => throw new InvalidOperationException(
            "TrOcrModel overrides InferBatchAsync directly. ParseBatchOutputs is not used.");

    private static void ResizeAndPackImage(SKBitmap decoded, Span<float> dest)
    {
        SKImageInfo targetInfo = new(InputWidth, InputHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap resized = decoded.Resize(targetInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"SkiaSharp failed to resize image to {InputWidth}×{InputHeight} for TrOCR input.");

        int planeSize = InputHeight * InputWidth;
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

                dest[yx]                 = (r - MeanValues[0]) / StdValues[0];
                dest[planeSize + yx]     = (g - MeanValues[1]) / StdValues[1];
                dest[2 * planeSize + yx] = (b - MeanValues[2]) / StdValues[2];
            }
        }
    }

    private string GenerateGreedy(
        float[] encoderHiddenStates,
        int seqLen,
        int hiddenDim,
        CancellationToken cancellationToken)
    {
        DenseTensor<float> hiddenTensor = new(encoderHiddenStates, [1, seqLen, hiddenDim]);

        // Per-layer KV caches as fp32 buffers. CreateAutoCastInput casts to
        // fp16 at the session boundary when the decoder is fp16; outputs come
        // back through ToFloatTensor. 4 buffers per layer:
        // decoder.key, decoder.value, encoder.key, encoder.value.
        float[][] decoderKey = new float[_decoderLayers][];
        float[][] decoderValue = new float[_decoderLayers][];
        float[][] encoderKey = new float[_decoderLayers][];
        float[][] encoderValue = new float[_decoderLayers][];
        for (int layer = 0; layer < _decoderLayers; layer++)
        {
            decoderKey[layer] = [];
            decoderValue[layer] = [];
            encoderKey[layer] = [];
            encoderValue[layer] = [];
        }

        List<int> tokens = new(_maxTokens + 1) { _decoderStartTokenId };
        bool useCacheBranch = false;
        int perPositionFloats = _decoderAttentionHeads * _decoderHeadDim;

        for (int step = 0; step <= _maxTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Prefill: full input_ids (just the start token here, length 1).
            // Incremental: single newest token only.
            long[] inputIdsBuffer = useCacheBranch
                ? [tokens[^1]]
                : tokens.Select(t => (long)t).ToArray();
            int decInputLen = inputIdsBuffer.Length;
            DenseTensor<long> inputIds = new(inputIdsBuffer, [1, decInputLen]);

            int pastDecLen = useCacheBranch ? decoderKey[0].Length / perPositionFloats : 0;
            int pastEncLen = useCacheBranch ? encoderKey[0].Length / perPositionFloats : 0;
            int[] decoderShape = [1, _decoderAttentionHeads, pastDecLen, _decoderHeadDim];
            int[] encoderShape = [1, _decoderAttentionHeads, pastEncLen, _decoderHeadDim];

            List<NamedOnnxValue> decoderInputs = new(3 + 4 * _decoderLayers)
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                OnnxTensorConversion.CreateAutoCastInput(_decoderSession, "encoder_hidden_states", hiddenTensor),
                NamedOnnxValue.CreateFromTensor(
                    "use_cache_branch",
                    new DenseTensor<bool>(new[] { useCacheBranch }, [1])),
            };
            for (int layer = 0; layer < _decoderLayers; layer++)
            {
                decoderInputs.Add(OnnxTensorConversion.CreateAutoCastInput(
                    _decoderSession, $"past_key_values.{layer}.decoder.key",
                    new DenseTensor<float>(decoderKey[layer], decoderShape)));
                decoderInputs.Add(OnnxTensorConversion.CreateAutoCastInput(
                    _decoderSession, $"past_key_values.{layer}.decoder.value",
                    new DenseTensor<float>(decoderValue[layer], decoderShape)));
                decoderInputs.Add(OnnxTensorConversion.CreateAutoCastInput(
                    _decoderSession, $"past_key_values.{layer}.encoder.key",
                    new DenseTensor<float>(encoderKey[layer], encoderShape)));
                decoderInputs.Add(OnnxTensorConversion.CreateAutoCastInput(
                    _decoderSession, $"past_key_values.{layer}.encoder.value",
                    new DenseTensor<float>(encoderValue[layer], encoderShape)));
            }

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _decoderSession.Run(decoderInputs);

            DisposableNamedOnnxValue logitsValue = outputs.FirstOrDefault(v => v.Name == "logits")
                ?? throw new InvalidOperationException("Decoder produced no 'logits' output.");
            DenseTensor<float> logits = OnnxTensorConversion.ToFloatTensor(logitsValue);
            int[] logitsShape = logits.Dimensions.ToArray();
            if (logitsShape.Length != 3 || logitsShape[0] != 1 || logitsShape[1] != decInputLen)
            {
                throw new InvalidOperationException(
                    $"Decoder logits shape {string.Join('x', logitsShape)} doesn't match " +
                    $"expected [1, {decInputLen}, vocab].");
            }
            int vocabSize = logitsShape[2];
            ReadOnlySpan<float> logitsFlat = logits.Buffer.Span;
            ReadOnlySpan<float> lastLogits = logitsFlat.Slice((decInputLen - 1) * vocabSize, vocabSize);

            int nextToken = ArgMax(lastLogits);
            if (nextToken == _eosTokenId) break;
            tokens.Add(nextToken);

            // Stop if we've already produced max_tokens — no need to refresh
            // caches we'll never use.
            if (step == _maxTokens) break;

            // Read present.* outputs into fp32 cache buffers for the next
            // step. The output collection is disposed at the end of this
            // loop iteration, so we must copy.
            for (int layer = 0; layer < _decoderLayers; layer++)
            {
                decoderKey[layer]   = ReadCacheFloats(outputs, $"present.{layer}.decoder.key");
                decoderValue[layer] = ReadCacheFloats(outputs, $"present.{layer}.decoder.value");
                encoderKey[layer]   = ReadCacheFloats(outputs, $"present.{layer}.encoder.key");
                encoderValue[layer] = ReadCacheFloats(outputs, $"present.{layer}.encoder.value");
            }
            useCacheBranch = true;
        }

        // Skip the start token; ByteLevelBpeDecoder reverses the byte-to-unicode
        // mapping that BpeTokenizer.Decode leaves in place (Ġ → space, etc.).
        IEnumerable<int> outputTokens = tokens.Skip(1);
        string raw = _tokenizer.Decode(outputTokens) ?? string.Empty;
        return ByteLevelBpeDecoder.Decode(raw).Trim();
    }

    private static float[] ReadCacheFloats(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        string name)
    {
        DisposableNamedOnnxValue value = outputs.FirstOrDefault(v => v.Name == name)
            ?? throw new InvalidOperationException($"Decoder produced no '{name}' output.");
        DenseTensor<float> t = OnnxTensorConversion.ToFloatTensor(value);
        float[] copy = new float[t.Length];
        t.Buffer.Span.CopyTo(copy);
        return copy;
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
        _decoderSession.Dispose();
        base.Dispose();
    }
}

using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// Moondream2 — small vision-language model: SigLIP-style 378×378 vision
/// encoder feeding a Phi-1.5/2 decoder via spliced image embeddings.
/// SQL surface is <c>models.moondream2(image, prompt) → string</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Architecture (3 ONNX sessions).</strong>
/// <code>
/// image  →  vision_encoder    →  image_features [1, 729, 2048] ─┐
///                                                               ├→ inputs_embeds → decoder → logits
/// prompt →  embed_tokens      →  text_embeds  [1, T, 2048]  ────┘                    ↑
///                                                                          (autoregressive, KV-cached)
/// </code>
/// Vision encoder bakes the SigLIP→Phi projection into its graph and
/// emits 729 image tokens (27×27 patches at 378/14 stride) in Phi's
/// 2048-dim hidden space, ready to splice ahead of the prompt
/// embeddings.
/// </para>
/// <para>
/// <strong>Prompt template.</strong> Moondream's training format is
/// <c>&lt;image&gt;\n\nQuestion: {prompt}\n\nAnswer:</c>. The
/// <c>&lt;image&gt;</c> placeholder is just documentation for the
/// embedding-splice point; we tokenize the surrounding text only and
/// build <c>inputs_embeds = concat(image_features, text_embeds)</c>.
/// </para>
/// <para>
/// <strong>Merged decoder, KV cache.</strong> The export ships a single
/// <c>decoder_model_merged_fp16.onnx</c> — no <c>use_cache_branch</c>
/// switch needed; pass empty past_key_values for prefill and grow them
/// from the <c>present.*</c> outputs each step. 24 layers × {key, value},
/// shape <c>[1, 32, past_seq, 64]</c>. Greedy decode (argmax) terminates
/// on Phi's EOS token <c>50256</c> (<c>&lt;|endoftext|&gt;</c>) or when
/// <c>maxTokens</c> is hit.
/// </para>
/// <para>
/// <strong>Tokenizer.</strong> Phi-2 uses GPT-2-style byte-level BPE.
/// <c>BpeTokenizer.Decode</c> leaves the byte-to-unicode mapping in place
/// (<c>Ġ</c> for space, <c>Ċ</c> for newline);
/// <see cref="ByteLevelBpeDecoder"/> reverses it to ordinary text.
/// </para>
/// </remarks>
public sealed class Moondream2Model : OnnxModel
{
    private const int InputWidth = 378;
    private const int InputHeight = 378;
    private const int InputChannels = 3;
    // SigLIP normalisation: (raw/255 - 0.5) / 0.5 = raw * (2/255) - 1.
    private const float ScaleConstant = 2f / 255f;
    private const float BiasConstant = -1f;

    // Phi-2 special tokens. EOS = BOS = <|endoftext|> in the export's
    // generation_config.json.
    private const int EosTokenId = 50256;

    private readonly InferenceSession _embedTokensSession;
    private readonly InferenceSession _decoderSession;
    private readonly BpeTokenizer _tokenizer;
    private readonly int _maxTokens;
    private readonly int _decoderLayers;
    private readonly int _decoderAttentionHeads;
    private readonly int _decoderHeadDim;

    // IO Binding memory locations. CUDA = GPU device memory (KV caches stay
    // resident across the autoregressive loop instead of round-tripping
    // through managed memory each step); CPU = system memory (logits land
    // here so argmax sampling reads directly without a separate copy-back).
    // Constructed once and reused across every step. Without this, naive
    // ORT inference moves ~280 MB per cache × 48 caches across PCIe per
    // generated token — bandwidth-bound at ~3-4s/token even on fast GPUs.
    private readonly OrtMemoryInfo _cudaMemInfo = new("Cuda", OrtAllocatorType.DeviceAllocator, 0, OrtMemType.Default);
    private readonly OrtMemoryInfo _cpuMemInfo = OrtMemoryInfo.DefaultInstance;

    // I/O names captured once from session metadata.
    private readonly string _visionInputName;
    private readonly string _visionOutputName;
    private readonly string _embedInputName;
    private readonly string _embedOutputName;

    /// <summary>Maximum tokens generated per prompt.</summary>
    public int MaxTokens => _maxTokens;

    /// <summary>
    /// Per-row dispatch — the decoder loop generates a divergent number of
    /// tokens per row, so cross-row batching at the decoder is wasteful.
    /// Vision encoder could batch but typical caption volumes don't
    /// justify the complexity. Stream small groups so users see first
    /// outputs in seconds.
    /// </summary>
    public int? PreferredBatchSize => 1;

    /// <summary>
    /// Loads Moondream2 from a directory containing the three ONNX files
    /// plus the Phi-2 tokenizer (vocab.json + merges.txt).
    /// </summary>
    /// <param name="name">Catalog-visible name.</param>
    /// <param name="visionEncoderModelFilePath">
    /// Absolute path to <c>vision_encoder_fp16.onnx</c> (or the fp32
    /// variant). Used as the directory anchor; embed_tokens + decoder +
    /// tokenizer load from the same folder.
    /// </param>
    /// <param name="embedTokensFileName">
    /// File name of the embed_tokens ONNX relative to the model directory.
    /// Defaults to the fp16 variant; pair the precision with the vision
    /// encoder for consistent dtype handling.
    /// </param>
    /// <param name="decoderFileName">
    /// File name of the merged decoder ONNX. Defaults to the fp16 variant.
    /// </param>
    /// <param name="maxTokens">
    /// Cap on generated tokens per prompt. 256 is generous for descriptions
    /// and short Q&amp;A; raise for long-form summarisation.
    /// </param>
    public Moondream2Model(
        string name,
        string visionEncoderModelFilePath,
        string embedTokensFileName = "embed_tokens_fp16.onnx",
        string decoderFileName = "decoder_model_merged_fp16.onnx",
        int maxTokens = 256)
        : base(
            name,
            visionEncoderModelFilePath,
            inputKinds: [DataKind.Image, DataKind.String],
            outputKind: DataKind.String,
            isDeterministic: true)
    {
        _maxTokens = maxTokens;

        string onnxDirectory = Path.GetDirectoryName(visionEncoderModelFilePath)
            ?? throw new InvalidOperationException(
                $"Could not derive ONNX directory from '{visionEncoderModelFilePath}'.");

        string embedPath = Path.Combine(onnxDirectory, embedTokensFileName);
        if (!File.Exists(embedPath))
        {
            throw new FileNotFoundException(
                $"Moondream2 '{embedTokensFileName}' not found alongside the vision encoder. " +
                "Expected layout: vision_encoder*.onnx + embed_tokens*.onnx + " +
                "decoder_model_merged*.onnx in the same onnx/ folder.",
                embedPath);
        }
        _embedTokensSession = OnnxSessionFactory.Create(embedPath);

        string decoderPath = Path.Combine(onnxDirectory, decoderFileName);
        if (!File.Exists(decoderPath))
        {
            throw new FileNotFoundException(
                $"Moondream2 '{decoderFileName}' not found alongside the vision encoder.",
                decoderPath);
        }
        _decoderSession = OnnxSessionFactory.Create(decoderPath);

        // Tokenizer files live one level up from the onnx/ subfolder in the
        // standard Xenova/onnx-community export layout.
        string tokenizerDirectory = Path.GetDirectoryName(onnxDirectory) ?? onnxDirectory;
        string vocabPath = Path.Combine(tokenizerDirectory, "vocab.json");
        string mergesPath = Path.Combine(tokenizerDirectory, "merges.txt");
        if (!File.Exists(vocabPath) || !File.Exists(mergesPath))
        {
            throw new FileNotFoundException(
                "Moondream2 tokenizer files (vocab.json + merges.txt) not found in the model root. " +
                "Expected layout: moondream2-onnx/{vocab.json,merges.txt} alongside the onnx/ folder.",
                File.Exists(vocabPath) ? mergesPath : vocabPath);
        }
        using FileStream vocabStream = File.OpenRead(vocabPath);
        using FileStream mergesStream = File.OpenRead(mergesPath);
        _tokenizer = BpeTokenizer.Create(vocabStream, mergesStream);

        _visionInputName = Session.InputMetadata.Keys.First();
        _visionOutputName = Session.OutputMetadata.Keys.First();
        _embedInputName = _embedTokensSession.InputMetadata.Keys.First();
        _embedOutputName = _embedTokensSession.OutputMetadata.Keys.First();

        // Discover decoder layer count and KV head shape from metadata.
        int layerCount = 0;
        while (_decoderSession.InputMetadata.ContainsKey($"past_key_values.{layerCount}.key"))
        {
            layerCount++;
        }
        if (layerCount == 0)
        {
            throw new InvalidOperationException(
                "Moondream2 decoder ONNX does not expose 'past_key_values.0.key'. " +
                "Expected the merged Phi-decoder export from optimum-cli / transformers.js.");
        }
        _decoderLayers = layerCount;

        int[] kvShape = _decoderSession.InputMetadata["past_key_values.0.key"].Dimensions;
        if (kvShape.Length != 4 || kvShape[1] <= 0 || kvShape[3] <= 0)
        {
            throw new InvalidOperationException(
                $"past_key_values.0.key has unexpected dims [{string.Join(',', kvShape)}]; " +
                "expected [batch, num_heads, past_seq_len, head_dim] with static heads + head_dim.");
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

        return await Task.Run<IReadOnlyList<ValueRef>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValueRef[] results = new ValueRef[inputs.Count];

            for (int row = 0; row < inputs.Count; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<ValueRef> rowInputs = inputs[row];
                if (rowInputs.Count < 2)
                {
                    throw new InvalidOperationException(
                        $"Moondream2Model expects 2 inputs (image, prompt); row {row} has {rowInputs.Count}.");
                }

                ValueRef image = rowInputs[0];
                ValueRef prompt = rowInputs[1];
                if (image.IsNull)
                {
                    throw new InvalidOperationException(
                        $"Moondream2Model received a null image at row {row}; filter nulls upstream.");
                }
                string promptText = prompt.IsNull ? "Describe this image." : prompt.AsString();

                SKBitmap decoded = image.AsImage();
                results[row] = ValueRef.FromString(GenerateOne(decoded, promptText, cancellationToken));
            }
            return results;
        }, cancellationToken).ConfigureAwait(false);
    }

    private string GenerateOne(SKBitmap decoded, string prompt, CancellationToken cancellationToken)
    {
        // Step 1: vision encoder → image_features [1, 729, 2048].
        DenseTensor<float> imageFeatures = RunVisionEncoder(decoded);
        int numImageTokens = imageFeatures.Dimensions[1];
        int hiddenDim = imageFeatures.Dimensions[2];

        // Step 2: tokenize the text portion of the prompt template.
        // Moondream's training format is "<image>\n\nQuestion: P\n\nAnswer:"
        // — the <image> marker is the splice point we already covered with
        // image_features, so we tokenize only the trailing text.
        string textPart = $"\n\nQuestion: {prompt}\n\nAnswer:";
        IReadOnlyList<int> textTokenIds = _tokenizer.EncodeToIds(textPart);
        long[] textIds = new long[textTokenIds.Count];
        for (int i = 0; i < textIds.Length; i++) textIds[i] = textTokenIds[i];

        DenseTensor<float> textEmbeds = RunEmbedTokens(textIds);
        int textLen = textEmbeds.Dimensions[1];

        // Step 3: build prefill inputs_embeds = [image_features | text_embeds].
        int prefillLen = numImageTokens + textLen;
        float[] prefillData = new float[prefillLen * hiddenDim];
        ReadOnlySpan<float> imgFlat = imageFeatures.Buffer.Span;
        imgFlat.Slice(0, numImageTokens * hiddenDim).CopyTo(prefillData);
        ReadOnlySpan<float> txtFlat = textEmbeds.Buffer.Span;
        txtFlat.Slice(0, textLen * hiddenDim).CopyTo(prefillData.AsSpan(numImageTokens * hiddenDim));

        return GenerateGreedy(prefillData, prefillLen, hiddenDim, cancellationToken);
    }

    /// <summary>
    /// Greedy autoregressive generation with ORT IO Binding. KV cache
    /// outputs of step N are bound directly as inputs to step N+1 — the
    /// tensors stay GPU-resident across the entire loop, eliminating the
    /// per-step PCIe round-trip that dominates naive inference. Logits
    /// are bound to CPU memory so argmax sampling reads directly without
    /// a separate copy-back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Lifecycle.</strong> Output OrtValues from a binding are
    /// owned by that binding; disposing the binding invalidates them.
    /// We keep <c>prevBinding</c> alive until the next step has bound
    /// its new outputs into <c>kvCaches</c>, then dispose the previous
    /// binding (whose outputs are no longer referenced). Same pattern as
    /// <see cref="MusicGenModel"/>.
    /// </para>
    /// <para>
    /// <strong>Empty initial KV cache.</strong> The merged decoder
    /// requires past_key_values inputs on every step including prefill;
    /// we satisfy this by binding zero-length tensors with shape
    /// <c>[1, num_heads, 0, head_dim]</c>. Their backing arrays are
    /// kept alive by <c>initialEmptyData</c> until step 0's outputs
    /// replace them in the cache map.
    /// </para>
    /// </remarks>
    private string GenerateGreedy(
        float[] prefillEmbeds,
        int prefillLen,
        int hiddenDim,
        CancellationToken cancellationToken)
    {
        using RunOptions runOpts = new();

        Dictionary<string, OrtValue> kvCaches = new(StringComparer.Ordinal);

        // Empty initial CPU OrtValues for the prefill step's past_key_values
        // inputs. Disposed after step 0's outputs replace them.
        OrtValue[] initialPastKeys = new OrtValue[_decoderLayers];
        OrtValue[] initialPastValues = new OrtValue[_decoderLayers];
        long[] emptyKvShape = [1, _decoderAttentionHeads, 0, _decoderHeadDim];
        float[] initialEmptyData = [];
        for (int layer = 0; layer < _decoderLayers; layer++)
        {
            initialPastKeys[layer] = OrtValue.CreateTensorValueFromMemory(
                _cpuMemInfo, initialEmptyData.AsMemory(), emptyKvShape);
            initialPastValues[layer] = OrtValue.CreateTensorValueFromMemory(
                _cpuMemInfo, initialEmptyData.AsMemory(), emptyKvShape);
            kvCaches[$"past_key_values.{layer}.key"] = initialPastKeys[layer];
            kvCaches[$"past_key_values.{layer}.value"] = initialPastValues[layer];
        }
        bool initialDisposed = false;

        OrtIoBinding? prevBinding = null;
        List<int> generated = new(_maxTokens + 1);
        int currentSeqLen = 0;

        try
        {
            for (int step = 0; step <= _maxTokens; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Per-step inputs_embeds. Step 0 = prefill (image_features
                // concatenated with prompt embeds). Subsequent steps = single
                // new token's embedding from embed_tokens.
                Memory<float> embedsMem;
                long[] embedsShape;
                int stepLen;
                DenseTensor<float>? stepEmbedsTensor = null;

                if (step == 0)
                {
                    embedsMem = prefillEmbeds.AsMemory();
                    embedsShape = [1, prefillLen, hiddenDim];
                    stepLen = prefillLen;
                }
                else
                {
                    long[] singleId = [generated[^1]];
                    stepEmbedsTensor = RunEmbedTokens(singleId);
                    embedsMem = stepEmbedsTensor.Buffer;
                    embedsShape = [1, 1, hiddenDim];
                    stepLen = 1;
                }

                long[] positionIds = new long[stepLen];
                for (int i = 0; i < stepLen; i++) positionIds[i] = currentSeqLen + i;
                long[] posShape = [1, stepLen];

                int totalLen = currentSeqLen + stepLen;
                long[] attentionMask = new long[totalLen];
                for (int i = 0; i < totalLen; i++) attentionMask[i] = 1;
                long[] maskShape = [1, totalLen];

                OrtIoBinding stepBinding = _decoderSession.CreateIoBinding();
                bool ownStepBinding = true;

                try
                {
                    using OrtValue inputsEmbedsOv = OrtValue.CreateTensorValueFromMemory(
                        _cpuMemInfo, embedsMem, embedsShape);
                    using OrtValue positionIdsOv = OrtValue.CreateTensorValueFromMemory(
                        _cpuMemInfo, positionIds.AsMemory(), posShape);
                    using OrtValue attentionMaskOv = OrtValue.CreateTensorValueFromMemory(
                        _cpuMemInfo, attentionMask.AsMemory(), maskShape);

                    stepBinding.BindInput("inputs_embeds", inputsEmbedsOv);
                    stepBinding.BindInput("attention_mask", attentionMaskOv);
                    stepBinding.BindInput("position_ids", positionIdsOv);
                    for (int layer = 0; layer < _decoderLayers; layer++)
                    {
                        stepBinding.BindInput(
                            $"past_key_values.{layer}.key",
                            kvCaches[$"past_key_values.{layer}.key"]);
                        stepBinding.BindInput(
                            $"past_key_values.{layer}.value",
                            kvCaches[$"past_key_values.{layer}.value"]);
                    }

                    // Logits → CPU (we sample); KV cache → GPU (next step
                    // binds them as input without round-trip).
                    stepBinding.BindOutputToDevice("logits", _cpuMemInfo);
                    for (int layer = 0; layer < _decoderLayers; layer++)
                    {
                        stepBinding.BindOutputToDevice($"present.{layer}.key", _cudaMemInfo);
                        stepBinding.BindOutputToDevice($"present.{layer}.value", _cudaMemInfo);
                    }

                    _decoderSession.RunWithBinding(runOpts, stepBinding);
                    stepBinding.SynchronizeBoundOutputs();

                    // Don't dispose the collection wrapper — we keep references
                    // to the contained OrtValues in kvCaches and they're
                    // owned by stepBinding.
                    IDisposableReadOnlyCollection<OrtValue> outputs = stepBinding.GetOutputValues();
                    IReadOnlyList<string> outputNames = stepBinding.GetOutputNames();

                    int logitsIdx = -1;
                    for (int i = 0; i < outputNames.Count; i++)
                    {
                        if (outputNames[i] == "logits") { logitsIdx = i; break; }
                    }
                    if (logitsIdx < 0)
                    {
                        throw new InvalidOperationException(
                            "Moondream2 decoder produced no 'logits' output.");
                    }

                    OrtValue logitsOv = outputs[logitsIdx];
                    ReadOnlySpan<long> logitsShape = logitsOv.GetTensorTypeAndShape().Shape;
                    if (logitsShape.Length != 3 || logitsShape[0] != 1 || logitsShape[1] != stepLen)
                    {
                        throw new InvalidOperationException(
                            $"Moondream2 decoder logits shape {string.Join('x', logitsShape.ToArray())} " +
                            $"doesn't match expected [1, {stepLen}, vocab].");
                    }
                    int vocabSize = (int)logitsShape[2];
                    ReadOnlySpan<float> logitsFlat = logitsOv.GetTensorDataAsSpan<float>();
                    ReadOnlySpan<float> lastLogits = logitsFlat.Slice(
                        (stepLen - 1) * vocabSize, vocabSize);

                    int nextToken = ArgMax(lastLogits);
                    if (nextToken == EosTokenId) break;
                    generated.Add(nextToken);
                    currentSeqLen += stepLen;

                    if (step == _maxTokens) break;

                    // Move new GPU OrtValues into the cache map.
                    for (int i = 0; i < outputNames.Count; i++)
                    {
                        string n = outputNames[i];
                        if (!n.StartsWith("present.", StringComparison.Ordinal)) continue;
                        string pastName = "past_key_values." + n["present.".Length..];
                        kvCaches[pastName] = outputs[i];
                    }

                    // Release the previous step's binding (its outputs are
                    // no longer referenced by kvCaches).
                    prevBinding?.Dispose();
                    prevBinding = stepBinding;
                    ownStepBinding = false;

                    // Once step 0 finished, the initial empty CPU OrtValues
                    // are no longer in the cache — dispose them.
                    if (step == 0 && !initialDisposed)
                    {
                        for (int layer = 0; layer < _decoderLayers; layer++)
                        {
                            initialPastKeys[layer].Dispose();
                            initialPastValues[layer].Dispose();
                        }
                        initialDisposed = true;
                    }

                    _ = stepEmbedsTensor; // keep alive until end of step
                }
                finally
                {
                    if (ownStepBinding) stepBinding.Dispose();
                }
            }
        }
        finally
        {
            prevBinding?.Dispose();
            if (!initialDisposed)
            {
                for (int layer = 0; layer < _decoderLayers; layer++)
                {
                    initialPastKeys[layer].Dispose();
                    initialPastValues[layer].Dispose();
                }
            }
        }

        string raw = _tokenizer.Decode(generated) ?? string.Empty;
        return ByteLevelBpeDecoder.Decode(raw).Trim();
    }

    private DenseTensor<float> RunVisionEncoder(SKBitmap decoded)
    {
        float[] pixelData = new float[InputChannels * InputHeight * InputWidth];
        ImageTensorPrep.StretchAndPackNchw(
            decoded, pixelData, InputWidth, InputHeight, ScaleConstant, BiasConstant);

        DenseTensor<float> pixelValues = new(
            pixelData, [1, InputChannels, InputHeight, InputWidth]);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> visionOut = Session.Run(
            [OnnxTensorConversion.CreateAutoCastInput(Session, _visionInputName, pixelValues)]);
        DisposableNamedOnnxValue v = visionOut.FirstOrDefault(o => o.Name == _visionOutputName)
            ?? visionOut.First();
        DenseTensor<float> features = OnnxTensorConversion.ToFloatTensor(v);
        int[] shape = features.Dimensions.ToArray();
        if (shape.Length != 3 || shape[0] != 1)
        {
            throw new InvalidOperationException(
                $"Moondream2 vision encoder output shape {string.Join('x', shape)} doesn't match " +
                "expected [1, num_image_tokens, hidden_dim].");
        }
        return features;
    }

    private DenseTensor<float> RunEmbedTokens(long[] inputIds)
    {
        DenseTensor<long> idsTensor = new(inputIds, [1, inputIds.Length]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> embedOut = _embedTokensSession.Run(
            [NamedOnnxValue.CreateFromTensor(_embedInputName, idsTensor)]);
        DisposableNamedOnnxValue v = embedOut.FirstOrDefault(o => o.Name == _embedOutputName)
            ?? embedOut.First();
        return OnnxTensorConversion.ToFloatTensor(v);
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<ValueRef>> rows)
        => throw new InvalidOperationException(
            "Moondream2Model overrides InferBatchAsync directly. BuildBatchInputs is not used.");

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
        => throw new InvalidOperationException(
            "Moondream2Model overrides InferBatchAsync directly. ParseBatchOutputs is not used.");

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

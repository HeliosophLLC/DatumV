using System.Text;
using System.Text.Json;
using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// Microsoft Florence-2 — prompt-driven vision-language model. One model
/// handles caption (short / detailed / paragraph), object detection, OCR,
/// dense region captioning, and segmentation, all selected at runtime by
/// a task-prompt token. This wrapper currently exposes the captioning
/// subset; detection and OCR add later as parsers for the bounded-text
/// output format.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Architecture.</strong> Four ONNX sessions form an
/// encoder-decoder pipeline with a vision branch:
/// <code>
/// image  →  vision_encoder   →  visual features
///                                       ↓
/// prompt →  embed_tokens    →  text embeddings  →  encoder  →  encoded
///                                                                   ↓
///                                       decoder_input_ids   →  decoder
///                                                                   ↓
///                                                            next-token logits
/// </code>
/// The vision encoder is DaViT (Dual Attention Vision Transformer); the
/// text side is a BART-style encoder-decoder. Concatenation of visual +
/// text embeddings happens at the encoder input — we build the combined
/// sequence in C# and pass it as the encoder's <c>inputs_embeds</c>.
/// </para>
/// <para>
/// <strong>Task prompt.</strong> Selects what the model does. The
/// vocabulary includes special tokens for each task (<c>&lt;CAPTION&gt;</c>,
/// <c>&lt;DETAILED_CAPTION&gt;</c>, <c>&lt;OD&gt;</c>, etc.); the prompt is
/// just that token wrapped with <c>&lt;s&gt;...&lt;/s&gt;</c> BOS/EOS.
/// </para>
/// <para>
/// <strong>Quantization auto-detection.</strong> The catalog points at
/// the encoder file (e.g. <c>vision_encoder_fp16.onnx</c>); the constructor
/// strips the <c>vision_encoder</c> prefix and <c>.onnx</c> suffix to extract
/// a "_fp16" or "_quantized" component-suffix and uses it to locate the
/// other three ONNX files. Lets fp16 and quantized variants live in
/// separate folders without renaming.
/// </para>
/// </remarks>
public sealed class Florence2Model : OnnxModel
{
    // DaViT input convention: 768×768, RGB, ImageNet normalization
    // (mean=[0.485,0.456,0.406], std=[0.229,0.224,0.225]).
    private const int InputWidth = 768;
    private const int InputHeight = 768;
    private const int InputChannels = 3;

    // BART special tokens used by Florence-2.
    private const int BosTokenId = 0;       // <s>
    private const int EosTokenId = 2;       // </s>
    private const int PadTokenId = 1;       // <pad>

    private readonly InferenceSession _embedTokensSession;
    private readonly InferenceSession _encoderSession;
    private readonly InferenceSession _decoderSession;
    private readonly BpeTokenizer _tokenizer;
    private readonly int _maxTokens;
    private readonly int[] _taskPromptTokenIds;
    private readonly string _taskPromptDescription;

    private readonly string _visionInputName;
    private readonly string _visionOutputName;
    private readonly string _embedInputName;
    private readonly string _embedOutputName;
    private readonly string _encoderInputName;
    private readonly string _encoderAttentionMaskName;
    private readonly string _encoderOutputName;
    private readonly string _decoderInputIdsName;
    private readonly string _decoderEncoderHiddenStatesName;
    private readonly string _decoderEncoderAttentionMaskName;
    private readonly string _decoderLogitsName;

    /// <summary>Maximum tokens the decoder will generate per image.</summary>
    public int MaxTokens => _maxTokens;

    /// <summary>The task prompt this instance is configured for (for diagnostics).</summary>
    public string TaskPromptDescription => _taskPromptDescription;

    /// <inheritdoc />
    /// <remarks>
    /// Florence-2 captioning is ~1-2s per image: vision encoder pass +
    /// embed + encoder + autoregressive decode. Stream in groups of 8
    /// so the user sees first captions quickly. <c>MORE_DETAILED_CAPTION</c>
    /// is closer to 2s per image (long output sequences); even at that
    /// cost a group-of-8 first batch arrives in ~15s, much better than
    /// waiting for 1024 rows.
    /// </remarks>
    public int? PreferredBatchSize => 8;

    /// <summary>
    /// Loads the Florence-2 pipeline from a directory containing the four
    /// ONNX files plus tokenizer + configs.
    /// </summary>
    /// <param name="name">Catalog-visible name.</param>
    /// <param name="visionEncoderPath">
    /// Absolute path to the vision encoder ONNX file. The constructor
    /// derives the model directory from this path; the encoder filename
    /// also drives quantization auto-detection (suffix between
    /// <c>vision_encoder</c> and <c>.onnx</c>).
    /// </param>
    /// <param name="taskPrompt">
    /// The Florence-2 task token to drive generation:
    /// <c>"&lt;CAPTION&gt;"</c>, <c>"&lt;DETAILED_CAPTION&gt;"</c>,
    /// <c>"&lt;MORE_DETAILED_CAPTION&gt;"</c>, etc. Available in the
    /// model's special-tokens vocabulary.
    /// </param>
    /// <param name="maxTokens">Cap on generated tokens. Defaults to 200 for verbose captions.</param>
    public Florence2Model(
        string name,
        string visionEncoderPath,
        string taskPrompt = "<CAPTION>",
        int maxTokens = 200)
        : base(
            name,
            visionEncoderPath,
            inputKinds: [DataKind.Image],
            outputKind: DataKind.String,
            isDeterministic: true)
    {
        _maxTokens = maxTokens;
        _taskPromptDescription = taskPrompt;

        string modelDirectory = Path.GetDirectoryName(visionEncoderPath)
            ?? throw new InvalidOperationException(
                $"Could not derive model directory from '{visionEncoderPath}'.");

        // Auto-detect quantization suffix. vision_encoder_fp16.onnx → "_fp16".
        // vision_encoder.onnx → "" (no suffix; raw fp32 build).
        string encoderName = Path.GetFileName(visionEncoderPath);
        string suffix = ExtractComponentSuffix(encoderName);

        _embedTokensSession = LoadOrThrow(modelDirectory, $"embed_tokens{suffix}.onnx");
        _encoderSession = LoadOrThrow(modelDirectory, $"encoder_model{suffix}.onnx");
        _decoderSession = LoadOrThrow(modelDirectory, $"decoder_model{suffix}.onnx");

        // Tokenizer is shared across quantizations — same vocab files in both folders.
        string vocabPath = Path.Combine(modelDirectory, "vocab.json");
        string mergesPath = Path.Combine(modelDirectory, "merges.txt");
        if (!File.Exists(vocabPath) || !File.Exists(mergesPath))
        {
            throw new FileNotFoundException(
                "Florence-2 tokenizer files (vocab.json + merges.txt) not found alongside the model. " +
                "These are emitted alongside the ONNX files in the onnx-community Florence-2 repos.",
                File.Exists(vocabPath) ? mergesPath : vocabPath);
        }
        using FileStream vocabStream = File.OpenRead(vocabPath);
        using FileStream mergesStream = File.OpenRead(mergesPath);
        _tokenizer = BpeTokenizer.Create(vocabStream, mergesStream);

        // Resolve the task prompt to its token IDs. Florence-2 special tokens
        // (<CAPTION>, <DETAILED_CAPTION>, etc.) live in the vocab as single
        // tokens — we look them up directly via the tokenizer's vocab map
        // since BpeTokenizer.EncodeToIds doesn't always handle special tokens
        // verbatim.
        _taskPromptTokenIds = ResolveTaskPromptTokens(taskPrompt);

        _visionInputName = Session.InputMetadata.Keys.First();
        _visionOutputName = Session.OutputMetadata.Keys.First();
        _embedInputName = _embedTokensSession.InputMetadata.Keys.First();
        _embedOutputName = _embedTokensSession.OutputMetadata.Keys.First();

        // Encoder takes inputs_embeds + attention_mask; decoder takes
        // input_ids + encoder_hidden_states + encoder_attention_mask. These
        // are the canonical Florence-2 ONNX input names.
        _encoderInputName = "inputs_embeds";
        _encoderAttentionMaskName = "attention_mask";
        _encoderOutputName = _encoderSession.OutputMetadata.Keys.First();
        // Florence-2's decoder takes pre-embedded tokens (inputs_embeds), not
        // raw token IDs — the embed_tokens lookup runs separately each step.
        _decoderInputIdsName = "inputs_embeds";
        _decoderEncoderHiddenStatesName = "encoder_hidden_states";
        _decoderEncoderAttentionMaskName = "encoder_attention_mask";
        _decoderLogitsName = "logits";
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
                    $"Florence2Model received a null image at row {row}; filter nulls upstream.");
            }
            SKBitmap decoded = image.AsImage();
            ImageTensorPrep.StretchAndPackNchw(
                decoded, tensorData.AsSpan(row * perImageFloats, perImageFloats),
                InputWidth, InputHeight,
                ImageTensorPrep.ImageNetScale, ImageTensorPrep.ImageNetBias);
        }

        return await Task.Run<IReadOnlyList<ValueRef>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Per-row dispatch. Florence-2's encoder takes the concatenation
            // of visual features + text embeddings, which means each row's
            // sequence length is different (visual seq + variable prompt
            // tokens). We loop per-row rather than batching to keep the
            // shapes simple. For multi-image batched generation, we'd pad
            // and mask — defer until throughput matters.
            ValueRef[] results = new ValueRef[batchSize];
            for (int row = 0; row < batchSize; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 1. Vision encoder pass (per row).
                float[] perImage = new float[perImageFloats];
                tensorData.AsSpan(row * perImageFloats, perImageFloats).CopyTo(perImage);
                DenseTensor<float> pixelValues = new(perImage,
                    [1, InputChannels, InputHeight, InputWidth]);

                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> visionOutputs =
                    Session.Run([OnnxTensorConversion.CreateAutoCastInput(Session, _visionInputName, pixelValues)]);
                DisposableNamedOnnxValue visionOutput = visionOutputs.First();
                DenseTensor<float> visualFeatures = OnnxTensorConversion.ToFloatTensor(visionOutput);
                int[] visualShape = visualFeatures.Dimensions.ToArray();
                int visualSeqLen = visualShape[1];
                int hiddenDim = visualShape[2];

                // 2. Embed task-prompt tokens.
                long[] promptIdsBuffer = new long[_taskPromptTokenIds.Length];
                for (int i = 0; i < _taskPromptTokenIds.Length; i++)
                    promptIdsBuffer[i] = _taskPromptTokenIds[i];
                DenseTensor<long> promptIds = new(promptIdsBuffer,
                    [1, _taskPromptTokenIds.Length]);

                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> embedOutputs =
                    _embedTokensSession.Run([NamedOnnxValue.CreateFromTensor(_embedInputName, promptIds)]);
                DenseTensor<float> textEmbeds = OnnxTensorConversion.ToFloatTensor(embedOutputs.First());
                int promptSeqLen = textEmbeds.Dimensions[1];

                // 3. Concatenate visual + text embeddings into one sequence
                //    [1, visual_seq + prompt_seq, hidden_dim] and build the
                //    matching all-ones attention mask.
                int totalSeqLen = visualSeqLen + promptSeqLen;
                float[] combinedBuffer = new float[totalSeqLen * hiddenDim];
                visualFeatures.Buffer.Span.CopyTo(combinedBuffer.AsSpan(0, visualSeqLen * hiddenDim));
                textEmbeds.Buffer.Span.CopyTo(combinedBuffer.AsSpan(visualSeqLen * hiddenDim));
                DenseTensor<float> combinedEmbeds = new(combinedBuffer, [1, totalSeqLen, hiddenDim]);

                long[] attentionMaskBuffer = new long[totalSeqLen];
                for (int i = 0; i < totalSeqLen; i++) attentionMaskBuffer[i] = 1;
                DenseTensor<long> attentionMask = new(attentionMaskBuffer, [1, totalSeqLen]);

                // 4. Encoder pass.
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoderOutputs =
                    _encoderSession.Run(
                    [
                        OnnxTensorConversion.CreateAutoCastInput(_encoderSession, _encoderInputName, combinedEmbeds),
                        NamedOnnxValue.CreateFromTensor(_encoderAttentionMaskName, attentionMask),
                    ]);
                DenseTensor<float> encoderHidden = OnnxTensorConversion.ToFloatTensor(encoderOutputs.First());

                // 5. Greedy autoregressive decode.
                string caption = DecodeGreedy(
                    encoderHidden, attentionMask, totalSeqLen, hiddenDim, cancellationToken);
                results[row] = ValueRef.FromString(caption);
            }
            return results;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<ValueRef>> rows)
        => throw new InvalidOperationException(
            "Florence2Model overrides InferBatchAsync directly. BuildBatchInputs is not used.");

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
        => throw new InvalidOperationException(
            "Florence2Model overrides InferBatchAsync directly. ParseBatchOutputs is not used.");

    private string DecodeGreedy(
        DenseTensor<float> encoderHidden,
        DenseTensor<long> encoderAttentionMask,
        int totalSeqLen,
        int hiddenDim,
        CancellationToken cancellationToken)
    {
        List<int> tokens = new(_maxTokens + 1) { BosTokenId };

        for (int step = 0; step < _maxTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Florence-2's decoder takes inputs_embeds, so we run the
            // current token sequence through embed_tokens first to lift
            // them into the embedding space. embed_tokens is a cheap
            // lookup so this is fine to repeat each step (the alternative
            // would be caching embeddings + appending, but the no-cache
            // decoder pattern re-runs everything anyway).
            long[] inputIdsBuffer = new long[tokens.Count];
            for (int i = 0; i < tokens.Count; i++) inputIdsBuffer[i] = tokens[i];
            DenseTensor<long> inputIds = new(inputIdsBuffer, [1, tokens.Count]);

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> embedOutputs =
                _embedTokensSession.Run([NamedOnnxValue.CreateFromTensor(_embedInputName, inputIds)]);
            DenseTensor<float> decoderEmbeds = OnnxTensorConversion.ToFloatTensor(embedOutputs.First());

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _decoderSession.Run(
            [
                OnnxTensorConversion.CreateAutoCastInput(_decoderSession, _decoderInputIdsName, decoderEmbeds),
                OnnxTensorConversion.CreateAutoCastInput(_decoderSession, _decoderEncoderHiddenStatesName, encoderHidden),
                NamedOnnxValue.CreateFromTensor(_decoderEncoderAttentionMaskName, encoderAttentionMask),
            ]);

            DisposableNamedOnnxValue logitsValue = outputs.FirstOrDefault(v => v.Name == _decoderLogitsName)
                ?? outputs.First();
            DenseTensor<float> logits = OnnxTensorConversion.ToFloatTensor(logitsValue);
            int vocabSize = logits.Dimensions[2];
            ReadOnlySpan<float> logitsFlat = logits.Buffer.Span;
            ReadOnlySpan<float> lastPositionLogits = logitsFlat.Slice(
                (tokens.Count - 1) * vocabSize, vocabSize);

            int nextToken = ArgMax(lastPositionLogits);
            if (nextToken == EosTokenId) break;
            tokens.Add(nextToken);
        }

        // Decode token IDs back to text. Skip BOS; EOS isn't appended.
        IEnumerable<int> outputTokens = tokens.Skip(1);
        string raw = _tokenizer.Decode(outputTokens) ?? string.Empty;
        return CleanFlorenceOutput(raw);
    }

    /// <summary>
    /// Cleans Florence-2's decoded text. The BART tokenizer uses the same
    /// byte-level BPE as GPT-2 (Ġ for space, Ċ for newline) — apply the
    /// inverse mapping. Florence-2 also sometimes emits the task token
    /// at the start (e.g. <c>&lt;CAPTION&gt;a photo of a man</c>); strip it.
    /// </summary>
    private string CleanFlorenceOutput(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        // Byte-level BPE inverse — same logic as ViT-GPT2's decoder.
        byte[] bytes = new byte[raw.Length * 4];
        Span<byte> singleCharBuf = stackalloc byte[4];
        Span<char> singleChar = stackalloc char[1];
        int byteIdx = 0;
        foreach (char c in raw)
        {
            if (UnicodeToByte.TryGetValue(c, out byte mapped))
            {
                bytes[byteIdx++] = mapped;
            }
            else
            {
                singleChar[0] = c;
                int written = Encoding.UTF8.GetBytes(singleChar, singleCharBuf);
                singleCharBuf[..written].CopyTo(bytes.AsSpan(byteIdx));
                byteIdx += written;
            }
        }
        string text = Encoding.UTF8.GetString(bytes, 0, byteIdx);

        // Strip BART/Florence special tokens that occasionally leak into the
        // decoded text (the tokenizer vocab includes <s>, </s>, <pad> as
        // literal strings, so the model can emit them as content tokens).
        text = StripSpecialTokens(text);

        // Strip leading task token if echoed by the model.
        if (text.StartsWith(_taskPromptDescription, StringComparison.Ordinal))
        {
            text = text[_taskPromptDescription.Length..];
        }
        return text.Trim();
    }

    /// <summary>
    /// Removes Florence-2 / BART special-token markers (<c>&lt;s&gt;</c>,
    /// <c>&lt;/s&gt;</c>, <c>&lt;pad&gt;</c>, <c>&lt;unk&gt;</c>) that the
    /// tokenizer's <c>Decode</c> sometimes emits as literal characters. The
    /// markers carry no semantic content in the captioned text — they're
    /// vocabulary artefacts.
    /// </summary>
    private static string StripSpecialTokens(string text)
    {
        return text
            .Replace("<s>", string.Empty, StringComparison.Ordinal)
            .Replace("</s>", string.Empty, StringComparison.Ordinal)
            .Replace("<pad>", string.Empty, StringComparison.Ordinal)
            .Replace("<unk>", string.Empty, StringComparison.Ordinal);
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

    /// <summary>
    /// Extracts the component-suffix from a vision encoder filename:
    /// <c>vision_encoder_fp16.onnx</c> → <c>_fp16</c>;
    /// <c>vision_encoder.onnx</c> → <c></c> (empty).
    /// </summary>
    private static string ExtractComponentSuffix(string encoderFileName)
    {
        const string Prefix = "vision_encoder";
        const string Extension = ".onnx";
        if (!encoderFileName.StartsWith(Prefix) || !encoderFileName.EndsWith(Extension))
        {
            throw new ArgumentException(
                $"Expected encoder filename to start with '{Prefix}' and end with '{Extension}', got '{encoderFileName}'.");
        }
        return encoderFileName[Prefix.Length..^Extension.Length];
    }

    private static InferenceSession LoadOrThrow(string directory, string filename)
    {
        string path = Path.Combine(directory, filename);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Florence-2 component '{filename}' not found alongside the encoder. " +
                "All four ONNX files (vision_encoder, embed_tokens, encoder_model, decoder_model) " +
                "must share a quantization variant and live in the same folder.",
                path);
        }
        return OnnxSessionFactory.Create(path);
    }

    /// <summary>
    /// Florence-2 task prompts are user-facing aliases for full English
    /// instructions the model was actually fine-tuned on. Microsoft's
    /// inference code expands the alias to the prompt text, BPE-tokenizes
    /// it, and feeds the result into <c>embed_tokens</c>. We replicate
    /// that here.
    /// </summary>
    /// <remarks>
    /// Source: Microsoft's <c>processing_florence2.py</c> in the official
    /// HuggingFace repo. Authoritative for the literal prompt strings.
    /// </remarks>
    private static readonly Dictionary<string, string> TaskPromptToInstruction = new()
    {
        ["<CAPTION>"]                = "What does the image describe?",
        ["<DETAILED_CAPTION>"]       = "Describe in detail what is shown in the image.",
        ["<MORE_DETAILED_CAPTION>"]  = "Describe with a paragraph what is shown in the image.",
        ["<OD>"]                     = "Locate the objects with category name in the image.",
        ["<DENSE_REGION_CAPTION>"]   = "Locate the objects in the image, with their descriptions.",
        ["<REGION_PROPOSAL>"]        = "Locate the region proposals in the image.",
        ["<OCR>"]                    = "What is the text in the image?",
        ["<OCR_WITH_REGION>"]        = "What is the text in the image, with regions?",
    };

    /// <summary>
    /// Resolves a task-prompt alias (<c>&lt;CAPTION&gt;</c>, etc.) to the
    /// BPE-tokenized form the model expects, wrapped in BOS/EOS.
    /// </summary>
    private int[] ResolveTaskPromptTokens(string taskPrompt)
    {
        if (!TaskPromptToInstruction.TryGetValue(taskPrompt, out string? instruction))
        {
            throw new InvalidOperationException(
                $"Unknown Florence-2 task prompt '{taskPrompt}'. Valid: " +
                string.Join(", ", TaskPromptToInstruction.Keys));
        }

        // BPE-tokenize the instruction. EncodeToIds returns the IDs without
        // BOS/EOS; we add them ourselves to match Florence-2's training format.
        IReadOnlyList<int> bpeIds = _tokenizer.EncodeToIds(instruction);
        int[] result = new int[bpeIds.Count + 2];
        result[0] = BosTokenId;
        for (int i = 0; i < bpeIds.Count; i++) result[i + 1] = bpeIds[i];
        result[^1] = EosTokenId;
        return result;
    }

    /// <inheritdoc />
    public new void Dispose()
    {
        _embedTokensSession.Dispose();
        _encoderSession.Dispose();
        _decoderSession.Dispose();
        base.Dispose();
    }

    // ------- Byte-level BPE inverse table (shared with GPT-2 family) -------

    private static readonly Dictionary<char, byte> UnicodeToByte = BuildUnicodeToByte();

    private static Dictionary<char, byte> BuildUnicodeToByte()
    {
        Dictionary<char, byte> reverse = new(256);
        List<int> printable =
        [
            .. Enumerable.Range('!', '~' - '!' + 1),
            .. Enumerable.Range('¡', '¬' - '¡' + 1),
            .. Enumerable.Range('®', 'ÿ' - '®' + 1),
        ];
        HashSet<int> printableSet = new(printable);

        foreach (int b in printable) reverse[(char)b] = (byte)b;

        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            if (printableSet.Contains(b)) continue;
            reverse[(char)(256 + n)] = (byte)b;
            n++;
        }
        return reverse;
    }
}

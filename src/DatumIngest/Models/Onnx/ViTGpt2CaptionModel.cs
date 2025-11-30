using System.Text;
using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// ViT-GPT2 image captioner — Vision Transformer encoder feeding a GPT-2
/// autoregressive decoder. Accepts a single <see cref="DataKind.Image"/>
/// per row, returns a <see cref="DataKind.String"/> caption.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Multi-stage dispatch.</strong> Unlike single-shot ONNX models
/// (MobileNetV2 → label, YOLO → detections), captioning is two ONNX
/// sessions: the encoder turns the image into hidden states, then the
/// decoder generates tokens autoregressively conditioned on those hidden
/// states. We override <see cref="InferBatchAsync"/> to thread the
/// encoder→decoder flow ourselves.
/// </para>
/// <para>
/// <strong>Encoder.</strong> ViT-base, 224×224 input. Preprocessing uses
/// the original ViT normalisation
/// (mean=<c>[0.5, 0.5, 0.5]</c>, std=<c>[0.5, 0.5, 0.5]</c>) — *not* the
/// ImageNet statistics MobileNetV2 uses. Output shape
/// <c>[batch, 197, 768]</c> (1 CLS + 196 patches × 768 hidden).
/// </para>
/// <para>
/// <strong>Decoder.</strong> GPT-2 with cross-attention to encoder
/// hidden states. We run greedy decoding (argmax of next-token logits)
/// per image, capped at <see cref="MaxTokens"/>. Beam search would give
/// modestly better captions at higher cost; greedy is fine for the
/// single-sentence captions this model produces.
/// </para>
/// <para>
/// <strong>Folder layout.</strong> Unlike single-file ONNX models, this
/// one is a *directory* of related files (encoder + decoder + tokenizer
/// + configs). The catalog entry's <c>RelativePath</c> points at
/// <c>encoder_model.onnx</c> inside the folder; the constructor derives
/// the model directory from there and loads the decoder + tokenizer
/// alongside.
/// </para>
/// </remarks>
public sealed class ViTGpt2CaptionModel : OnnxModel
{
    private const int InputWidth = 224;
    private const int InputHeight = 224;
    private const int InputChannels = 3;

    private static readonly float[] MeanValues = [0.5f, 0.5f, 0.5f];
    private static readonly float[] StdValues = [0.5f, 0.5f, 0.5f];

    private readonly InferenceSession _decoderSession;
    private readonly BpeTokenizer _tokenizer;
    private readonly int _maxTokens;
    private readonly int _decoderStartTokenId;
    private readonly int _eosTokenId;

    private readonly string _encoderInputName;
    private readonly string _encoderOutputName;
    private readonly string _decoderInputIdsName;
    private readonly string _decoderEncoderHiddenStatesName;
    private readonly string _decoderLogitsName;

    /// <summary>Maximum tokens the decoder will generate per image.</summary>
    public int MaxTokens => _maxTokens;

    /// <inheritdoc />
    /// <remarks>
    /// Captioning takes ~500ms-1s per image (vision-encoder pass + ~16
    /// autoregressive decoder steps). Stream in groups of 8 so the user
    /// sees first captions in a few seconds rather than waiting for
    /// 1024-row batches.
    /// </remarks>
    public int? PreferredBatchSize => 8;

    /// <summary>
    /// Loads the ViT-GPT2 captioner from a directory of ONNX + tokenizer files.
    /// </summary>
    /// <param name="name">Catalog-visible name.</param>
    /// <param name="encoderModelFilePath">
    /// Absolute path to <c>encoder_model.onnx</c>. The constructor derives the
    /// model directory from this path and loads <c>decoder_model.onnx</c>,
    /// <c>vocab.json</c>, and <c>merges.txt</c> from the same folder.
    /// </param>
    /// <param name="maxTokens">
    /// Cap on generated tokens per caption. nlpconnect/vit-gpt2 produces
    /// short captions; 16 is the default and fits typical outputs.
    /// </param>
    /// <param name="decoderStartTokenId">
    /// First token fed to the decoder. GPT-2's <c>&lt;|endoftext|&gt;</c> = 50256
    /// is the standard start token for vit-gpt2 captioners.
    /// </param>
    /// <param name="eosTokenId">
    /// End-of-sequence token. Same value as start (50256) for GPT-2.
    /// </param>
    public ViTGpt2CaptionModel(
        string name,
        string encoderModelFilePath,
        int maxTokens = 16,
        int decoderStartTokenId = 50256,
        int eosTokenId = 50256)
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

        string decoderPath = Path.Combine(modelDirectory, "decoder_model.onnx");
        if (!File.Exists(decoderPath))
        {
            throw new FileNotFoundException(
                $"ViT-GPT2 decoder file 'decoder_model.onnx' not found alongside the encoder. " +
                "Both encoder_model.onnx and decoder_model.onnx must be in the same directory.",
                decoderPath);
        }
        _decoderSession = new InferenceSession(decoderPath);

        string vocabPath = Path.Combine(modelDirectory, "vocab.json");
        string mergesPath = Path.Combine(modelDirectory, "merges.txt");
        if (!File.Exists(vocabPath) || !File.Exists(mergesPath))
        {
            throw new FileNotFoundException(
                "ViT-GPT2 tokenizer files (vocab.json + merges.txt) not found alongside the model. " +
                "These are emitted by `optimum-cli export onnx` automatically — verify the export completed.",
                File.Exists(vocabPath) ? mergesPath : vocabPath);
        }
        using FileStream vocabStream = File.OpenRead(vocabPath);
        using FileStream mergesStream = File.OpenRead(mergesPath);
        _tokenizer = BpeTokenizer.Create(vocabStream, mergesStream);

        _encoderInputName = Session.InputMetadata.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException("Encoder ONNX has no declared input — file is malformed.");
        _encoderOutputName = Session.OutputMetadata.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException("Encoder ONNX has no declared output — file is malformed.");

        // Decoder ONNX has multiple named inputs; pick by exact name. The
        // optimum export uses these standard names; if a future export tweaks
        // them, adjust here rather than scanning for variants.
        _decoderInputIdsName = "input_ids";
        _decoderEncoderHiddenStatesName = "encoder_hidden_states";
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
                    $"ViTGpt2CaptionModel received a null image at row {row}; filter nulls upstream.");
            }
            byte[] bytes = image.AsBytes();
            DecodeAndPackImage(bytes, tensorData.AsSpan(row * perImageFloats, perImageFloats));
        }

        return await Task.Run<IReadOnlyList<ValueRef>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Encoder: one batched call. Output shape is [batch, seq_len, hidden_dim].
            DenseTensor<float> pixelValues = new(
                tensorData,
                [batchSize, InputChannels, InputHeight, InputWidth]);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoderOutputs = Session.Run(
                [NamedOnnxValue.CreateFromTensor(_encoderInputName, pixelValues)]);

            DisposableNamedOnnxValue encoderOutput = encoderOutputs.FirstOrDefault()
                ?? throw new InvalidOperationException("ViT encoder produced no output.");
            DenseTensor<float> encoderHidden = encoderOutput.AsTensor<float>().ToDenseTensor();
            int[] hiddenShape = encoderHidden.Dimensions.ToArray();
            // Expected: [batchSize, seqLen, hiddenDim]
            if (hiddenShape.Length != 3 || hiddenShape[0] != batchSize)
            {
                throw new InvalidOperationException(
                    $"ViT encoder output shape {string.Join('x', hiddenShape)} doesn't match " +
                    $"expected [{batchSize}, *, *]. Model file may not be vit-gpt2 compatible.");
            }
            int seqLen = hiddenShape[1];
            int hiddenDim = hiddenShape[2];

            // Per-row decoder loop. Greedy decoding: each step runs the full
            // sequence through the decoder, takes argmax of the last position's
            // logits, appends, repeats until EOS or max_length.
            ReadOnlySpan<float> hiddenFlat = encoderHidden.Buffer.Span;
            int hiddenPerRow = seqLen * hiddenDim;
            ValueRef[] results = new ValueRef[batchSize];

            for (int row = 0; row < batchSize; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Slice this row's encoder output and reshape to a tensor with
                // batch dim 1 for the decoder call.
                float[] rowHidden = new float[hiddenPerRow];
                hiddenFlat.Slice(row * hiddenPerRow, hiddenPerRow).CopyTo(rowHidden);

                string caption = GenerateCaptionGreedy(rowHidden, seqLen, hiddenDim, cancellationToken);
                results[row] = ValueRef.FromString(caption);
            }

            return results;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<ValueRef>> rows)
        => throw new InvalidOperationException(
            "ViTGpt2CaptionModel overrides InferBatchAsync directly. BuildBatchInputs is not used.");

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
        => throw new InvalidOperationException(
            "ViTGpt2CaptionModel overrides InferBatchAsync directly. ParseBatchOutputs is not used.");

    /// <summary>
    /// Decodes the encoded image bytes, resizes to 224×224 RGB, normalises
    /// with ViT statistics, and writes the result into <paramref name="dest"/>
    /// in NCHW layout (R-plane, then G-plane, then B-plane).
    /// </summary>
    private static void DecodeAndPackImage(byte[] imageBytes, Span<float> dest)
    {
        using SKBitmap? decoded = SKBitmap.Decode(imageBytes)
            ?? throw new InvalidOperationException(
                "SkiaSharp failed to decode image bytes for ViT-GPT2 input.");

        SKImageInfo targetInfo = new(InputWidth, InputHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap resized = decoded.Resize(targetInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"SkiaSharp failed to resize image to {InputWidth}×{InputHeight} for ViT-GPT2 input.");

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

    /// <summary>
    /// Greedy decode for a single image: feed the encoder's hidden states
    /// + a growing token sequence into the decoder until EOS or max_length.
    /// </summary>
    private string GenerateCaptionGreedy(
        float[] encoderHiddenStates,
        int seqLen,
        int hiddenDim,
        CancellationToken cancellationToken)
    {
        // Encoder hidden states for this single image — used unchanged at every step.
        DenseTensor<float> hiddenTensor = new(
            encoderHiddenStates,
            [1, seqLen, hiddenDim]);

        List<int> tokens = new(_maxTokens + 1) { _decoderStartTokenId };

        for (int step = 0; step < _maxTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // input_ids: [1, current_length]. We pass the full sequence each
            // step because we're using the no-cache decoder variant.
            long[] inputIdsBuffer = new long[tokens.Count];
            for (int i = 0; i < tokens.Count; i++) inputIdsBuffer[i] = tokens[i];
            DenseTensor<long> inputIds = new(inputIdsBuffer, [1, tokens.Count]);

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _decoderSession.Run(
            [
                NamedOnnxValue.CreateFromTensor(_decoderInputIdsName, inputIds),
                NamedOnnxValue.CreateFromTensor(_decoderEncoderHiddenStatesName, hiddenTensor),
            ]);

            DisposableNamedOnnxValue logitsValue = outputs.FirstOrDefault(v => v.Name == _decoderLogitsName)
                ?? outputs.First();
            DenseTensor<float> logits = logitsValue.AsTensor<float>().ToDenseTensor();
            int[] logitsShape = logits.Dimensions.ToArray();
            // Expected: [1, seq_len, vocab_size]
            if (logitsShape.Length != 3 || logitsShape[0] != 1 || logitsShape[1] != tokens.Count)
            {
                throw new InvalidOperationException(
                    $"Decoder logits shape {string.Join('x', logitsShape)} doesn't match " +
                    $"expected [1, {tokens.Count}, vocab].");
            }

            int vocabSize = logitsShape[2];
            ReadOnlySpan<float> logitsFlat = logits.Buffer.Span;
            // Logits for the last position: offset = (seq_len - 1) * vocab_size.
            ReadOnlySpan<float> lastPositionLogits = logitsFlat.Slice(
                (tokens.Count - 1) * vocabSize, vocabSize);

            int nextToken = ArgMax(lastPositionLogits);
            if (nextToken == _eosTokenId) break;
            tokens.Add(nextToken);
        }

        // Decode token IDs back to text. Skip the start token (index 0); the
        // model treats it as a sentinel rather than emitted content. EOS isn't
        // appended (we break before adding it).
        IEnumerable<int> outputTokens = tokens.Skip(1);
        string raw = _tokenizer.Decode(outputTokens) ?? string.Empty;
        return DecodeByteLevelBpe(raw).Trim();
    }

    /// <summary>
    /// Applies the inverse of GPT-2's byte-level BPE encoding. The encoder
    /// maps non-printable bytes (including space and newline) to high-Unicode
    /// codepoints so they survive BPE merging without ambiguity; the decoder
    /// must reverse that mapping. <c>BpeTokenizer.Decode</c> doesn't apply
    /// this reverse step, so a literal <c>Ġ</c> (U+0120, the encoded form of
    /// space) leaks into the output. We unmap it here.
    /// </summary>
    /// <remarks>
    /// The GPT-2 byte-to-unicode table assigns codepoints 256+ to bytes
    /// outside the printable-ASCII range. For English captions only two
    /// bytes typically need translation: space (<c>0x20 → Ġ</c> at U+0120)
    /// and newline (<c>0x0A → Ċ</c> at U+010A). We translate the full
    /// 188-byte mapping for correctness across non-English outputs.
    /// </remarks>
    private static string DecodeByteLevelBpe(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        byte[] bytes = new byte[raw.Length * 4];  // worst-case UTF-8 expansion
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
                // Unmapped char (printable ASCII or genuinely emitted Unicode):
                // pass through as UTF-8.
                singleChar[0] = c;
                int written = Encoding.UTF8.GetBytes(singleChar, singleCharBuf);
                singleCharBuf[..written].CopyTo(bytes.AsSpan(byteIdx));
                byteIdx += written;
            }
        }
        return Encoding.UTF8.GetString(bytes, 0, byteIdx);
    }

    /// <summary>
    /// Inverse of GPT-2's byte_to_unicode table — maps the high-Unicode
    /// codepoints the encoder emitted back to their original bytes.
    /// Built lazily once per process; small (188 entries).
    /// </summary>
    private static readonly Dictionary<char, byte> UnicodeToByte = BuildUnicodeToByte();

    private static Dictionary<char, byte> BuildUnicodeToByte()
    {
        // Replicate GPT-2's bytes_to_unicode: printable bytes (! through ~,
        // ¡ through ¬, ® through ÿ) map to themselves; the remaining 68
        // unmapped bytes get codepoints starting at 0x100 (256).
        Dictionary<char, byte> reverse = new(256);
        List<int> printable =
        [
            .. Enumerable.Range('!', '~' - '!' + 1),
            .. Enumerable.Range('¡', '¬' - '¡' + 1),
            .. Enumerable.Range('®', 'ÿ' - '®' + 1),
        ];
        HashSet<int> printableSet = new(printable);

        // Self-mapped: byte b → char b for printable bytes.
        foreach (int b in printable)
        {
            reverse[(char)b] = (byte)b;
        }

        // Encoded: byte b → char (256 + n) for non-printable b, n incrementing.
        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            if (printableSet.Contains(b)) continue;
            reverse[(char)(256 + n)] = (byte)b;
            n++;
        }
        return reverse;
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

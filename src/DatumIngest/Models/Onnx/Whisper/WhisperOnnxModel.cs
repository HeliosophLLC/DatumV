using System.Text;
using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace DatumIngest.Models.Onnx.Whisper;

/// <summary>
/// Whisper STT — accepts raw WAV bytes, returns the transcribed text.
/// Wraps two ONNX sessions (encoder + decoder, no KV cache) and the
/// Whisper feature extractor pipeline (mel spectrogram → log10 → clip).
/// English-only multilingual transcription for the baseline; other
/// languages by changing the language-token override.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Folder layout</strong> — same shape as <c>optimum-cli export onnx --model openai/whisper-base</c>:
/// <code>
/// whisper-base-onnx/
///   encoder_model.onnx
///   decoder_model.onnx
///   vocab.json
///   merges.txt
///   ...
/// </code>
/// The catalog entry's <c>RelativePath</c> points at <c>encoder_model.onnx</c>;
/// the constructor derives the model directory from there.
/// </para>
/// <para>
/// <strong>Decoder cadence.</strong> We use the no-cache <c>decoder_model.onnx</c>
/// (recompute the full sequence each step) for code simplicity, matching
/// <c>ViTGpt2CaptionModel</c>. <c>decoder_with_past_model.onnx</c> would
/// be ~5× faster on long transcripts but adds KV-cache plumbing for ~12
/// past_key_values inputs and outputs per layer; not worth it for the
/// short clips this baseline targets.
/// </para>
/// <para>
/// <strong>Audio length.</strong> Hardcoded to 30s clips (the encoder's
/// fixed context). Longer audio needs sliding-window chunking with stitched
/// transcripts — out of scope for this baseline; truncate or split upstream.
/// </para>
/// </remarks>
public sealed class WhisperOnnxModel : OnnxModel
{
    /// <summary>Whisper's <c>&lt;|startoftranscript|&gt;</c> sentinel.</summary>
    private const int StartOfTranscriptToken = 50258;

    /// <summary>Whisper's <c>&lt;|en|&gt;</c> language token (multilingual model).</summary>
    private const int EnglishLanguageToken = 50259;

    /// <summary>Whisper's <c>&lt;|transcribe|&gt;</c> task token (vs translate).</summary>
    private const int TranscribeTaskToken = 50359;

    /// <summary>Whisper's <c>&lt;|notimestamps|&gt;</c> mode token.</summary>
    private const int NoTimestampsToken = 50363;

    /// <summary>Whisper's <c>&lt;|endoftext|&gt;</c> EOS / BOS / PAD.</summary>
    private const int EndOfTextToken = 50257;

    /// <summary>Hard cap from <c>generation_config.json</c>.</summary>
    private const int MaxLength = 448;

    private readonly InferenceSession _decoderSession;
    private readonly BpeTokenizer _tokenizer;
    private readonly WhisperMelSpectrogram _melExtractor;
    private readonly int _maxTokens;

    /// <summary>Stream STT results 4 rows at a time. ~3-15s per clip means
    /// the user sees first transcripts in seconds, not after a 1024-row batch.</summary>
    public int? PreferredBatchSize => 4;

    /// <summary>
    /// Loads Whisper from a directory of ONNX + tokenizer files.
    /// </summary>
    /// <param name="name">Catalog-visible name.</param>
    /// <param name="encoderModelFilePath">
    /// Absolute path to <c>encoder_model.onnx</c>. The constructor derives
    /// the model directory from this and loads <c>decoder_model.onnx</c>,
    /// <c>vocab.json</c>, and <c>merges.txt</c> from the same folder.
    /// </param>
    /// <param name="maxTokens">
    /// Cap on generated tokens per clip. Whisper's hard ceiling is 448
    /// (from the model's positional embeddings); for short clips, lower
    /// values cut runtime by limiting the worst-case decoder loop.
    /// </param>
    public WhisperOnnxModel(
        string name,
        string encoderModelFilePath,
        int maxTokens = MaxLength)
        : base(
            name,
            encoderModelFilePath,
            inputKinds: [DataKind.Audio],
            outputKind: DataKind.String,
            isDeterministic: true)
    {
        _maxTokens = maxTokens;

        string modelDirectory = Path.GetDirectoryName(encoderModelFilePath)
            ?? throw new InvalidOperationException(
                $"Could not derive model directory from '{encoderModelFilePath}'.");

        string decoderPath = Path.Combine(modelDirectory, "decoder_model.onnx");
        if (!File.Exists(decoderPath))
        {
            throw new FileNotFoundException(
                "Whisper 'decoder_model.onnx' not found alongside the encoder. " +
                "Both files are produced by `optimum-cli export onnx --model openai/whisper-base`.",
                decoderPath);
        }
        _decoderSession = OnnxSessionFactory.Create(decoderPath);

        string vocabPath = Path.Combine(modelDirectory, "vocab.json");
        string mergesPath = Path.Combine(modelDirectory, "merges.txt");
        if (!File.Exists(vocabPath) || !File.Exists(mergesPath))
        {
            throw new FileNotFoundException(
                "Whisper tokenizer files (vocab.json + merges.txt) not found alongside the model.",
                File.Exists(vocabPath) ? mergesPath : vocabPath);
        }
        using FileStream vocabStream = File.OpenRead(vocabPath);
        using FileStream mergesStream = File.OpenRead(mergesPath);
        _tokenizer = BpeTokenizer.Create(vocabStream, mergesStream);

        _melExtractor = new WhisperMelSpectrogram();
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

            int batchSize = inputs.Count;
            ValueRef[] results = new ValueRef[batchSize];

            // Per-row dispatch — Whisper's encoder accepts batched input but
            // the autoregressive decoder doesn't benefit much from batching
            // (different sequences finish at different lengths). One row per
            // pass keeps memory bounded and code simple.
            for (int row = 0; row < batchSize; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ValueRef input = inputs[row][0];
                if (input.IsNull)
                {
                    throw new InvalidOperationException(
                        $"WhisperOnnxModel received null audio at row {row}; filter nulls upstream.");
                }

                byte[] wavBytes = input.AsBytes();
                float[] audio = WhisperAudioInput.DecodeToMono16k(wavBytes);
                float[] melFeatures = _melExtractor.ComputeMel(audio);

                string transcript = TranscribeOne(melFeatures, cancellationToken);
                results[row] = ValueRef.FromString(transcript);
            }

            return results;
        }, cancellationToken).ConfigureAwait(false);
    }

    private string TranscribeOne(float[] melFeatures, CancellationToken cancellationToken)
    {
        // Encoder pass: mel features [1, n_mels, n_frames] → hidden states [1, seq, hidden]
        DenseTensor<float> melTensor = new(
            melFeatures,
            [1, _melExtractor.NumMels, WhisperMelSpectrogram.NumFrames]);

        string encoderInputName = Session.InputMetadata.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException("Whisper encoder ONNX has no declared input.");

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoderOutputs = Session.Run(
            [OnnxTensorConversion.CreateAutoCastInput(Session, encoderInputName, melTensor)]);

        DisposableNamedOnnxValue encoderOutput = encoderOutputs.FirstOrDefault()
            ?? throw new InvalidOperationException("Whisper encoder produced no output.");
        DenseTensor<float> encoderHidden = OnnxTensorConversion.ToFloatTensor(encoderOutput);

        // Greedy decoder loop. Prefix is [SOT, lang, task, no_timestamps];
        // we generate content tokens until EOS or max_tokens.
        List<int> tokens =
        [
            StartOfTranscriptToken,
            EnglishLanguageToken,
            TranscribeTaskToken,
            NoTimestampsToken,
        ];

        for (int step = 0; step < _maxTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long[] inputIdsBuffer = new long[tokens.Count];
            for (int i = 0; i < tokens.Count; i++) inputIdsBuffer[i] = tokens[i];
            DenseTensor<long> inputIds = new(inputIdsBuffer, [1, tokens.Count]);

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> decoderOutputs = _decoderSession.Run(
            [
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                OnnxTensorConversion.CreateAutoCastInput(_decoderSession, "encoder_hidden_states", encoderHidden),
            ]);

            DisposableNamedOnnxValue logitsValue = decoderOutputs.FirstOrDefault(v => v.Name == "logits")
                ?? decoderOutputs.First();
            DenseTensor<float> logits = OnnxTensorConversion.ToFloatTensor(logitsValue);
            int[] shape = logits.Dimensions.ToArray();
            // Expect [1, seq_len, vocab_size]
            if (shape.Length != 3 || shape[0] != 1 || shape[1] != tokens.Count)
            {
                throw new InvalidOperationException(
                    $"Whisper decoder logits shape {string.Join('x', shape)} doesn't match " +
                    $"expected [1, {tokens.Count}, vocab].");
            }
            int vocabSize = shape[2];
            ReadOnlySpan<float> logitsFlat = logits.Buffer.Span;
            ReadOnlySpan<float> lastLogits = logitsFlat.Slice(
                (tokens.Count - 1) * vocabSize, vocabSize);

            int nextToken = ArgMaxWithSpecialTokenSuppression(lastLogits);
            if (nextToken == EndOfTextToken) break;
            tokens.Add(nextToken);
        }

        return DecodeTranscript(tokens);
    }

    /// <summary>
    /// Greedy argmax that masks the special-token range so the decoder
    /// never emits stray <c>&lt;|...|&gt;</c> tokens in the transcript
    /// proper. EOS (50257) is allowed and signals end-of-clip; other
    /// IDs ≥ 50258 are suppressed.
    /// </summary>
    private static int ArgMaxWithSpecialTokenSuppression(ReadOnlySpan<float> logits)
    {
        int bestIdx = 0;
        float bestVal = float.NegativeInfinity;
        // Allow EOS (50257) explicitly; suppress 50258+ (start/language/task/timestamps).
        int suppressFrom = EndOfTextToken + 1;
        int searchEnd = Math.Min(logits.Length, suppressFrom);
        for (int i = 0; i < searchEnd; i++)
        {
            if (logits[i] > bestVal)
            {
                bestVal = logits[i];
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    /// <summary>
    /// Convert generated token IDs back to text. Skips the prefix tokens,
    /// uses BpeTokenizer to detokenise, and reverses GPT-2's byte-level BPE
    /// encoding (Ġ → space, etc.).
    /// </summary>
    private string DecodeTranscript(List<int> tokens)
    {
        // Skip the prefix [SOT, lang, task, no_timestamps]; keep only
        // content tokens. EOS isn't appended (we break before adding it).
        IEnumerable<int> contentTokens = tokens.Skip(4).Where(t => t < EndOfTextToken);
        string raw = _tokenizer.Decode(contentTokens) ?? string.Empty;
        return DecodeByteLevelBpe(raw).Trim();
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<ValueRef>> rows)
        => throw new InvalidOperationException(
            "WhisperOnnxModel overrides InferBatchAsync directly. BuildBatchInputs is not used.");

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
        => throw new InvalidOperationException(
            "WhisperOnnxModel overrides InferBatchAsync directly. ParseBatchOutputs is not used.");

    // ─── GPT-2 byte-level BPE inverse mapping ─────────────────────────────
    //
    // Same logic as ViTGpt2CaptionModel — Whisper uses the same byte-level
    // BPE encoding as GPT-2. Space encodes as 'Ġ' (U+0120), newline as
    // 'Ċ' (U+010A), and so on. BpeTokenizer.Decode returns the raw
    // codepoints; we unmap them back to bytes here.

    private static readonly Dictionary<char, byte> UnicodeToByte = BuildUnicodeToByte();

    private static string DecodeByteLevelBpe(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

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
        return Encoding.UTF8.GetString(bytes, 0, byteIdx);
    }

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

        foreach (int b in printable)
        {
            reverse[(char)b] = (byte)b;
        }

        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            if (printableSet.Contains(b)) continue;
            reverse[(char)(256 + n)] = (byte)b;
            n++;
        }
        return reverse;
    }

    /// <inheritdoc />
    public new void Dispose()
    {
        _decoderSession.Dispose();
        base.Dispose();
    }
}

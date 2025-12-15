using System.Text.Json;

using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// Meta MusicGen — text-to-audio music generation model. Generates mono
/// 32 kHz audio from a natural-language prompt using an autoregressive
/// transformer decoder conditioned on T5 text embeddings, with EnCodec
/// for audio tokenisation and decoding.
/// </summary>
/// <remarks>
/// Four ONNX sessions are loaded at construction:
/// <list type="bullet">
///   <item><c>text_encoder.onnx</c> — T5 encoder, maps prompt tokens to
///     cross-attention conditioning embeddings.</item>
///   <item><c>decoder_model.onnx</c> — first autoregressive step, no past
///     KV cache.</item>
///   <item><c>decoder_with_past_model.onnx</c> — subsequent steps; accepts
///     the growing KV cache from the previous step.</item>
///   <item><c>encodec_decode.onnx</c> — converts the generated discrete
///     audio token sequence back to a PCM waveform.</item>
/// </list>
/// The optimum-exported <c>build_delay_pattern_mask.onnx</c> is intentionally
/// not loaded: it bakes in <c>max_length=16</c>, useless for real generation.
/// We apply MusicGen's inter-codebook delay pattern in C# instead — see the
/// step 3 / step 6 comments in <see cref="GenerateAudio"/>.
/// KV cache tensor names are discovered dynamically from session metadata
/// so the same class works for both Small (24 layers) and Medium (48 layers).
/// </remarks>
internal sealed class MusicGenModel : IModel, IDisposable
{
    private readonly InferenceSession _textEncoderSession;
    private readonly InferenceSession _decoderFirstSession;
    private readonly InferenceSession _decoderPastSession;
    private readonly InferenceSession _encodecSession;
    private readonly T5Tokenizer _tokenizer;
    private readonly Random _rng;

    private readonly int _numCodebooks;
    private readonly int _sampleRate;
    private readonly int _maxNewTokens;
    private readonly long _bosTokenId;
    private readonly long _padTokenId;

    // Stable order of past_key_values.* inputs to decoder_with_past_model. We
    // route both first-step and subsequent-step "present.*" outputs back into
    // these slots by name (present.X → past_key_values.X). The first step
    // populates every slot (both decoder.* and encoder.*); subsequent steps
    // typically only update the decoder.* slots — the encoder.* KV is invariant
    // across decoder steps because the prompt embedding doesn't change.
    private readonly string[] _pastInputNames;

    // Decoder input names — vary across optimum export versions, so we resolve
    // them by trying a list of candidates against the actual session metadata.
    private readonly string _decoderInputIdsName;
    private readonly string _decoderEncHiddenName;
    private readonly string _decoderEncMaskName;

    // Memory locations for IoBinding. CUDA = GPU device memory (KV caches stay
    // resident); CPU = system memory (logits land here so we can sample without
    // a separate copy-back). Constructed once and reused across every step.
    private readonly OrtMemoryInfo _cudaMemInfo = new("Cuda", OrtAllocatorType.DeviceAllocator, 0, OrtMemType.Default);
    private readonly OrtMemoryInfo _cpuMemInfo = OrtMemoryInfo.DefaultInstance;

    public string Name { get; }
    public bool IsDeterministic => false;
    public IReadOnlyList<DataKind> InputKinds { get; } = [DataKind.String];
    public DataKind OutputKind => DataKind.Audio;

    public MusicGenModel(string name, string modelDirectory, int maxNewTokens = 512, int? seed = null)
    {
        Name = name;
        _maxNewTokens = maxNewTokens;
        _rng = seed is int s ? new Random(s) : new Random();

        _textEncoderSession = OnnxSessionFactory.Create(Path.Combine(modelDirectory, "text_encoder.onnx"));
        _decoderFirstSession = OnnxSessionFactory.Create(Path.Combine(modelDirectory, "decoder_model.onnx"));
        _decoderPastSession = OnnxSessionFactory.Create(Path.Combine(modelDirectory, "decoder_with_past_model.onnx"));
        _encodecSession = OnnxSessionFactory.Create(Path.Combine(modelDirectory, "encodec_decode.onnx"));

        _tokenizer = T5Tokenizer.FromTokenizerJson(Path.Combine(modelDirectory, "tokenizer.json"));

        // config.json: num_codebooks, audio sample rate
        using JsonDocument configDoc = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(modelDirectory, "config.json")));
        JsonElement root = configDoc.RootElement;
        _numCodebooks = root.TryGetProperty("num_codebooks", out JsonElement nc) ? nc.GetInt32() : 4;
        _sampleRate = root.TryGetProperty("audio_encoder", out JsonElement ae) &&
                      ae.TryGetProperty("sampling_rate", out JsonElement sr)
            ? sr.GetInt32() : 32000;

        // generation_config.json: BOS / PAD token ids
        using JsonDocument genDoc = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(modelDirectory, "generation_config.json")));
        JsonElement gen = genDoc.RootElement;
        _bosTokenId = gen.TryGetProperty("bos_token_id", out JsonElement bos) ? bos.GetInt64() : 2048;
        _padTokenId = gen.TryGetProperty("pad_token_id", out JsonElement pad) ? pad.GetInt64() : 2048;

        // Stable order of past_key_values.* slots. _pastInputNames[i] receives
        // the tensor from kvCaches[i] every step; first-step "present.X.*"
        // outputs initialise the slots by name mapping (present → past_key_values).
        _pastInputNames = [.. _decoderPastSession.InputMetadata.Keys
            .Where(k => k.StartsWith("past_key_values.", StringComparison.Ordinal))
            .Order()];

        // Resolve decoder input names — optimum exports vary across versions.
        // 'encoder_hidden_states' is needed only by decoder_model (first step);
        // decoder_with_past_model receives the encoder KV via past_key_values.X.encoder.*
        // and so does NOT take 'encoder_hidden_states' as an input.
        _decoderInputIdsName = ResolveInputName(_decoderFirstSession,
            "input_ids", "decoder_input_ids");
        _decoderEncHiddenName = ResolveInputName(_decoderFirstSession,
            "encoder_hidden_states", "encoder_outputs", "encoder_last_hidden_state",
            "last_hidden_state");
        _decoderEncMaskName = ResolveInputName(_decoderFirstSession,
            "encoder_attention_mask", "attention_mask");

        // Sanity-check that decoder_with_past_model has input_ids + encoder_attention_mask
        // under the same names. encoder_hidden_states is intentionally not required here.
        if (!_decoderPastSession.InputMetadata.ContainsKey(_decoderInputIdsName) ||
            !_decoderPastSession.InputMetadata.ContainsKey(_decoderEncMaskName))
        {
            throw new InvalidOperationException(
                $"MusicGen decoder_with_past_model is missing '{_decoderInputIdsName}' or " +
                $"'{_decoderEncMaskName}'. Available: " +
                $"[{string.Join(", ", _decoderPastSession.InputMetadata.Keys)}]");
        }
    }

    /// <summary>
    /// Maps a <c>present.X.Y.Z</c> output name to its corresponding
    /// <c>past_key_values.X.Y.Z</c> input name (same KV slot, next iteration).
    /// </summary>
    private static string PresentToPastName(string presentName)
        => "past_key_values." + presentName["present.".Length..];

    /// <summary>
    /// Returns the first <paramref name="candidates"/> entry that exists in
    /// <paramref name="session"/>'s input metadata, or throws an
    /// <see cref="InvalidOperationException"/> listing the actual input names.
    /// </summary>
    private static string ResolveInputName(InferenceSession session, params string[] candidates)
    {
        foreach (string c in candidates)
            if (session.InputMetadata.ContainsKey(c))
                return c;
        throw new InvalidOperationException(
            $"None of [{string.Join(", ", candidates)}] found in session inputs " +
            $"[{string.Join(", ", session.InputMetadata.Keys)}].");
    }

    public Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<ValueRef>>(() =>
        {
            ValueRef[] results = new ValueRef[inputs.Count];
            for (int i = 0; i < inputs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results[i] = GenerateAudio(inputs[i][0].AsString(), cancellationToken);
            }
            return (IReadOnlyList<ValueRef>)results;
        }, cancellationToken);
    }

    private ValueRef GenerateAudio(string prompt, CancellationToken ct)
    {
        // --- 1. Tokenize ---
        long[] tokenIds = _tokenizer.Encode(prompt);
        int seqLen = tokenIds.Length;
        long[] attMaskArr = new long[seqLen];
        Array.Fill(attMaskArr, 1L);

        DenseTensor<long> inputIdsTensor = new(tokenIds, [1, seqLen]);
        DenseTensor<long> attMaskTensor = new(attMaskArr, [1, seqLen]);

        // --- 2. Text encode (T5) ---
        DenseTensor<float> encoderHidden;
        {
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encOut =
                _textEncoderSession.Run([
                    NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
                    NamedOnnxValue.CreateFromTensor("attention_mask", attMaskTensor),
                ]);
            encoderHidden = OnnxTensorConversion.ToFloatTensor(
                encOut.First(o => o.Name == "last_hidden_state"));
        }

        // --- 3. Build initial input_ids with the inter-codebook delay pattern ---
        // The optimum-exported build_delay_pattern_mask.onnx hard-codes max_length
        // at 16, useless for real generation, so we apply the pattern in C#:
        // codebook k's "real" stream starts at step k. At step 0 only codebook 0
        // emits a BOS; codebooks 1..num_codebooks-1 sit on pad_token_id until their
        // turn comes around. The model was trained on this pattern and continues
        // to produce delayed outputs naturally — we just need to undo the delay
        // before passing tokens to EnCodec (see step 6).
        long[] initIds = new long[_numCodebooks];
        initIds[0] = _bosTokenId;
        for (int k = 1; k < _numCodebooks; k++)
            initIds[k] = _padTokenId;
        DenseTensor<long> currentIds = new(initIds, [_numCodebooks, 1]);

        // --- 4 & 5. Decoder pipeline with IoBinding ---
        // KV caches are kept on GPU between iterations: each step's "present.*"
        // outputs become the next step's "past_key_values.*" inputs by name.
        // Only logits cross to CPU each step (small) — the bulky KV tensors
        // (~50 MiB at peak for Small) never round-trip through managed memory.
        //
        // Lifetime model:
        // - firstBinding lives for the entire generation. It owns the encoder
        //   KV cache, which every subsequent step reads but no step rewrites.
        // - prevPastBinding holds the most-recent decoder_past binding whose
        //   outputs (decoder.* KV) are still referenced by the kvCaches map.
        //   Once the next step refreshes those entries, prevPastBinding can
        //   be disposed.

        long[] nextTokens;
        Dictionary<string, OrtValue> kvCaches = new(StringComparer.Ordinal);
        List<long[]> allTokens;

        using RunOptions runOpts = new();

        // CPU-resident input OrtValues that are constant for the whole generation.
        long[] encMaskShape = [1, seqLen];
        using OrtValue encMaskOv = OrtValue.CreateTensorValueFromMemory(
            _cpuMemInfo, attMaskArr.AsMemory(), encMaskShape);

        long[] encHiddenShape = encoderHidden.Dimensions.ToArray().Select(d => (long)d).ToArray();
        using OrtValue encHiddenOv = OrtValue.CreateTensorValueFromMemory(
            _cpuMemInfo, encoderHidden.Buffer, encHiddenShape);

        OrtIoBinding firstBinding = _decoderFirstSession.CreateIoBinding();
        OrtIoBinding? prevPastBinding = null;

        try
        {
            long[] initIdsShape = [_numCodebooks, 1];
            using OrtValue initIdsOv = OrtValue.CreateTensorValueFromMemory(
                _cpuMemInfo, initIds.AsMemory(), initIdsShape);

            firstBinding.BindInput(_decoderInputIdsName, initIdsOv);
            firstBinding.BindInput(_decoderEncHiddenName, encHiddenOv);
            firstBinding.BindInput(_decoderEncMaskName, encMaskOv);
            firstBinding.BindOutputToDevice("logits", _cpuMemInfo);
            foreach (string outName in _decoderFirstSession.OutputMetadata.Keys)
                if (outName.StartsWith("present.", StringComparison.Ordinal))
                    firstBinding.BindOutputToDevice(outName, _cudaMemInfo);

            _decoderFirstSession.RunWithBinding(runOpts, firstBinding);
            firstBinding.SynchronizeBoundOutputs();

            // Don't `using` the collection — the OrtValues we extract into kvCaches
            // are kept alive by firstBinding for the rest of the generation.
            // Disposing the collection wrapper might also dispose the contained
            // OrtValue handles, which would invalidate our kvCaches entries.
            IDisposableReadOnlyCollection<OrtValue> firstOutputs = firstBinding.GetOutputValues();
            IReadOnlyList<string> firstOutputNames = firstBinding.GetOutputNames();

            int firstLogitsIdx = IndexOfLogits(firstOutputNames);
            nextTokens = SampleTopKFromOrtValue(firstOutputs[firstLogitsIdx]);
            allTokens = [nextTokens];

            // Map first-step present.* outputs into kvCaches by their past_key_values.* name.
            for (int i = 0; i < firstOutputNames.Count; i++)
            {
                string n = firstOutputNames[i];
                if (!n.StartsWith("present.", StringComparison.Ordinal)) continue;
                string pastName = PresentToPastName(n);
                if (Array.IndexOf(_pastInputNames, pastName) >= 0)
                    kvCaches[pastName] = firstOutputs[i];
            }
            foreach (string pastName in _pastInputNames)
                if (!kvCaches.ContainsKey(pastName))
                    throw new InvalidOperationException(
                        $"MusicGen first-step output is missing the present.* counterpart of '{pastName}'.");

            // --- Autoregressive loop ---
            for (int step = 1; step < _maxNewTokens; step++)
            {
                ct.ThrowIfCancellationRequested();

                OrtIoBinding stepBinding = _decoderPastSession.CreateIoBinding();
                bool ownStepBinding = true;

                try
                {
                    // Per-step input_ids (small CPU OrtValue; lives only for this Run).
                    long[] stepIdsShape = [_numCodebooks, 1];
                    using OrtValue stepIdsOv = OrtValue.CreateTensorValueFromMemory(
                        _cpuMemInfo, nextTokens.AsMemory(), stepIdsShape);

                    stepBinding.BindInput(_decoderInputIdsName, stepIdsOv);
                    stepBinding.BindInput(_decoderEncMaskName, encMaskOv);

                    foreach (string pastName in _pastInputNames)
                        stepBinding.BindInput(pastName, kvCaches[pastName]);

                    stepBinding.BindOutputToDevice("logits", _cpuMemInfo);
                    foreach (string outName in _decoderPastSession.OutputMetadata.Keys)
                        if (outName.StartsWith("present.", StringComparison.Ordinal))
                            stepBinding.BindOutputToDevice(outName, _cudaMemInfo);

                    _decoderPastSession.RunWithBinding(runOpts, stepBinding);
                    stepBinding.SynchronizeBoundOutputs();

                    // Same reasoning as in the first-step block: don't dispose the
                    // collection wrapper — kvCaches takes references to OrtValues
                    // that stepBinding (now prevPastBinding) owns.
                    IDisposableReadOnlyCollection<OrtValue> stepOutputs = stepBinding.GetOutputValues();
                    IReadOnlyList<string> stepOutputNames = stepBinding.GetOutputNames();

                    int logitsIdx = IndexOfLogits(stepOutputNames);
                    nextTokens = SampleTopKFromOrtValue(stepOutputs[logitsIdx]);
                    allTokens.Add(nextTokens);

                    // Replace the kvCaches entries whose present.* counterpart is in this
                    // step's outputs. Encoder.* slots (which decoder_with_past doesn't
                    // re-emit) keep pointing at firstBinding's OrtValues — those remain
                    // valid because firstBinding is still alive.
                    for (int i = 0; i < stepOutputNames.Count; i++)
                    {
                        string n = stepOutputNames[i];
                        if (!n.StartsWith("present.", StringComparison.Ordinal)) continue;
                        string pastName = PresentToPastName(n);
                        if (Array.IndexOf(_pastInputNames, pastName) >= 0)
                            kvCaches[pastName] = stepOutputs[i];
                    }

                    // kvCaches no longer references prevPastBinding's outputs — safe
                    // to dispose. firstBinding is preserved (still owns encoder KV).
                    prevPastBinding?.Dispose();
                    prevPastBinding = stepBinding;
                    ownStepBinding = false;
                }
                finally
                {
                    if (ownStepBinding) stepBinding.Dispose();
                }
            }
        }
        finally
        {
            prevPastBinding?.Dispose();
            firstBinding.Dispose();
        }

        // --- 6. Undelay tokens for EnCodec ---
        // The decoder produced [num_codebooks, totalSteps] tokens following the
        // delay pattern: codebook k's real audio is at positions [k, k+L). To
        // align all codebooks at length L = totalSteps - num_codebooks + 1,
        // codebook k's i-th aligned token comes from generation step k+i.
        int totalSteps = allTokens.Count;
        int alignedLen = totalSteps - _numCodebooks + 1;
        if (alignedLen <= 0)
            throw new InvalidOperationException(
                $"MusicGen needs at least {_numCodebooks} generation steps for delay alignment; " +
                $"got {totalSteps}.");

        long[] audioTokens = new long[_numCodebooks * alignedLen];
        for (int cb = 0; cb < _numCodebooks; cb++)
            for (int i = 0; i < alignedLen; i++)
                audioTokens[cb * alignedLen + i] = allTokens[cb + i][cb];

        // EnCodec's exported decoder expects rank-4 [num_chunks, batch, num_codebooks,
        // seq_len]. MusicGen always uses a single chunk, so num_chunks = batch = 1.
        DenseTensor<long> audioCodesTensor = new(audioTokens, [1, 1, _numCodebooks, alignedLen]);

        // --- 7. Decode audio tokens to PCM waveform ---
        float[] audioSamples;
        {
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> decOut =
                _encodecSession.Run([
                    NamedOnnxValue.CreateFromTensor("audio_codes", audioCodesTensor),
                ]);
            audioSamples = OnnxTensorConversion.ToFloatTensor(
                decOut.First(o => o.Name == "audio_values")).Buffer.Span.ToArray();
        }

        return ValueRef.FromBytes(DataKind.Audio, BuildWav(audioSamples, _sampleRate));
    }

    /// <summary>
    /// Top-k (default 250) multinomial sampling at temperature 1.0 over the
    /// last time-step's logits, applied per codebook. Reads directly from the
    /// CPU-bound <see cref="OrtValue"/> produced by <c>BindOutputToDevice("logits", cpuMem)</c>
    /// — no managed copy. Logits shape: [num_codebooks, seq_len, vocab_size].
    /// </summary>
    private long[] SampleTopKFromOrtValue(OrtValue logits, int topK = 250, float temperature = 1.0f)
    {
        ReadOnlySpan<long> dims = logits.GetTensorTypeAndShape().Shape;
        int numCb = (int)dims[0];
        int seqLen = (int)dims[1];
        int vocabSize = (int)dims[2];
        int lastStep = seqLen - 1;

        ReadOnlySpan<float> data = logits.GetTensorDataAsSpan<float>();

        long[] result = new long[numCb];
        for (int cb = 0; cb < numCb; cb++)
        {
            int offset = (cb * seqLen + lastStep) * vocabSize;
            ReadOnlySpan<float> slice = data.Slice(offset, vocabSize);
            result[cb] = SampleOneTopK(slice, topK, temperature);
        }
        return result;
    }

    /// <summary>
    /// Returns the index of the "logits" entry in <paramref name="names"/>,
    /// throwing if absent.
    /// </summary>
    private static int IndexOfLogits(IReadOnlyList<string> names)
    {
        for (int i = 0; i < names.Count; i++)
            if (names[i] == "logits") return i;
        throw new InvalidOperationException(
            $"Decoder session output is missing 'logits'. Available: [{string.Join(", ", names)}].");
    }

    /// <summary>
    /// Samples one index from <paramref name="logits"/> via top-k filtering
    /// followed by softmax + multinomial draw. Allocates 2×k floats.
    /// </summary>
    private long SampleOneTopK(ReadOnlySpan<float> logits, int topK, float temperature)
    {
        int k = Math.Min(topK, logits.Length);

        // Maintain a small "min-heap-by-tracking-min" of the top-k logits
        // seen so far. For k=250 over a 2048-vocab, scanning is cheap.
        int[] topIdx = new int[k];
        float[] topVal = new float[k];
        for (int i = 0; i < k; i++)
        {
            topIdx[i] = i;
            topVal[i] = logits[i] / temperature;
        }

        int minSlot = 0;
        float minVal = topVal[0];
        for (int i = 1; i < k; i++)
            if (topVal[i] < minVal) { minVal = topVal[i]; minSlot = i; }

        for (int i = k; i < logits.Length; i++)
        {
            float scaled = logits[i] / temperature;
            if (scaled <= minVal) continue;
            topIdx[minSlot] = i;
            topVal[minSlot] = scaled;
            // Rescan for the new min slot.
            minVal = topVal[0];
            minSlot = 0;
            for (int j = 1; j < k; j++)
                if (topVal[j] < minVal) { minVal = topVal[j]; minSlot = j; }
        }

        // Softmax over the top-k (subtract max for numerical stability).
        float maxVal = topVal[0];
        for (int i = 1; i < k; i++)
            if (topVal[i] > maxVal) maxVal = topVal[i];

        float sumExp = 0f;
        for (int i = 0; i < k; i++)
        {
            topVal[i] = MathF.Exp(topVal[i] - maxVal);
            sumExp += topVal[i];
        }

        // Multinomial draw.
        double r = _rng.NextDouble() * sumExp;
        double cum = 0;
        for (int i = 0; i < k; i++)
        {
            cum += topVal[i];
            if (r <= cum) return topIdx[i];
        }
        return topIdx[k - 1];
    }

    /// <summary>
    /// Converts float32 PCM samples in [-1, 1] to a 16-bit mono WAV byte array.
    /// </summary>
    private static byte[] BuildWav(float[] samples, int sampleRate)
    {
        int dataSize = samples.Length * 2; // int16 = 2 bytes
        byte[] wav = new byte[44 + dataSize];
        using MemoryStream ms = new(wav);
        using BinaryWriter bw = new(ms);

        bw.Write("RIFF"u8);
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);             // PCM format
        bw.Write((short)1);             // mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);       // byte rate
        bw.Write((short)2);             // block align
        bw.Write((short)16);            // bits per sample
        bw.Write("data"u8);
        bw.Write(dataSize);

        for (int i = 0; i < samples.Length; i++)
            bw.Write((short)(Math.Clamp(samples[i], -1f, 1f) * 32767f));

        return wav;
    }

    public void Dispose()
    {
        _textEncoderSession.Dispose();
        _decoderFirstSession.Dispose();
        _decoderPastSession.Dispose();
        _encodecSession.Dispose();
    }

    /// <summary>
    /// Minimal T5 SentencePiece tokenizer loaded from a HuggingFace
    /// <c>tokenizer.json</c> file. Uses greedy longest-match segmentation
    /// (not Viterbi) over the Unigram vocabulary — accurate enough for
    /// short English music prompts where most words appear as full tokens.
    /// Appends EOS (<c>&lt;/s&gt;</c>) automatically, matching T5's default
    /// post-processing.
    /// </summary>
    private sealed class T5Tokenizer
    {
        private readonly Dictionary<string, int> _vocab;
        private readonly int _eosId;
        private readonly int _unkId;

        private T5Tokenizer(Dictionary<string, int> vocab, int eosId, int unkId)
        {
            _vocab = vocab;
            _eosId = eosId;
            _unkId = unkId;
        }

        public static T5Tokenizer FromTokenizerJson(string path)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement model = doc.RootElement.GetProperty("model");

            // Vocab is an array of [token_string, score] pairs.
            var vocab = new Dictionary<string, int>();
            int idx = 0;
            foreach (JsonElement pair in model.GetProperty("vocab").EnumerateArray())
                vocab[pair[0].GetString()!] = idx++;

            int unkId = model.TryGetProperty("unk_id", out JsonElement u) ? u.GetInt32() : 2;
            vocab.TryGetValue("</s>", out int eosId);
            return new T5Tokenizer(vocab, eosId, unkId);
        }

        /// <summary>
        /// Encodes <paramref name="text"/> to T5 token ids with trailing EOS.
        /// </summary>
        public long[] Encode(string text)
        {
            // SentencePiece convention: prepend '▁' to the start and replace
            // spaces with '▁'. This matches T5TokenizerFast's behaviour.
            string normalized = "▁" + text.Trim().Replace(" ", "▁");

            var ids = new List<int>();
            int pos = 0;
            while (pos < normalized.Length)
            {
                // Greedy longest match — scan from max possible length down to 1.
                int remaining = normalized.Length - pos;
                int bestLen = 0;
                int bestId = _unkId;

                for (int len = Math.Min(remaining, 64); len >= 1; len--)
                {
                    if (_vocab.TryGetValue(normalized.Substring(pos, len), out int id))
                    {
                        bestLen = len;
                        bestId = id;
                        break;
                    }
                }

                ids.Add(bestLen > 0 ? bestId : _unkId);
                pos += bestLen > 0 ? bestLen : 1;
            }

            ids.Add(_eosId);
            return ids.Select(i => (long)i).ToArray();
        }
    }
}

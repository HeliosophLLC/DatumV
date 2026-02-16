using System.Text;

using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntimeGenAI;

// `OnnxRuntimeGenAI.Model` collides with `DatumIngest.Model`.
using OgaModel = Microsoft.ML.OnnxRuntimeGenAI.Model;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// Microsoft Phi-3.5-vision via the ONNX Runtime GenAI managed runtime —
/// vision encoder + Phi-3 decoder, packaged in Microsoft's GenAI-bundle
/// format with <c>genai_config.json</c> declaring the multi-session
/// orchestration. SQL surface:
/// <c>models.phi35_vision(image, prompt) → string</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why ORT GenAI rather than hand-rolled ORT.</strong>
/// Decoder-only generative inference at batch=1 is dominated by the
/// per-token KV-cache movement between GPU and CPU. The naive
/// "<c>ReadCacheFloats → DenseTensor → CreateAutoCastInput</c>" pattern
/// (used by <see cref="TrOcrModel"/>, <see cref="Moondream2Model"/>,
/// and <c>PaliGemmaModel</c>) round-trips the entire growing KV cache
/// through managed memory every step — at ~3-4s per token on a modern
/// GPU vs the ~20-50ms the same hardware can deliver with proper
/// IO binding. ORT GenAI handles IO binding, KV-cache management, and
/// sampling internally, keeping caches GPU-resident across the whole
/// generative loop. ~50-200× faster end-to-end on a typical 2B-param
/// VLM.
/// </para>
/// <para>
/// <strong>Bundle format.</strong> The Microsoft GenAI export ships a
/// directory containing <c>genai_config.json</c> plus three ONNX bundles
/// (vision, embedding, text decoder) and tokenizer files. Construction
/// loads the directory as a single unit; the catalog entry anchors on
/// <c>genai_config.json</c>.
/// </para>
/// <para>
/// <strong>Prompt template.</strong> Phi-3.5-vision expects
/// <c>&lt;|user|&gt;\n&lt;|image_1|&gt;\n{prompt}&lt;|end|&gt;\n&lt;|assistant|&gt;\n</c>.
/// The <c>&lt;|image_1|&gt;</c> placeholder marks the splice point for
/// the multimodal processor; ORT GenAI handles the actual image-token
/// insertion internally.
/// </para>
/// <para>
/// <strong>Image input.</strong> ORT GenAI's <see cref="Images"/> loader
/// reads from file paths. We materialise the row's encoded bytes
/// (<c>ValueRef.AsBytes()</c>) to a temp file per inference and delete
/// it after; the disk-write overhead is rounding error against the
/// generation cost.
/// </para>
/// <para>
/// <strong>Determinism.</strong> The bundled <c>genai_config.json</c>
/// pins <c>do_sample: false</c> + <c>top_k: 1</c> — pure greedy decode,
/// reproducible per (image, prompt). Registered as deterministic so the
/// planner CSE-folds equal call sites.
/// </para>
/// </remarks>
public sealed class Phi35VisionModel : IModel, IDisposable
{
    /// <summary>
    /// Headroom for the input portion of the sequence. Phi-3.5-vision's
    /// high-res mode can emit up to ~2500 image tokens for a single
    /// image (16 crops × 144 + overhead); 4096 leaves space for the
    /// chat-template wrapper and a normal user prompt without forcing
    /// callers to think about tokenized image length.
    /// </summary>
    private const int InputTokenBudget = 4096;

    /// <summary>
    /// Phi-3.5-vision end-of-turn tokens. The model is trained to emit
    /// <c>&lt;|endoftext|&gt;</c> (32000) to terminate an assistant turn,
    /// but Microsoft's GenAI bundles have shipped <c>genai_config.json</c>
    /// with <c>eos_token_id</c> swapped against <c>pad_token_id</c> — so
    /// ORT GenAI's <c>IsDone()</c> doesn't fire on the real EOS, and
    /// generation runs hundreds of tokens past end-of-turn into the KV
    /// cache's residual state (producing phantom continuations of
    /// previous rows' content). Stop manually on both turn-end tokens to
    /// stay correct regardless of bundle-config drift.
    /// </summary>
    private const int EndOfTextTokenId = 32000;
    private const int EndOfTurnTokenId = 32007;

    private readonly OgaModel _model;
    private readonly Tokenizer _tokenizer;
    private readonly int _maxTokens;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsDeterministic => true;

    /// <inheritdoc />
    public IReadOnlyList<DataKind> InputKinds { get; } =
        [DataKind.Image, DataKind.String];

    /// <inheritdoc />
    public DataKind OutputKind => DataKind.String;

    /// <inheritdoc />
    /// <remarks>
    /// VLM generation is per-row by nature (each image+prompt has its
    /// own KV cache). Streaming size 1 emits each row's answer as soon
    /// as it's done rather than waiting on the full upstream batch.
    /// </remarks>
    public int? PreferredBatchSize => 1;

    /// <summary>
    /// Loads Phi-3.5-vision from a GenAI-format bundle directory.
    /// </summary>
    /// <param name="name">Catalog-visible name.</param>
    /// <param name="modelDirectory">
    /// Absolute path to the directory containing <c>genai_config.json</c>
    /// (e.g. <c>{$DATUM_MODELS}/phi35-vision-onnx/gpu/gpu-int4-rtn-block-32/</c>).
    /// </param>
    /// <param name="maxTokens">
    /// Cap on generated tokens per prompt. 256 is generous for
    /// descriptions and short Q&amp;A; raise for long-form summarisation.
    /// </param>
    public Phi35VisionModel(string name, string modelDirectory, int maxTokens = 256)
    {
        Name = name;
        _maxTokens = maxTokens;

        if (!File.Exists(Path.Combine(modelDirectory, "genai_config.json")))
        {
            throw new FileNotFoundException(
                "Phi-3.5-vision GenAI bundle missing 'genai_config.json'. " +
                "Expected a Microsoft GenAI-format export directory; download " +
                "from microsoft/Phi-3.5-vision-instruct-onnx (gpu/gpu-int4-rtn-block-32 " +
                "for CUDA, cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4 for CPU).",
                Path.Combine(modelDirectory, "genai_config.json"));
        }

        // Microsoft's gpu-* GenAI bundles ship genai_config.json with empty
        // `provider_options: []` — that selects the CPU EP at load time
        // regardless of the folder name. CUDA must be opted in explicitly
        // via Config.AppendProvider before constructing the Model. Without
        // this, Phi-3.5-vision runs on CPU at ~2-4s per token despite the
        // .Cuda runtime package being installed.
        using Config config = new(modelDirectory);
        config.AppendProvider("cuda");
        _model = new OgaModel(config);
        _tokenizer = new Tokenizer(_model);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        CancellationToken cancellationToken)
    {
        _ = overrides;
        cancellationToken.ThrowIfCancellationRequested();
        if (inputs.Count == 0) return Task.FromResult<IReadOnlyList<ValueRef>>([]);

        return Task.Run<IReadOnlyList<ValueRef>>(() =>
        {
            ValueRef[] results = new ValueRef[inputs.Count];

            for (int row = 0; row < inputs.Count; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<ValueRef> rowInputs = inputs[row];
                if (rowInputs.Count < 2)
                {
                    throw new InvalidOperationException(
                        $"Phi35VisionModel expects 2 inputs (image, prompt); row {row} has {rowInputs.Count}.");
                }

                ValueRef image = rowInputs[0];
                ValueRef prompt = rowInputs[1];
                if (image.IsNull)
                {
                    throw new InvalidOperationException(
                        $"Phi35VisionModel received a null image at row {row}; filter nulls upstream.");
                }
                string promptText = prompt.IsNull ? "Describe this image." : prompt.AsString();

                results[row] = ValueRef.FromString(
                    GenerateOne(image.AsBytes(), promptText, cancellationToken));
            }

            return results;
        }, cancellationToken);
    }

    private string GenerateOne(byte[] imageBytes, string userPrompt, CancellationToken cancellationToken)
    {
        // ORT GenAI's Images loader reads from file paths. Stage the
        // encoded bytes in a temp file and clean up immediately after.
        // The PNG/JPEG decode happens inside the processor (DecodeImage
        // op in processor_config.json).
        string tempPath = Path.Combine(
            Path.GetTempPath(),
            $"phi35vision-{Guid.NewGuid():N}.img");
        File.WriteAllBytes(tempPath, imageBytes);

        try
        {
            string formattedPrompt =
                $"<|user|>\n<|image_1|>\n{userPrompt}<|end|>\n<|assistant|>\n";

            // MultiModalProcessor is per-call rather than shared. In ORT
            // GenAI 0.5.x the processor retains internal scratch state
            // across ProcessImages calls; reusing one instance across
            // rows produces malformed image features for the first
            // several invocations (visible as the decoder emitting only
            // <unk> / no-token sentinels for the first 3-4 rows before
            // outputs stabilise). The processor is cheap to construct
            // — the cost is dwarfed by the per-row generation budget.
            using MultiModalProcessor processor = new(_model);
            using Images images = Images.Load([tempPath]);
            using NamedTensors processed = processor.ProcessImages(formattedPrompt, images);

            using GeneratorParams generatorParams = new(_model);
            // ORT GenAI's `max_length` caps total sequence length (input
            // tokens + generated tokens), not new-tokens budget. Phi-3.5-
            // vision in high-resolution mode emits up to ~2500 image
            // tokens for a single picture (16 crops × 144 + overhead);
            // the InputBudget headroom keeps single-image prompts safe
            // while preserving the user's "maxTokens new tokens" intent.
            generatorParams.SetSearchOption("max_length", InputTokenBudget + _maxTokens);

            using Generator generator = new(_model, generatorParams);
            // GenAI 0.6.x moved processed-input binding from GeneratorParams
            // to Generator. The processor's NamedTensors are now applied
            // after the generator is constructed.
            generator.SetInputs(processed);

            // Stream new tokens through TokenizerStream so byte-level BPE
            // (Unicode pieces split across token boundaries) decodes
            // cleanly without manual reassembly. 0.6.x unified the
            // previously-separate ComputeLogits() step into
            // GenerateNextToken().
            //
            // Decode only the newest token (sequence[^1]) per step. The
            // sequence at iteration 1 already contains every prompt
            // token (chat-template wrappers + ~1.9-2.5k image-placeholder
            // tokens + the user prompt); decoding from index 0 leaks the
            // user's prompt back into the answer and pumps thousands of
            // special tokens through TokenizerStream's UTF-8 byte buffer
            // before any real generation begins.
            //
            // Stop on both <|endoftext|> (32000) and <|end|> (32007). Phi-
            // 3.5-vision is trained to terminate assistant turns with
            // 32000, but Microsoft's GenAI bundle ships eos_token_id=32007
            // (mismatched against pad_token_id=32000). Without an explicit
            // check here, IsDone() runs hundreds of tokens past EOS into
            // the KV cache's residual state, producing phantom
            // continuations that look like content from previous rows.
            using TokenizerStream tokenizerStream = _tokenizer.CreateStream();
            StringBuilder answer = new();

            while (!generator.IsDone())
            {
                cancellationToken.ThrowIfCancellationRequested();
                generator.GenerateNextToken();
                int newToken = generator.GetSequence(0)[^1];
                if (newToken == EndOfTextTokenId || newToken == EndOfTurnTokenId)
                {
                    break;
                }
                answer.Append(tokenizerStream.Decode(newToken));
            }

            return answer.ToString().Trim();
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _tokenizer.Dispose();
        _model.Dispose();
    }
}

using System.Runtime.CompilerServices;
using System.Text;

using DatumIngest.Functions;
using DatumIngest.Model;

using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace DatumIngest.Models.Llama;

/// <summary>
/// LlamaSharp-backed <see cref="IModel"/> for one-shot LLM inference. Loads a
/// GGUF model file at construction, runs a <see cref="StatelessExecutor"/> per
/// row (no chat history retention across rows), and returns the assistant's
/// response as a single <see cref="DataKind.String"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Streaming.</strong> The primary inference path is
/// <see cref="InferStreamingAsync"/>, which yields tokens (as
/// <see cref="ValueRef"/> chunks) in the order LlamaSharp emits them.
/// <see cref="InferBatchAsync"/> is implemented in terms of it — it collects
/// chunks into a single string per row before returning. This means
/// SQL <c>SELECT</c> and <c>CALL</c> share one inference path; the only
/// difference is whether the consumer collects or forwards chunks to a
/// streaming sink.
/// </para>
/// <para>
/// <strong>Chat templating.</strong> Llama 3.1 Instruct expects a specific
/// header-token format around system/user/assistant messages. Inputs are
/// wrapped in that template before dispatch, and the assistant's response is
/// returned with the trailing stop token (<c>&lt;|eot_id|&gt;</c>) stripped.
/// </para>
/// <para>
/// <strong>Determinism.</strong> Inference samples with temperature, so the
/// model is registered as nondeterministic — the planner won't CSE-fold two
/// textually-identical call sites with the same input. (Same AST node still
/// resolves to one evaluation, per the CSE-correctness invariant.)
/// </para>
/// <para>
/// <strong>Batching.</strong> LLMs maintain per-request KV cache, so true
/// cross-row batching needs an external dispatcher (Demo 5+). For Demo 1.5
/// each row dispatches serially through the executor — fine for demo cadence,
/// not for throughput.
/// </para>
/// </remarks>
public sealed class LlamaModel : IModel, IDisposable
{
    private readonly LLamaWeights _weights;
    private readonly ModelParams _modelParams;
    private readonly StatelessExecutor _executor;
    private readonly LlamaChatTemplate _template;
    private readonly int _maxTokens;
    private readonly float _temperature;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsDeterministic => false;

    /// <inheritdoc />
    public IReadOnlyList<DataKind> InputKinds { get; } = [DataKind.String];

    /// <inheritdoc />
    public DataKind OutputKind => DataKind.String;

    /// <inheritdoc />
    /// <remarks>
    /// LLMs typically take 2–5 seconds per generation on a consumer GPU.
    /// A 1024-row upstream batch would mean ~hour-long waits before the
    /// user sees a single result. Streaming in groups of 4 keeps total
    /// throughput nearly identical (each group is run with one
    /// <c>InferBatchAsync</c> call) while delivering first results in
    /// seconds.
    /// </remarks>
    public int? PreferredBatchSize => 4;

    /// <summary>
    /// Loads the GGUF file at <paramref name="modelFilePath"/> with all layers
    /// offloaded to GPU when available (LlamaSharp falls back to CPU when no
    /// supported GPU backend is present).
    /// </summary>
    /// <param name="name">Catalog-visible name (the <c>"llama31_8b"</c> in <c>models.llama31_8b</c>).</param>
    /// <param name="modelFilePath">Absolute path to the <c>.gguf</c> file.</param>
    /// <param name="template">
    /// Chat template to wrap user messages with, plus the stop-sequence
    /// vocabulary that ends generation. Defaults to
    /// <see cref="LlamaChatTemplate.Llama31"/>; use
    /// <see cref="LlamaChatTemplate.Phi3"/> for Phi-3-family GGUFs and
    /// construct a custom <see cref="LlamaChatTemplate"/> for any other family.
    /// </param>
    /// <param name="contextSize">Token context window. Defaults to 4096 — fits typical demo prompts plus generation.</param>
    /// <param name="maxTokens">Max new tokens to generate per call. Defaults to 256.</param>
    /// <param name="temperature">Sampling temperature. Defaults to 0.7.</param>
    public LlamaModel(
        string name,
        string modelFilePath,
        LlamaChatTemplate? template = null,
        uint contextSize = 4096,
        int maxTokens = 256,
        float temperature = 0.7f)
    {
        if (!File.Exists(modelFilePath))
        {
            throw new FileNotFoundException(
                $"GGUF model file not found at '{modelFilePath}'. Confirm the catalog's ModelDirectory and the entry's RelativePath resolve to a real file.",
                modelFilePath);
        }

        Name = name;
        _template = template ?? LlamaChatTemplate.Llama31;
        _maxTokens = maxTokens;
        _temperature = temperature;

        try
        {
            LlamaNativeConfig.EnsureConfigured();

            _modelParams = new ModelParams(modelFilePath)
            {
                ContextSize = contextSize,
                // 999 is "all layers on GPU" for any single-card GGUF — llama.cpp
                // caps to the model's actual layer count so this is safe.
                GpuLayerCount = 999,
            };

            _weights = LLamaWeights.LoadFromFile(_modelParams);
            _executor = new StatelessExecutor(_weights, _modelParams);
        }
        catch (TypeInitializationException ex)
        {
            // .NET wraps native-load failures in TypeInitializationException.
            // Walk the InnerException chain to get to the actual cause; the
            // outermost TIE message ("Type initializer for X threw an
            // exception") is useless on its own.
            Exception root = ex;
            while (root.InnerException is not null) root = root.InnerException;

            throw new InvalidOperationException(
                $"LlamaSharp native libraries failed to initialize. Root cause: {root.GetType().Name}: {root.Message}",
                ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Collects over <see cref="InferStreamingAsync"/> per row, concatenates
    /// the chunks, and trims trailing whitespace. Every <c>SELECT</c> against
    /// the LLM goes through the streaming path internally — the streaming
    /// code is exercised even when the consumer only wants the final string.
    /// </remarks>
    public async Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (inputs.Count == 0)
        {
            return [];
        }

        ValueRef[] outputs = new ValueRef[inputs.Count];
        for (int row = 0; row < inputs.Count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<ValueRef> rowOverrides = overrides.Count > row
                ? overrides[row]
                : [];

            StringBuilder sb = new();
            await foreach (ValueRef chunk in InferStreamingAsync(inputs[row], rowOverrides, cancellationToken).ConfigureAwait(false))
            {
                sb.Append(chunk.AsString());
            }

            // Trim outer whitespace to match historical scalar semantics —
            // tokens often arrive with a leading space and the user expects
            // a clean string when binding the result into a row.
            outputs[row] = ValueRef.FromString(sb.ToString().Trim());
        }

        return outputs;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Per-row streaming. Yielded chunks are slices of the assistant response
    /// in token-arrival order, with stop sequences stripped before they reach
    /// the consumer (see holdback note below). Chunks may span multiple
    /// LlamaSharp tokens — the implementation holds back the longest stop
    /// sequence's worth of characters so a partial stop marker can't leak
    /// out as a visible chunk.
    /// </para>
    /// <para>
    /// <strong>Holdback granularity.</strong> The consumer sees content
    /// roughly <c>holdback</c> characters behind the underlying token
    /// stream (10 chars for Llama 3.1's <c>&lt;|eot_id|&gt;</c> stop). For
    /// a 256-token response this delay is invisible; what the user perceives
    /// is "tokens stream live."
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<ValueRef> InferStreamingAsync(
        IReadOnlyList<ValueRef> rowInputs,
        IReadOnlyList<ValueRef> rowOverrides,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (rowInputs.Count != 1)
        {
            throw new InvalidOperationException(
                $"LlamaModel expects exactly one input column per row but received {rowInputs.Count}.");
        }

        ValueRef prompt = rowInputs[0];
        if (prompt.IsNull)
        {
            throw new InvalidOperationException(
                "LlamaModel received a null prompt; filter nulls upstream before invoking the model.");
        }

        // Resolve per-row hyperparameters from the override slice.
        // Order matches the catalog entry's OptionalArgKinds:
        //   [0] = temperature (Float64)
        //   [1] = max_tokens   (Int32)
        // Missing or null entries fall back to construction-time defaults.
        float temperature = rowOverrides.Count > 0 && !rowOverrides[0].IsNull
            ? rowOverrides[0].ToFloat()
            : _temperature;
        int maxTokens = rowOverrides.Count > 1 && !rowOverrides[1].IsNull
            ? rowOverrides[1].ToInt32()
            : _maxTokens;

        string templated = _template.Format(prompt.AsString());

        // No manual KV-cache reset: StatelessExecutor.Context is only valid
        // *during* InferAsync — the executor builds a fresh context per call
        // and disposes it after. Touching Context.NativeHandle from outside
        // that window throws ObjectDisposedException. Each InferAsync already
        // starts from an empty KV cache.

        // Fresh seed per call so identical prompts can still produce different
        // outputs across calls — and so non-identical prompts never accidentally
        // share a sampling trajectory because the seed defaulted to 0.
        InferenceParams inferenceParams = new()
        {
            MaxTokens = maxTokens,
            AntiPrompts = [.. _template.StopSequences],
            // Load-bearing: LlamaSharp defaults to suppressing special
            // tokens from the streamed text. With it off, role/turn
            // markers (Phi-3 `<|end|>`, Llama-3.1 `<|eot_id|>`, …) emit
            // as empty strings and the AntiPrompts list never matches —
            // generation runs to MaxTokens emitting hallucinated
            // training-data continuation. Surfacing them as text lets
            // both AntiPrompts AND the snapshot search below catch them;
            // the slice at `stopAt` strips the marker before yielding to
            // the consumer, so the user-visible response stays clean.
            DecodeSpecialTokens = true,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = temperature,
                Seed = (uint)Random.Shared.Next(),
            },
        };

        // Hold back the longest stop sequence's worth of characters: the
        // tail of the buffer might be a partial stop marker that completes
        // on the next token. AntiPrompts usually catches stops mid-stream,
        // but defensively we never emit a chunk that could later be revealed
        // as part of a stop sequence.
        int holdback = 0;
        foreach (string stop in _template.StopSequences)
        {
            if (stop.Length > holdback) holdback = stop.Length;
        }

        StringBuilder pending = new();

        await foreach (string token in _executor.InferAsync(templated, inferenceParams, cancellationToken).ConfigureAwait(false))
        {
            pending.Append(token);

            // If a complete stop sequence has appeared, yield everything
            // before it and end the stream.
            int stopAt = -1;
            string pendingSnapshot = pending.ToString();
            foreach (string stop in _template.StopSequences)
            {
                int idx = pendingSnapshot.IndexOf(stop, StringComparison.Ordinal);
                if (idx >= 0 && (stopAt < 0 || idx < stopAt))
                {
                    stopAt = idx;
                }
            }
            if (stopAt >= 0)
            {
                if (stopAt > 0)
                {
                    yield return ValueRef.FromString(pendingSnapshot[..stopAt]);
                }
                yield break;
            }

            // No stop hit: emit everything except the final `holdback`
            // chars (which might be the start of a stop sequence completed
            // by the next token).
            int safe = pending.Length - holdback;
            if (safe > 0)
            {
                yield return ValueRef.FromString(pending.ToString(0, safe));
                pending.Remove(0, safe);
            }
        }

        // Stream ended without an explicit stop marker. Defensively strip a
        // trailing stop from the residual buffer (mirrors the old
        // post-collection StripTrailingStop) and emit the remainder.
        if (pending.Length > 0)
        {
            string remainder = pending.ToString();
            foreach (string stop in _template.StopSequences)
            {
                int idx = remainder.IndexOf(stop, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    remainder = remainder[..idx];
                    break;
                }
            }
            if (remainder.Length > 0)
            {
                yield return ValueRef.FromString(remainder);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _weights.Dispose();
        GC.SuppressFinalize(this);
    }
}

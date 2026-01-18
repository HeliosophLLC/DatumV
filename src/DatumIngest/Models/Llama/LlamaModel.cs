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
/// SQL <c>SELECT</c> and <c>EXEC</c> share one inference path; the only
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
    private static readonly object _nativeConfigLock = new();
    private static bool _nativeConfigured;

    private readonly LLamaWeights _weights;
    private readonly ModelParams _modelParams;
    private readonly StatelessExecutor _executor;
    private readonly LlamaChatTemplate _template;
    private readonly int _maxTokens;
    private readonly float _temperature;

    /// <summary>
    /// When <see langword="true"/> (the default), require the CUDA backend to load
    /// successfully and refuse silent CPU fallback. Set to <see langword="false"/>
    /// to allow CPU when CUDA isn't available — useful for CI / non-GPU machines.
    /// </summary>
    /// <remarks>
    /// This is process-wide static state because LlamaSharp's
    /// <see cref="NativeLibraryConfig"/> is a singleton: the first
    /// <c>LoadFromFile</c> call locks in the configuration. Subsequent constructors
    /// can't change the choice, so we set it once before the first load.
    /// </remarks>
    public static bool RequireCuda { get; set; } = true;

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
            EnsureNativeConfigured();

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

    /// <summary>
    /// Runs once before the first <see cref="LLamaWeights.LoadFromFile"/> call.
    /// When <see cref="RequireCuda"/> is set, disables LlamaSharp's auto-fallback
    /// so a missing CUDA Runtime surfaces as a clear error rather than a silent
    /// drop to CPU (which is fast to "work" but slow to actually run).
    /// </summary>
    private static void EnsureNativeConfigured()
    {
        lock (_nativeConfigLock)
        {
            if (_nativeConfigured) return;
            // The NativeLibraryConfig singleton locks once any library has loaded;
            // skip configuration if we're already past that point (e.g. another
            // model in this process loaded first).
            if (NativeLibraryConfig.LLama.LibraryHasLoaded)
            {
                _nativeConfigured = true;
                return;
            }

            // Make the CUDA Runtime DLLs reachable via PATH if we can find them
            // somewhere LlamaSharp's loader can see. The CUDA backend NuGet ships
            // ggml-cuda.dll but not the NVIDIA-installed cudart64_12.dll — without
            // this probe, the loader silently falls back to CPU even on a
            // GPU-capable machine when the user hasn't installed the CUDA toolkit
            // system-wide.
            string? cudaDirAdded = null;
            if (RequireCuda)
            {
                (CudaRuntimeProbe.Result outcome, string? dir) = CudaRuntimeProbe.EnsureOnPath();
                if (outcome == CudaRuntimeProbe.Result.NotFound)
                {
                    throw new InvalidOperationException(
                        "CUDA 12 Runtime DLLs (cudart64_12.dll, cublas64_12.dll, cublasLt64_12.dll) " +
                        "are not on the process PATH and weren't found in known bundled locations " +
                        "(Ollama's lib\\ollama\\cuda_v12 or NVIDIA's CUDA Toolkit install). " +
                        "Either install the CUDA 12.x Runtime from NVIDIA, ensure Ollama is installed (its bundled CUDA folder will be detected automatically), " +
                        "or set LlamaModel.RequireCuda = false to allow CPU fallback (significantly slower).");
                }
                cudaDirAdded = dir;
            }

            // Route only Warning+ from LlamaSharp to the console — the lower
            // levels are extremely chatty (per-tensor / per-token traces during
            // load and inference) and drown out actual problems. Bump to a
            // lower threshold here when debugging native-load issues.
            //
            // Continue is a special llama.cpp marker meaning "no newline
            // before me; this is a continuation of the previous log line"
            // (used to print the tensor-loading "..........." dots one at a
            // time). It slips through the >= Warning filter because its
            // numeric value happens to be high; exclude it explicitly.
            NativeLogConfig.LLamaLogCallback logCallback = (level, message) =>
            {
                if (level >= LLamaLogLevel.Warning && level != LLamaLogLevel.Continue)
                {
                    Console.Error.Write($"[llama:{level}] {message}");
                }
            };

            // PreferVulkan is on by default and ranks above CUDA in the
            // selection policy, so without explicitly disabling it the loader
            // tries the (non-existent) Vulkan native folder first and — with
            // AllowFallback off — gives up before reaching CUDA. We don't ship
            // the Vulkan backend, so opt out unconditionally.
            //
            // SkipCheck(true) is also load-bearing: LlamaSharp's pre-flight
            // CUDA viability probe uses lookup paths that don't include our
            // process PATH prepend, so it concludes "no CUDA available" and
            // silently demotes UseCuda to False — pushing the loader to the
            // CPU AVX2 backend even when PreferCuda is True. Skipping the
            // pre-flight tells the policy to trust our preference and just
            // attempt the CUDA load; if cudart isn't actually reachable, the
            // load itself fails loudly (with AutoFallback off).
            // SkipCheck(true) and WithAutoFallback(true) are mutually exclusive
            // in LlamaSharp 0.27 — the loader throws ArgumentException if both
            // are set. We only need SkipCheck when forcing CUDA (where
            // LlamaSharp's pre-flight CUDA-availability probe is unreliable).
            // When fallback is allowed (CPU-OK builds, CI), let the check run.
            NativeLibraryConfig.LLama
                .WithLogCallback(logCallback)
                .WithVulkan(false)
                .WithCuda(true)
                .SkipCheck(RequireCuda)
                .WithAutoFallback(!RequireCuda);

            if (cudaDirAdded is not null)
            {
                Console.Error.WriteLine($"[llama] Using CUDA Runtime from: {cudaDirAdded}");
            }

            _nativeConfigured = true;
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
        //   [2] = templated    (Boolean) — when true, skip Format() so a
        //                       caller that pre-templated via the
        //                       templates.X scalar functions isn't
        //                       double-wrapped.
        // Missing or null entries fall back to construction-time defaults.
        float temperature = rowOverrides.Count > 0 && !rowOverrides[0].IsNull
            ? rowOverrides[0].ToFloat()
            : _temperature;
        int maxTokens = rowOverrides.Count > 1 && !rowOverrides[1].IsNull
            ? rowOverrides[1].ToInt32()
            : _maxTokens;
        bool alreadyTemplated = rowOverrides.Count > 2 && !rowOverrides[2].IsNull
            && rowOverrides[2].AsBoolean();

        string promptText = prompt.AsString();
        string templated = alreadyTemplated ? promptText : _template.Format(promptText);

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

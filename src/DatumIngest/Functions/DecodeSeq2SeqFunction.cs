using DatumIngest.Diagnostics;
using DatumIngest.Execution;
using DatumIngest.Inference;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Greedy autoregressive decoding loop against a bound encoder-decoder
/// session — the SQL primitive that replaces hand-rolled C# generation
/// loops for TrOCR-style, ViT-GPT2-style, and Florence-2-style models.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Surface.</strong>
/// <code>
/// decode_seq2seq(
///     decoder_session         String,      -- session alias bound via USING ... AS x
///     encoder_features        Float32[],   -- flat-packed encoder output
///     encoder_attention_mask  Int64[],     -- NULL when the decoder has no
///                                          --   such input; required when it does
///     prefix_token_ids        Int64[],     -- [decoder_start_token] for simple
///                                          --   shapes; [BOS, ...task, EOS] for
///                                          --   prompt-prefixed decoders (Florence-2)
///     eos_token_id            Int64,
///     max_tokens              Int32,
///     use_kv_cache            Boolean      -- v1: only `false` supported. KV-cache
///                                          --   path lands in a follow-up alongside
///                                          --   TrOCR migration.
/// ) RETURNS Int64[]                        -- generated token ids, in order
/// </code>
/// </para>
/// <para>
/// <strong>Decoder session contract.</strong> The bound decoder session
/// must declare an <c>input_ids</c> input (Int64 [batch, seq]) and an
/// <c>encoder_hidden_states</c> input (Float32 [batch, enc_seq, hidden]).
/// Hidden-dim is read from <c>encoder_hidden_states.Shape[2]</c> (must be
/// concrete); encoder-seq length is computed as
/// <c>encoder_features.Length / hidden_dim</c>. If the session also
/// declares <c>encoder_attention_mask</c>, the caller's
/// <c>encoder_attention_mask</c> argument is wired in (NULL
/// is rejected in that case). The session must produce a <c>logits</c>
/// output of shape <c>[batch, seq, vocab]</c>; argmax over the last
/// position picks the next token.
/// </para>
/// <para>
/// <strong>No-cache loop only (v1).</strong> Every step re-runs the
/// decoder over the full <c>prefix + generated-so-far</c> sequence. This
/// matches ViT-GPT2-Caption's and Florence-2's no-cache export shape and
/// is the cheapest decoding path to get working end-to-end. The KV-cache
/// variant — pass <c>use_kv_cache = true</c> — is reserved here but throws
/// <see cref="NotSupportedException"/> until the TrOCR migration adds it.
/// </para>
/// <para>
/// <strong>Output shape.</strong> Returns only the <em>generated</em>
/// token ids — does not echo back the <c>prefix_token_ids</c>.
/// The loop terminates on <c>eos_token_id</c> or
/// <c>max_tokens</c>, whichever fires first; the EOS token
/// is NOT included in the returned array.
/// </para>
/// </remarks>
public sealed class DecodeSeq2SeqFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "decode_seq2seq";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Greedy autoregressive decoding loop against a named encoder-decoder "
        + "session. Returns the generated token ids (not including the prefix or EOS). "
        + "Replaces hand-rolled C# generation loops for ViT-GPT2 / Florence-2 / TrOCR "
        + "families. v1 supports the no-cache path (use_kv_cache=false); the KV-cache "
        + "path is reserved and lands with TrOCR.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("decoder_session", DataKindMatcher.Exact(DataKind.String),  IsArray: ArrayMatch.Scalar),
                new ParameterSpec("encoder_features", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                // Nullable: a decoder without an encoder_attention_mask input
                // takes NULL here. NULL arrives at plan time as Unknown, which
                // doesn't match an exact-kind+array spec; widen to Any and
                // type-check inside ExecuteAsync. The function's contract
                // (non-NULL must be Int64[]) is enforced at runtime instead.
                new ParameterSpec("encoder_attention_mask", DataKindMatcher.Any),
                new ParameterSpec("prefix_token_ids", DataKindMatcher.Exact(DataKind.Int64), IsArray: ArrayMatch.Array),
                new ParameterSpec("eos_token_id", DataKindMatcher.Exact(DataKind.Int64), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("max_tokens", DataKindMatcher.Exact(DataKind.Int32), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("use_kv_cache", DataKindMatcher.Exact(DataKind.Boolean), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            // Output element kind known statically — token ids.
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Int64))),
    ];

    /// <inheritdoc />
    public static BodyScopeRequirement BodyScope => BodyScopeRequirement.ModelBody;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DecodeSeq2SeqFunction>(argumentKinds);

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args.Length != 7)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq() expects 7 arguments; got {args.Length}.");
        }

        if (frame.CurrentModel is not { } model)
        {
            throw new InvalidOperationException(
                "decode_seq2seq() is only callable from inside a CREATE MODEL body. "
                + "Outside a model frame there is no bound session to dispatch to.");
        }

        // Argument extraction. ValueRef accessors handle inline + arena
        // payloads transparently; primitive arrays come back as their
        // managed counterparts via Materialized.
        if (args[0].IsNull || args[0].Kind != DataKind.String)
        {
            throw new InvalidOperationException(
                "decode_seq2seq(): decoder_session must be a non-NULL String alias.");
        }
        string sessionAlias = args[0].AsString();

        if (!model.BoundSessions.TryGetValue(sessionAlias, out IInferenceSession? decoder))
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): decoder session alias '{sessionAlias}' is not bound. "
                + $"Available aliases: [{string.Join(", ", model.BoundSessions.Keys)}]. "
                + "Aliases come from the CREATE MODEL's USING clause "
                + "(`USING 'path' AS alias`).");
        }

        if (args[1].IsNull)
        {
            throw new InvalidOperationException(
                "decode_seq2seq(): encoder_features must be a non-NULL Float32[].");
        }
        float[] encoderFeatures = ExtractFloat32Array(args[1]);

        // encoder_attention_mask is structurally nullable — caller passes
        // NULL when the decoder session has no such input. The signature
        // accepts Any (the matcher can't represent "Int64[] or NULL");
        // when non-null we enforce the Int64[] contract here.
        long[]? encoderMask;
        if (args[2].IsNull)
        {
            encoderMask = null;
        }
        else
        {
            if (!args[2].IsArray || args[2].ArrayElementKind != DataKind.Int64)
            {
                throw new InvalidOperationException(
                    $"decode_seq2seq(): encoder_attention_mask must be Int64[] or NULL; "
                    + $"got {args[2].Kind}{(args[2].IsArray ? "[]" : "")}.");
            }
            encoderMask = ExtractInt64Array(args[2]);
        }

        if (args[3].IsNull)
        {
            throw new InvalidOperationException(
                "decode_seq2seq(): prefix_token_ids must be non-NULL "
                + "(typically [decoder_start_token] for simple decoders, "
                + "or [BOS, ...task_tokens, EOS] for prefix-conditioned decoders).");
        }
        long[] prefix = ExtractInt64Array(args[3]);
        if (prefix.Length == 0)
        {
            throw new InvalidOperationException(
                "decode_seq2seq(): prefix_token_ids must contain at least one token.");
        }

        long eosTokenId = args[4].AsInt64();
        int maxTokens = args[5].AsInt32();
        if (maxTokens <= 0)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): max_tokens must be positive; got {maxTokens}.");
        }
        bool useKvCache = args[6].AsBoolean();

        // Resolve decoder session input metadata. The decoder must declare
        // at least `input_ids` and `encoder_hidden_states`; `encoder_attention_mask`
        // is optional and only consumed when present.
        TensorSpec? inputIdsSpec = FindInput(decoder, "input_ids");
        TensorSpec? encoderHiddenSpec = FindInput(decoder, "encoder_hidden_states");
        TensorSpec? encoderMaskSpec = FindInput(decoder, "encoder_attention_mask");

        if (inputIdsSpec is null)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): decoder session '{sessionAlias}' has no 'input_ids' input. "
                + "v1 supports decoders that take integer token ids directly. "
                + "Decoders taking `inputs_embeds` (Florence-2, Moondream2) will be "
                + "supported when those models migrate; until then, use the "
                + "C# IModel fallback path.");
        }
        if (encoderHiddenSpec is null)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): decoder session '{sessionAlias}' has no "
                + "'encoder_hidden_states' input. Decoder-only models should use "
                + "the (forthcoming) decode_decoder_only() scalar instead.");
        }

        // Hidden dim must be concrete on the encoder_hidden_states spec —
        // we use it to deduce encoder-seq-length from the flat features
        // array (since the batch dim is 1 and we know the total length).
        if (encoderHiddenSpec.Shape.Count != 3 || encoderHiddenSpec.Shape[2] is not int hiddenDim)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): decoder session '{sessionAlias}' encoder_hidden_states "
                + $"input shape must be [batch, seq, hidden_dim] with a concrete hidden_dim; "
                + $"got [{string.Join(", ", encoderHiddenSpec.Shape.Select(d => d?.ToString() ?? "?"))}].");
        }
        if (hiddenDim <= 0 || encoderFeatures.Length % hiddenDim != 0)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): encoder_features length {encoderFeatures.Length} is not "
                + $"divisible by hidden_dim {hiddenDim} (from decoder session metadata).");
        }
        int encoderSeqLen = encoderFeatures.Length / hiddenDim;

        // Encoder attention mask wiring. If the decoder declares the input,
        // the caller MUST supply it; if not, the caller MUST pass NULL.
        // The two-sided check catches obvious mismatches (right shape but
        // wrong intent) at the function boundary instead of as a confusing
        // ORT error deeper in the dispatch.
        if (encoderMaskSpec is not null && encoderMask is null)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): decoder session '{sessionAlias}' declares an "
                + "'encoder_attention_mask' input but the call passed NULL. Provide a "
                + "matching Int64[] of length encoder_seq_len.");
        }
        if (encoderMaskSpec is null && encoderMask is not null)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): decoder session '{sessionAlias}' has no "
                + "'encoder_attention_mask' input but the call supplied one. Pass NULL "
                + "for decoders that don't consume an encoder mask.");
        }
        if (encoderMask is not null && encoderMask.Length != encoderSeqLen)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): encoder_attention_mask length {encoderMask.Length} "
                + $"does not match encoder_seq_len {encoderSeqLen} "
                + $"(derived from encoder_features.Length / hidden_dim).");
        }

        // Find the output tensor name. Most decoders call it `logits`; we
        // accept the first declared output if `logits` isn't present so a
        // model that uses a different name doesn't force a rename.
        if (decoder.Outputs.Count == 0)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): decoder session '{sessionAlias}' declares no outputs.");
        }
        TensorSpec logitsSpec = decoder.Outputs.FirstOrDefault(o =>
            string.Equals(o.Name, "logits", StringComparison.Ordinal))
            ?? decoder.Outputs[0];

        int[] encoderShape = [1, encoderSeqLen, hiddenDim];
        int[]? encoderMaskShape = encoderMask is null ? null : new[] { 1, encoderSeqLen };

        DatumActivity.Scalars.Trace(
            $"[decode_seq2seq] {model.QualifiedName}#{sessionAlias}: "
            + $"prefix.Length={prefix.Length} max_tokens={maxTokens} eos={eosTokenId} "
            + $"encoder=[{encoderSeqLen},{hiddenDim}] mask={(encoderMask is null ? "null" : encoderMask.Length.ToString())} "
            + $"use_kv_cache={useKvCache}");

        long[] result = useKvCache
            ? await GenerateWithKvCacheAsync(
                decoder, inputIdsSpec, encoderHiddenSpec, encoderMaskSpec, logitsSpec,
                prefix, eosTokenId, maxTokens,
                encoderFeatures, encoderShape, encoderMask, encoderMaskShape,
                sessionAlias, cancellationToken).ConfigureAwait(false)
            : await GenerateNoCacheAsync(
                decoder, inputIdsSpec, encoderHiddenSpec, encoderMaskSpec, logitsSpec,
                prefix, eosTokenId, maxTokens,
                encoderFeatures, encoderShape, encoderMask, encoderMaskShape,
                cancellationToken).ConfigureAwait(false);

        DatumActivity.Scalars.Trace(
            $"[decode_seq2seq] {model.QualifiedName}#{sessionAlias}: "
            + $"generated.Count={result.Length}");

        return ValueRef.FromPrimitiveArray(result, DataKind.Int64);
    }

    /// <summary>
    /// No-cache greedy loop. Each step rebuilds the full
    /// <c>input_ids = prefix || generated</c> and re-runs the decoder over
    /// the entire sequence — cheapest decoding path that doesn't require
    /// the decoder ONNX to export <c>past_key_values.*</c> inputs. Used by
    /// ViT-GPT2, Florence-2 (no-cache exports), and any other decoder
    /// without a cache branch.
    /// </summary>
    private static async ValueTask<long[]> GenerateNoCacheAsync(
        IInferenceSession decoder,
        TensorSpec inputIdsSpec,
        TensorSpec encoderHiddenSpec,
        TensorSpec? encoderMaskSpec,
        TensorSpec logitsSpec,
        long[] prefix,
        long eosTokenId,
        int maxTokens,
        float[] encoderFeatures,
        int[] encoderShape,
        long[]? encoderMask,
        int[]? encoderMaskShape,
        CancellationToken cancellationToken)
    {
        List<long> generated = new(capacity: Math.Min(maxTokens, 32));

        for (int step = 0; step < maxTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int seqLen = prefix.Length + generated.Count;
            long[] currentIds = new long[seqLen];
            for (int i = 0; i < prefix.Length; i++) currentIds[i] = prefix[i];
            for (int i = 0; i < generated.Count; i++) currentIds[prefix.Length + i] = generated[i];

            long nextToken = await DispatchOneStepAsync(
                decoder,
                inputIdsSpec,
                encoderHiddenSpec,
                encoderMaskSpec,
                logitsSpec,
                currentIds,
                encoderFeatures,
                encoderShape,
                encoderMask,
                encoderMaskShape,
                cancellationToken).ConfigureAwait(false);

            if (nextToken == eosTokenId) break;
            generated.Add(nextToken);
        }

        return generated.ToArray();
    }

    /// <summary>
    /// KV-cache greedy loop. Used by TrOCR-style decoders whose ONNX export
    /// declares per-layer <c>past_key_values.{layer}.decoder.{key|value}</c>
    /// (self-attention) and optionally
    /// <c>past_key_values.{layer}.encoder.{key|value}</c> (cross-attention)
    /// inputs, plus matching <c>present.*</c> outputs. Step 0 is a prefill
    /// pass that consumes the full prefix with all-empty past caches and
    /// <c>use_cache_branch = false</c>; subsequent steps feed just the
    /// last generated token with the previous step's present-* outputs
    /// as past-* inputs and <c>use_cache_branch = true</c>.
    /// </summary>
    private static async ValueTask<long[]> GenerateWithKvCacheAsync(
        IInferenceSession decoder,
        TensorSpec inputIdsSpec,
        TensorSpec encoderHiddenSpec,
        TensorSpec? encoderMaskSpec,
        TensorSpec logitsSpec,
        long[] prefix,
        long eosTokenId,
        int maxTokens,
        float[] encoderFeatures,
        int[] encoderShape,
        long[]? encoderMask,
        int[]? encoderMaskShape,
        string sessionAlias,
        CancellationToken cancellationToken)
    {
        // Discover the cache layout from the decoder session's input set.
        // Naming convention: past_key_values.{layer}.{decoder|encoder}.{key|value}
        // (mirrored on the output side as `present.{...}`). Layer count is
        // the count of `past_key_values.{N}.decoder.key` entries.
        List<TensorSpec> decoderKeyInputs = new();
        List<TensorSpec> decoderValueInputs = new();
        List<TensorSpec> encoderKeyInputs = new();
        List<TensorSpec> encoderValueInputs = new();
        TensorSpec? useCacheBranchSpec = null;

        foreach (TensorSpec spec in decoder.Inputs)
        {
            if (spec.Name == "use_cache_branch")
            {
                useCacheBranchSpec = spec;
            }
            else if (spec.Name.StartsWith("past_key_values.", StringComparison.Ordinal))
            {
                if (spec.Name.EndsWith(".decoder.key", StringComparison.Ordinal)) decoderKeyInputs.Add(spec);
                else if (spec.Name.EndsWith(".decoder.value", StringComparison.Ordinal)) decoderValueInputs.Add(spec);
                else if (spec.Name.EndsWith(".encoder.key", StringComparison.Ordinal)) encoderKeyInputs.Add(spec);
                else if (spec.Name.EndsWith(".encoder.value", StringComparison.Ordinal)) encoderValueInputs.Add(spec);
            }
        }

        if (decoderKeyInputs.Count == 0)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): use_kv_cache=true but decoder session '{sessionAlias}' "
                + "declares no `past_key_values.{layer}.decoder.key` inputs. The decoder ONNX "
                + "must be an `optimum-cli` export with cache support (the `*-with-past` or "
                + "merged variant). Switch to use_kv_cache=false or re-export the model.");
        }

        int numLayers = decoderKeyInputs.Count;
        if (decoderValueInputs.Count != numLayers)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): decoder session '{sessionAlias}' has {numLayers} decoder.key "
                + $"inputs but {decoderValueInputs.Count} decoder.value inputs. The export is "
                + "malformed — every layer needs a matching key/value pair.");
        }
        bool hasCrossCache = encoderKeyInputs.Count == numLayers;
        if (encoderKeyInputs.Count != 0 && encoderKeyInputs.Count != numLayers)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): decoder session '{sessionAlias}' has partial cross-attention "
                + $"cache: {encoderKeyInputs.Count} encoder.key inputs vs {numLayers} layers. "
                + "Cross-attention cache must be either fully present or fully absent.");
        }

        // Derive num_heads + head_dim from one past tensor's shape spec.
        // Convention: [batch, num_heads, past_seq, head_dim] with dims 1 and 3
        // concrete (heads and head_dim are model-architecture constants).
        TensorSpec sampleSpec = decoderKeyInputs[0];
        if (sampleSpec.Shape.Count != 4
            || sampleSpec.Shape[1] is not int numHeads
            || sampleSpec.Shape[3] is not int headDim)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): decoder session '{sessionAlias}' "
                + $"`past_key_values.0.decoder.key` shape must be 4-d [batch, heads, past_seq, head_dim] "
                + $"with concrete heads and head_dim; got "
                + $"[{string.Join(", ", sampleSpec.Shape.Select(d => d?.ToString() ?? "?"))}].");
        }
        int perPositionFloats = numHeads * headDim;

        DatumActivity.Scalars.Trace(
            $"[decode_seq2seq] kv-cache layout: layers={numLayers} heads={numHeads} "
            + $"head_dim={headDim} hasCrossCache={hasCrossCache} hasUseCacheBranch={useCacheBranchSpec is not null}");

        // Cache state, one float[] per (layer, side, kv) slot. Indexed by layer
        // for the inner loop; the array stays at Empty until the first present
        // output is read in.
        float[][] decoderKeyCache = new float[numLayers][];
        float[][] decoderValueCache = new float[numLayers][];
        float[][]? encoderKeyCache = hasCrossCache ? new float[numLayers][] : null;
        float[][]? encoderValueCache = hasCrossCache ? new float[numLayers][] : null;
        for (int l = 0; l < numLayers; l++)
        {
            decoderKeyCache[l] = Array.Empty<float>();
            decoderValueCache[l] = Array.Empty<float>();
            if (hasCrossCache)
            {
                encoderKeyCache![l] = Array.Empty<float>();
                encoderValueCache![l] = Array.Empty<float>();
            }
        }

        List<long> generated = new(capacity: Math.Min(maxTokens, 32));
        bool useCacheBranch = false;

        for (int step = 0; step < maxTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Prefill (step 0): full prefix, empty cache, use_cache_branch=false.
            // Incremental (step 1+): just the last generated token, cache from
            // previous step, use_cache_branch=true. The cached-decoder ONNX
            // returns logits whose seq-dim matches the input_ids seq-dim, so
            // we always argmax over the LAST position regardless of mode.
            long[] currentIds;
            if (!useCacheBranch)
            {
                currentIds = prefix;
            }
            else
            {
                currentIds = new long[] { generated[^1] };
            }

            int pastDecSeq = useCacheBranch ? decoderKeyCache[0].Length / perPositionFloats : 0;
            int pastEncSeq = useCacheBranch && hasCrossCache
                ? encoderKeyCache![0].Length / perPositionFloats : 0;
            int[] decoderCacheShape = [1, numHeads, pastDecSeq, headDim];
            int[] encoderCacheShape = hasCrossCache ? [1, numHeads, pastEncSeq, headDim] : null!;

            long nextToken = await DispatchOneStepWithCacheAsync(
                decoder,
                inputIdsSpec, encoderHiddenSpec, encoderMaskSpec, logitsSpec,
                useCacheBranchSpec,
                decoderKeyInputs, decoderValueInputs,
                encoderKeyInputs, encoderValueInputs,
                currentIds, encoderFeatures, encoderShape, encoderMask, encoderMaskShape,
                useCacheBranch,
                decoderKeyCache, decoderValueCache, encoderKeyCache, encoderValueCache,
                decoderCacheShape, encoderCacheShape, hasCrossCache,
                numLayers,
                cancellationToken).ConfigureAwait(false);

            if (nextToken == eosTokenId) break;
            generated.Add(nextToken);
            useCacheBranch = true;
        }

        return generated.ToArray();
    }

    /// <summary>
    /// Runs a single cached decoder forward pass: wires every past_kv input
    /// from the supplied caches, sets <c>use_cache_branch</c>, runs the
    /// session, reads the argmax of the last logits position AND rotates
    /// every <c>present.*</c> output into the corresponding cache slot for
    /// the next iteration. Caller resets <c>useCacheBranch</c> to true
    /// after the first invocation.
    /// </summary>
    private static async ValueTask<long> DispatchOneStepWithCacheAsync(
        IInferenceSession decoder,
        TensorSpec inputIdsSpec,
        TensorSpec encoderHiddenSpec,
        TensorSpec? encoderMaskSpec,
        TensorSpec logitsSpec,
        TensorSpec? useCacheBranchSpec,
        List<TensorSpec> decoderKeyInputs,
        List<TensorSpec> decoderValueInputs,
        List<TensorSpec> encoderKeyInputs,
        List<TensorSpec> encoderValueInputs,
        long[] currentIds,
        float[] encoderFeatures,
        int[] encoderShape,
        long[]? encoderMask,
        int[]? encoderMaskShape,
        bool useCacheBranch,
        float[][] decoderKeyCache,
        float[][] decoderValueCache,
        float[][]? encoderKeyCache,
        float[][]? encoderValueCache,
        int[] decoderCacheShape,
        int[] encoderCacheShape,
        bool hasCrossCache,
        int numLayers,
        CancellationToken cancellationToken)
    {
        TensorBag inputBag = decoder.CreateInputBag();
        TensorBag? outputBag = null;
        try
        {
            // Required inputs: input_ids + encoder_hidden_states. Both shapes
            // are exact every step; the cached export's leading-dim is always
            // batch=1 and the seq-dim reflects either prefill (full prefix)
            // or incremental (single new token).
            inputBag.Add<long>(
                inputIdsSpec.Name, DataKind.Int64,
                new int[] { 1, currentIds.Length }, currentIds);
            inputBag.Add<float>(
                encoderHiddenSpec.Name, DataKind.Float32,
                encoderShape, encoderFeatures);
            if (encoderMaskSpec is not null)
            {
                inputBag.Add<long>(
                    encoderMaskSpec.Name, DataKind.Int64,
                    encoderMaskShape!, encoderMask!);
            }

            // use_cache_branch is a shape-[1] bool tensor in TrOCR's export.
            // Always wired when the input exists — false on prefill, true after.
            if (useCacheBranchSpec is not null)
            {
                inputBag.Add<bool>(
                    useCacheBranchSpec.Name, DataKind.Boolean,
                    new int[] { 1 }, new[] { useCacheBranch });
            }

            // Wire every per-layer past_kv input. Self-attention pair always;
            // cross-attention pair when the export declared it. On prefill
            // the caches are zero-length (decoderCacheShape[2] = 0); on
            // incremental steps they hold the previous step's `present.*`
            // contents.
            for (int l = 0; l < numLayers; l++)
            {
                inputBag.Add<float>(
                    decoderKeyInputs[l].Name, DataKind.Float32,
                    decoderCacheShape, decoderKeyCache[l]);
                inputBag.Add<float>(
                    decoderValueInputs[l].Name, DataKind.Float32,
                    decoderCacheShape, decoderValueCache[l]);
                if (hasCrossCache)
                {
                    inputBag.Add<float>(
                        encoderKeyInputs[l].Name, DataKind.Float32,
                        encoderCacheShape, encoderKeyCache![l]);
                    inputBag.Add<float>(
                        encoderValueInputs[l].Name, DataKind.Float32,
                        encoderCacheShape, encoderValueCache![l]);
                }
            }

            outputBag = await decoder.RunAsync(inputBag, cancellationToken).ConfigureAwait(false);

            if (!outputBag.TryGet(logitsSpec.Name, out IInferenceTensor logitsTensor))
            {
                throw new InvalidOperationException(
                    $"decode_seq2seq(): decoder did not produce expected output '{logitsSpec.Name}'.");
            }

            ReadOnlySpan<float> logits = logitsTensor.AsSpan<float>();
            IReadOnlyList<int> shape = logitsTensor.Shape;
            if (shape.Count != 3)
            {
                throw new InvalidOperationException(
                    $"decode_seq2seq(): cached-decoder logits expected rank-3 [batch, seq, vocab]; "
                    + $"got [{string.Join(", ", shape)}].");
            }
            int vocab = shape[2];
            int lastPosStart = (shape[1] - 1) * vocab;
            ReadOnlySpan<float> lastPos = logits.Slice(lastPosStart, vocab);

            int argmax = 0;
            float best = lastPos[0];
            for (int i = 1; i < lastPos.Length; i++)
            {
                if (lastPos[i] > best) { best = lastPos[i]; argmax = i; }
            }

            // Rotate `present.*` outputs into the corresponding cache slot
            // for the next iteration. Names match per-layer past inputs but
            // with the prefix flipped from `past_key_values.` to `present.`.
            // The output bag is disposed at the end of this scope, so we
            // copy out to managed arrays here.
            for (int l = 0; l < numLayers; l++)
            {
                decoderKeyCache[l]   = ReadPresentFloats(outputBag, decoderKeyInputs[l].Name);
                decoderValueCache[l] = ReadPresentFloats(outputBag, decoderValueInputs[l].Name);
                if (hasCrossCache)
                {
                    encoderKeyCache![l]   = ReadPresentFloats(outputBag, encoderKeyInputs[l].Name);
                    encoderValueCache![l] = ReadPresentFloats(outputBag, encoderValueInputs[l].Name);
                }
            }

            return argmax;
        }
        finally
        {
            inputBag.Dispose();
            outputBag?.Dispose();
        }
    }

    /// <summary>
    /// Reads the `present.*` output corresponding to a past_kv input name
    /// (`past_key_values.{layer}.{side}.{kv}` → `present.{layer}.{side}.{kv}`)
    /// and returns a freshly-allocated float[]. Throws if the expected
    /// output is missing — every cached-decoder export pairs each past
    /// input with its present output.
    /// </summary>
    private static float[] ReadPresentFloats(TensorBag outputBag, string pastInputName)
    {
        const string PastPrefix = "past_key_values.";
        const string PresentPrefix = "present.";
        string presentName = pastInputName.StartsWith(PastPrefix, StringComparison.Ordinal)
            ? PresentPrefix + pastInputName[PastPrefix.Length..]
            : pastInputName; // already in the present.* form (some exports use it directly)

        if (!outputBag.TryGet(presentName, out IInferenceTensor tensor))
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): cached decoder did not produce expected present output '{presentName}'.");
        }
        return tensor.AsSpan<float>().ToArray();
    }

    /// <summary>
    /// Runs a single decoder forward pass with the current
    /// <paramref name="currentIds"/> sequence + the fixed encoder
    /// features, reads out the logits row at the last position, and
    /// returns its argmax. Allocates one input bag + one output bag
    /// per call — bags are disposed inside the method.
    /// </summary>
    private static async ValueTask<long> DispatchOneStepAsync(
        IInferenceSession decoder,
        TensorSpec inputIdsSpec,
        TensorSpec encoderHiddenSpec,
        TensorSpec? encoderMaskSpec,
        TensorSpec logitsSpec,
        long[] currentIds,
        float[] encoderFeatures,
        int[] encoderShape,
        long[]? encoderMask,
        int[]? encoderMaskShape,
        CancellationToken cancellationToken)
    {
        TensorBag inputBag = decoder.CreateInputBag();
        TensorBag? outputBag = null;
        try
        {
            inputBag.Add<long>(
                inputIdsSpec.Name, DataKind.Int64,
                new int[] { 1, currentIds.Length }, currentIds);
            inputBag.Add<float>(
                encoderHiddenSpec.Name, DataKind.Float32,
                encoderShape, encoderFeatures);
            if (encoderMaskSpec is not null)
            {
                inputBag.Add<long>(
                    encoderMaskSpec.Name, DataKind.Int64,
                    encoderMaskShape!, encoderMask!);
            }

            outputBag = await decoder.RunAsync(inputBag, cancellationToken).ConfigureAwait(false);

            if (!outputBag.TryGet(logitsSpec.Name, out IInferenceTensor logitsTensor))
            {
                throw new InvalidOperationException(
                    $"decode_seq2seq(): decoder did not produce expected output '{logitsSpec.Name}'.");
            }

            // Logits shape: [batch, seq, vocab]. Argmax over the last
            // sequence position picks the next token.
            ReadOnlySpan<float> logits = logitsTensor.AsSpan<float>();
            IReadOnlyList<int> shape = logitsTensor.Shape;
            if (shape.Count != 3)
            {
                throw new InvalidOperationException(
                    $"decode_seq2seq(): decoder logits expected rank-3 [batch, seq, vocab]; "
                    + $"got [{string.Join(", ", shape)}].");
            }
            int vocab = shape[2];
            int lastPosStart = (shape[1] - 1) * vocab;
            ReadOnlySpan<float> lastPos = logits.Slice(lastPosStart, vocab);

            int argmax = 0;
            float best = lastPos[0];
            for (int i = 1; i < lastPos.Length; i++)
            {
                if (lastPos[i] > best)
                {
                    best = lastPos[i];
                    argmax = i;
                }
            }
            return argmax;
        }
        finally
        {
            inputBag.Dispose();
            outputBag?.Dispose();
        }
    }

    /// <summary>
    /// Linear lookup of a session input by name; case-sensitive because
    /// ONNX exporters preserve the model author's casing and we don't want
    /// to mask a typo'd model export.
    /// </summary>
    private static TensorSpec? FindInput(IInferenceSession session, string name)
    {
        foreach (TensorSpec spec in session.Inputs)
        {
            if (string.Equals(spec.Name, name, StringComparison.Ordinal))
            {
                return spec;
            }
        }
        return null;
    }

    /// <summary>
    /// Pulls a managed <c>float[]</c> out of a Float32-array argument.
    /// Fast path is <see cref="ValueRef.Materialized"/> when the array
    /// came from <see cref="ValueRef.FromPrimitiveArray{T}"/> (the chained-
    /// managed-payload invariant); slow path unpacks element-by-element
    /// for arrays built by SQL array literals.
    /// </summary>
    private static float[] ExtractFloat32Array(ValueRef arg)
    {
        if (arg.ArrayElementKind != DataKind.Float32)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): expected Float32[] argument; got {arg.ArrayElementKind}[].");
        }
        if (arg.Materialized is float[] direct) return direct;
        ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
        float[] copied = new float[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            if (!elements[i].TryToFloat(out float f))
            {
                throw new InvalidOperationException(
                    $"decode_seq2seq(): Float32[] element [{i}] is not Float32-coercible.");
            }
            copied[i] = f;
        }
        return copied;
    }

    /// <summary>Sibling of <see cref="ExtractFloat32Array"/> for Int64[].</summary>
    private static long[] ExtractInt64Array(ValueRef arg)
    {
        if (arg.ArrayElementKind != DataKind.Int64)
        {
            throw new InvalidOperationException(
                $"decode_seq2seq(): expected Int64[] argument; got {arg.ArrayElementKind}[].");
        }
        if (arg.Materialized is long[] direct) return direct;
        ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
        long[] copied = new long[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            if (!elements[i].TryToInt64(out long v))
            {
                throw new InvalidOperationException(
                    $"decode_seq2seq(): Int64[] element [{i}] is not Int64-coercible.");
            }
            copied[i] = v;
        }
        return copied;
    }
}

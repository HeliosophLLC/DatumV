using DatumIngest.Diagnostics;
using DatumIngest.Execution;
using DatumIngest.Inference;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Greedy autoregressive decoding loop against a decoder-only multimodal
/// LLM session — the SQL primitive that replaces hand-rolled C# generation
/// loops for Moondream2-style vision-language models (and, in the future,
/// any decoder-only architecture that takes a pre-built prefix-embedding
/// sequence as <c>inputs_embeds</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Surface.</strong>
/// <code>
/// decode_decoder_only(
///     decoder_session       String,      -- decoder session alias
///     embed_tokens_session  String,      -- session that turns token ids into embeddings
///     prefix_embeddings     Float32[],   -- pre-concatenated visual || prompt embeddings,
///                                        --   flat [1, prefix_seq, hidden] Float32
///     eos_token_id          Int64,
///     max_tokens            Int32
/// ) RETURNS Int64[]                      -- generated token ids, in order
/// </code>
/// </para>
/// <para>
/// <strong>Decoder session contract.</strong> The bound decoder must
/// declare <c>inputs_embeds</c> (<c>[batch, seq, hidden]</c>),
/// <c>attention_mask</c> (<c>[batch, total_seq]</c>),
/// <c>position_ids</c> (<c>[batch, step_seq]</c>), and per-layer
/// <c>past_key_values.{layer}.key</c> /
/// <c>past_key_values.{layer}.value</c> inputs (shape
/// <c>[batch, heads, past_seq, head_dim]</c> — <c>heads</c> and
/// <c>head_dim</c> must be concrete in the spec). Outputs must include
/// <c>logits</c> plus matching <c>present.{layer}.key</c> /
/// <c>present.{layer}.value</c>. This matches the canonical
/// <c>optimum-cli export onnx</c> output for Phi-2 / Phi-1.5 / SmolLM
/// decoder-only architectures.
/// </para>
/// <para>
/// <strong>Loop shape.</strong>
/// <list type="bullet">
/// <item><strong>Step 0 (prefill).</strong> <c>inputs_embeds</c> = the full
/// <c>prefix_embeddings</c>; <c>attention_mask</c> = all-1s of length
/// <c>prefix_seq</c>; <c>position_ids</c> = <c>[0, 1, …, prefix_seq-1]</c>;
/// every <c>past_key_values</c> input is zero-length. Output
/// <c>logits</c> has shape <c>[1, prefix_seq, vocab]</c>; argmax over
/// the last position picks the first generated token.</item>
/// <item><strong>Steps 1+ (incremental).</strong> Run <c>embed_tokens</c>
/// on the previously-generated token to get a single-position embedding;
/// pass as <c>inputs_embeds</c> of shape <c>[1, 1, hidden]</c>;
/// <c>attention_mask</c> = all-1s of length <c>current_seq + 1</c>;
/// <c>position_ids</c> = <c>[current_seq]</c>; <c>past_key_values</c> =
/// the previous step's <c>present.*</c> outputs. Logits shape
/// <c>[1, 1, vocab]</c>; argmax picks the next token.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Hidden-dim derivation.</strong> The caller's
/// <c>prefix_embeddings</c> is a flat Float32[]; we recover its
/// <c>prefix_seq</c> length as <c>length / hidden_dim</c> where
/// <c>hidden_dim = num_heads × head_dim</c> from the decoder's
/// declared <c>past_key_values.0.key</c> shape spec. This mirrors how
/// <c>decode_seq2seq</c> derives encoder_seq_len.
/// </para>
/// <para>
/// <strong>What's deferred (v1).</strong> IO binding (GPU-resident KV
/// cache across steps, no managed-memory round-trips per token) — the
/// canonical optimization for fp16 inference on CUDA. Our v1 uses
/// standard ORT dispatch with managed cache arrays; expect 5–20× lower
/// throughput than a hand-tuned IO-binding loop. The function's
/// signature doesn't change when IO binding lands.
/// </para>
/// </remarks>
public sealed class DecodeDecoderOnlyFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "decode_decoder_only";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Greedy autoregressive decoding loop for decoder-only multimodal "
        + "LLMs (Moondream2-style: visual prefix injected at the embedding "
        + "layer, no encoder cross-attention). Returns the generated token "
        + "ids (prefix and EOS excluded). v1 uses standard ORT dispatch; "
        + "IO-binding GPU-resident cache is a follow-up.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("decoder_session", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("embed_tokens_session", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("prefix_embeddings", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("eos_token_id", DataKindMatcher.Exact(DataKind.Int64), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("max_tokens", DataKindMatcher.Exact(DataKind.Int32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Int64))),
    ];

    /// <inheritdoc />
    public static BodyScopeRequirement BodyScope => BodyScopeRequirement.ModelBody;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DecodeDecoderOnlyFunction>(argumentKinds);

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        int argLen = arguments.Length;
        if (argLen != 5)
        {
            throw new InvalidOperationException(
                $"decode_decoder_only() expects 5 arguments; got {argLen}.");
        }
        if (frame.CurrentModel is not { } model)
        {
            throw new InvalidOperationException(
                "decode_decoder_only() is only callable from inside a CREATE MODEL body.");
        }

        // Two-phase: extract alias strings before awaiting (Spans can't
        // cross an await), then load both sessions, then re-acquire span.
        string decoderAlias;
        string embedAlias;
        {
            ReadOnlySpan<ValueRef> probe = arguments.Span;
            decoderAlias = probe[0].AsString();
            embedAlias = probe[1].AsString();
        }

        if (!model.BoundSessions.ContainsKey(decoderAlias))
        {
            throw new InvalidOperationException(
                $"decode_decoder_only(): decoder session alias '{decoderAlias}' is not bound. "
                + $"Available: [{string.Join(", ", model.BoundSessions.Keys)}].");
        }
        if (!model.BoundSessions.ContainsKey(embedAlias))
        {
            throw new InvalidOperationException(
                $"decode_decoder_only(): embed_tokens session alias '{embedAlias}' is not bound. "
                + $"Available: [{string.Join(", ", model.BoundSessions.Keys)}].");
        }
        // Cast from the narrow IModelSession handle: this scalar drives
        // tensor I/O (Inputs/Outputs introspection + RunAsync), so it
        // requires the IInferenceSession surface that the ORT-backed
        // session implementation supplies. A non-tensor backend bound
        // to this alias would surface here as an InvalidCastException —
        // the right outcome, since the body declared an ORT-shaped
        // contract.
        IInferenceSession decoder = (IInferenceSession)await model.BoundSessions
            .ResolveAsync(decoderAlias, cancellationToken).ConfigureAwait(false);
        IInferenceSession embedTokens = (IInferenceSession)await model.BoundSessions
            .ResolveAsync(embedAlias, cancellationToken).ConfigureAwait(false);

        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[2].IsNull)
        {
            throw new InvalidOperationException(
                "decode_decoder_only(): prefix_embeddings must be non-NULL "
                + "(typically `visual_features || embed_tokens(prompt_ids)`).");
        }
        float[] prefixEmbeds = ExtractFloat32Array(args[2]);
        long eosTokenId = args[3].AsInt64();
        int maxTokens = args[4].AsInt32();
        if (maxTokens <= 0)
        {
            throw new InvalidOperationException(
                $"decode_decoder_only(): max_tokens must be positive; got {maxTokens}.");
        }

        // Discover cache layout from the decoder session. Decoder-only
        // exports use a simpler naming convention than seq2seq:
        // past_key_values.{layer}.key / .value (no .decoder./.encoder.
        // split because there's no cross-attention).
        TensorSpec? inputsEmbedsSpec = FindInput(decoder, "inputs_embeds");
        TensorSpec? attentionMaskSpec = FindInput(decoder, "attention_mask");
        TensorSpec? positionIdsSpec = FindInput(decoder, "position_ids");
        if (inputsEmbedsSpec is null || attentionMaskSpec is null || positionIdsSpec is null)
        {
            throw new InvalidOperationException(
                $"decode_decoder_only(): decoder session '{decoderAlias}' must declare "
                + "`inputs_embeds`, `attention_mask`, and `position_ids` inputs. "
                + "Missing: ["
                + $"{(inputsEmbedsSpec is null ? "inputs_embeds " : "")}"
                + $"{(attentionMaskSpec is null ? "attention_mask " : "")}"
                + $"{(positionIdsSpec is null ? "position_ids " : "")}].");
        }

        List<TensorSpec> pastKeyInputs = new();
        List<TensorSpec> pastValueInputs = new();
        foreach (TensorSpec spec in decoder.Inputs)
        {
            if (spec.Name.StartsWith("past_key_values.", StringComparison.Ordinal))
            {
                if (spec.Name.EndsWith(".key", StringComparison.Ordinal)) pastKeyInputs.Add(spec);
                else if (spec.Name.EndsWith(".value", StringComparison.Ordinal)) pastValueInputs.Add(spec);
            }
        }
        int numLayers = pastKeyInputs.Count;
        if (numLayers == 0 || pastValueInputs.Count != numLayers)
        {
            throw new InvalidOperationException(
                $"decode_decoder_only(): decoder session '{decoderAlias}' must declare matching "
                + $"`past_key_values.{{N}}.key` and `past_key_values.{{N}}.value` inputs per layer "
                + $"(found {pastKeyInputs.Count} keys / {pastValueInputs.Count} values).");
        }

        // Derive heads + head_dim from one past_kv shape spec.
        TensorSpec sample = pastKeyInputs[0];
        if (sample.Shape.Count != 4
            || sample.Shape[1] is not int numHeads
            || sample.Shape[3] is not int headDim)
        {
            throw new InvalidOperationException(
                $"decode_decoder_only(): decoder session '{decoderAlias}' "
                + $"`past_key_values.0.key` shape must be 4-d [batch, heads, past_seq, head_dim] "
                + $"with concrete heads and head_dim; got "
                + $"[{string.Join(", ", sample.Shape.Select(d => d?.ToString() ?? "?"))}].");
        }
        int hiddenDim = numHeads * headDim;

        if (prefixEmbeds.Length == 0 || prefixEmbeds.Length % hiddenDim != 0)
        {
            throw new InvalidOperationException(
                $"decode_decoder_only(): prefix_embeddings length {prefixEmbeds.Length} "
                + $"is not divisible by hidden_dim {hiddenDim} (= {numHeads} heads × {headDim} head_dim). "
                + "The flat Float32[] must be a multiple of the model's hidden dimension.");
        }
        int prefixSeq = prefixEmbeds.Length / hiddenDim;

        // Resolve logits + present.* output specs. Convention: `logits`
        // first; `present.{layer}.key` and `present.{layer}.value` mirror
        // the past inputs.
        TensorSpec logitsSpec = decoder.Outputs.FirstOrDefault(o =>
            string.Equals(o.Name, "logits", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"decode_decoder_only(): decoder session '{decoderAlias}' has no 'logits' output.");

        // embed_tokens session metadata (for the per-step lookup that
        // turns the most-recently-generated token id into a single-
        // position embedding).
        TensorSpec? embedInputIdsSpec = FindInput(embedTokens, "input_ids");
        if (embedInputIdsSpec is null)
        {
            throw new InvalidOperationException(
                $"decode_decoder_only(): embed_tokens session '{embedAlias}' must declare an "
                + "'input_ids' input.");
        }
        if (embedTokens.Outputs.Count == 0)
        {
            throw new InvalidOperationException(
                $"decode_decoder_only(): embed_tokens session '{embedAlias}' declares no outputs.");
        }
        TensorSpec embedOutputSpec = embedTokens.Outputs[0];

        DatumActivity.Scalars.Trace(
            $"[decode_decoder_only] {model.QualifiedName}: layers={numLayers} "
            + $"heads={numHeads} head_dim={headDim} hidden={hiddenDim} "
            + $"prefix_seq={prefixSeq} max_tokens={maxTokens} eos={eosTokenId}");

        // Cache state — one float[] per (layer, side) slot.
        float[][] keyCache = new float[numLayers][];
        float[][] valueCache = new float[numLayers][];
        for (int l = 0; l < numLayers; l++)
        {
            keyCache[l] = Array.Empty<float>();
            valueCache[l] = Array.Empty<float>();
        }

        List<long> generated = new(capacity: Math.Min(maxTokens, 32));
        int currentSeqLen = 0;

        for (int step = 0; step < maxTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Determine step inputs.
            float[] stepEmbeds;
            int stepLen;
            if (step == 0)
            {
                stepEmbeds = prefixEmbeds;
                stepLen = prefixSeq;
            }
            else
            {
                stepEmbeds = await RunEmbedTokensSingleAsync(
                    embedTokens, embedInputIdsSpec, embedOutputSpec,
                    generated[^1], hiddenDim,
                    cancellationToken).ConfigureAwait(false);
                stepLen = 1;
            }

            int[] embedsShape = [1, stepLen, hiddenDim];

            long[] positionIds = new long[stepLen];
            for (int i = 0; i < stepLen; i++) positionIds[i] = currentSeqLen + i;
            int[] posShape = [1, stepLen];

            int totalLen = currentSeqLen + stepLen;
            long[] attentionMask = new long[totalLen];
            Array.Fill(attentionMask, 1L);
            int[] maskShape = [1, totalLen];

            // past_kv shape for THIS step. Length is the post-cache-rotation
            // count — zero on step 0 (no past yet), then growing by stepLen
            // each iteration (prefill grows by prefix_seq; incremental
            // grows by 1).
            int pastSeq = step == 0 ? 0 : keyCache[0].Length / (numHeads * headDim);
            int[] pastKvShape = [1, numHeads, pastSeq, headDim];

            long nextToken = await DispatchOneStepAsync(
                decoder,
                inputsEmbedsSpec, attentionMaskSpec, positionIdsSpec,
                pastKeyInputs, pastValueInputs,
                logitsSpec,
                stepEmbeds, embedsShape,
                attentionMask, maskShape,
                positionIds, posShape,
                keyCache, valueCache, pastKvShape,
                numLayers,
                cancellationToken).ConfigureAwait(false);

            if (nextToken == eosTokenId) break;
            generated.Add(nextToken);
            currentSeqLen += stepLen;
        }

        DatumActivity.Scalars.Trace(
            $"[decode_decoder_only] {model.QualifiedName}: generated.Count={generated.Count}");

        return ValueRef.FromPrimitiveArray(generated.ToArray(), DataKind.Int64);
    }

    /// <summary>
    /// Runs <c>embed_tokens</c> with a single-element input_ids tensor and
    /// returns the flat <c>hidden_dim</c>-length embedding. Used per-step
    /// during incremental decoding to convert the most-recently-generated
    /// token id into a <c>[1, 1, hidden]</c> tensor for the decoder's
    /// <c>inputs_embeds</c> input.
    /// </summary>
    private static async ValueTask<float[]> RunEmbedTokensSingleAsync(
        IInferenceSession session,
        TensorSpec inputIdsSpec,
        TensorSpec outputSpec,
        long tokenId,
        int hiddenDim,
        CancellationToken cancellationToken)
    {
        TensorBag inputBag = session.CreateInputBag();
        TensorBag? outputBag = null;
        try
        {
            inputBag.Add<long>(
                inputIdsSpec.Name, DataKind.Int64,
                new int[] { 1, 1 }, new[] { tokenId });

            outputBag = await session.RunAsync(inputBag, cancellationToken).ConfigureAwait(false);

            if (!outputBag.TryGet(outputSpec.Name, out IInferenceTensor outputTensor))
            {
                throw new InvalidOperationException(
                    $"decode_decoder_only(): embed_tokens produced no '{outputSpec.Name}' output.");
            }
            ReadOnlySpan<float> data = outputTensor.AsSpan<float>();
            if (data.Length != hiddenDim)
            {
                throw new InvalidOperationException(
                    $"decode_decoder_only(): embed_tokens single-id output produced {data.Length} "
                    + $"floats; expected hidden_dim {hiddenDim}.");
            }
            return data.ToArray();
        }
        finally
        {
            inputBag.Dispose();
            outputBag?.Dispose();
        }
    }

    /// <summary>
    /// Single decoder forward pass: wires inputs_embeds + attention_mask +
    /// position_ids + every per-layer past_kv input, runs the session,
    /// reads the argmax of the last logits position, and rotates the
    /// returned <c>present.*</c> outputs into the corresponding cache
    /// slots for the next iteration.
    /// </summary>
    private static async ValueTask<long> DispatchOneStepAsync(
        IInferenceSession decoder,
        TensorSpec inputsEmbedsSpec,
        TensorSpec attentionMaskSpec,
        TensorSpec positionIdsSpec,
        List<TensorSpec> pastKeyInputs,
        List<TensorSpec> pastValueInputs,
        TensorSpec logitsSpec,
        float[] embeds, int[] embedsShape,
        long[] attentionMask, int[] maskShape,
        long[] positionIds, int[] posShape,
        float[][] keyCache, float[][] valueCache,
        int[] pastKvShape,
        int numLayers,
        CancellationToken cancellationToken)
    {
        TensorBag inputBag = decoder.CreateInputBag();
        TensorBag? outputBag = null;
        try
        {
            inputBag.Add<float>(inputsEmbedsSpec.Name, DataKind.Float32, embedsShape, embeds);
            inputBag.Add<long>(attentionMaskSpec.Name, DataKind.Int64, maskShape, attentionMask);
            inputBag.Add<long>(positionIdsSpec.Name, DataKind.Int64, posShape, positionIds);
            for (int l = 0; l < numLayers; l++)
            {
                inputBag.Add<float>(pastKeyInputs[l].Name, DataKind.Float32, pastKvShape, keyCache[l]);
                inputBag.Add<float>(pastValueInputs[l].Name, DataKind.Float32, pastKvShape, valueCache[l]);
            }

            outputBag = await decoder.RunAsync(inputBag, cancellationToken).ConfigureAwait(false);

            if (!outputBag.TryGet(logitsSpec.Name, out IInferenceTensor logitsTensor))
            {
                throw new InvalidOperationException(
                    $"decode_decoder_only(): decoder did not produce '{logitsSpec.Name}'.");
            }
            ReadOnlySpan<float> logits = logitsTensor.AsSpan<float>();
            IReadOnlyList<int> shape = logitsTensor.Shape;
            if (shape.Count != 3)
            {
                throw new InvalidOperationException(
                    $"decode_decoder_only(): logits expected rank-3 [batch, seq, vocab]; "
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

            // Rotate present.* into past_key_values.* slots for the next
            // iteration. Names match per layer: present.{N}.key /
            // present.{N}.value mirror past_key_values.{N}.key / .value.
            for (int l = 0; l < numLayers; l++)
            {
                keyCache[l]   = ReadPresent(outputBag, pastKeyInputs[l].Name);
                valueCache[l] = ReadPresent(outputBag, pastValueInputs[l].Name);
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
    /// Reads the <c>present.*</c> output corresponding to a
    /// <c>past_key_values.*</c> input name and returns a freshly-
    /// allocated float[] copy.
    /// </summary>
    private static float[] ReadPresent(TensorBag outputBag, string pastInputName)
    {
        const string PastPrefix = "past_key_values.";
        const string PresentPrefix = "present.";
        string presentName = pastInputName.StartsWith(PastPrefix, StringComparison.Ordinal)
            ? PresentPrefix + pastInputName[PastPrefix.Length..]
            : pastInputName;

        if (!outputBag.TryGet(presentName, out IInferenceTensor tensor))
        {
            throw new InvalidOperationException(
                $"decode_decoder_only(): decoder did not produce expected present output '{presentName}'.");
        }
        return tensor.AsSpan<float>().ToArray();
    }

    private static TensorSpec? FindInput(IInferenceSession session, string name)
    {
        foreach (TensorSpec spec in session.Inputs)
        {
            if (string.Equals(spec.Name, name, StringComparison.Ordinal)) return spec;
        }
        return null;
    }

    private static float[] ExtractFloat32Array(ValueRef arg)
    {
        if (arg.ArrayElementKind != DataKind.Float32)
        {
            throw new InvalidOperationException(
                $"decode_decoder_only(): expected Float32[] argument; got {arg.ArrayElementKind}[].");
        }
        if (arg.Materialized is float[] direct) return direct;
        ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
        float[] copied = new float[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            if (!elements[i].TryToFloat(out float f))
            {
                throw new InvalidOperationException(
                    $"decode_decoder_only(): Float32[] element [{i}] is not Float32-coercible.");
            }
            copied[i] = f;
        }
        return copied;
    }
}

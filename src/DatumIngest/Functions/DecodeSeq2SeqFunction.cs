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
                new ParameterSpec("encoder_attention_mask", DataKindMatcher.Exact(DataKind.Int64), IsArray: ArrayMatch.Array),
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
        // NULL when the decoder session has no such input. Mismatch
        // resolution happens below against the decoder's declared inputs.
        long[]? encoderMask = args[2].IsNull ? null : ExtractInt64Array(args[2]);

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

        if (useKvCache)
        {
            // Reserve the parameter for the follow-up that lands the
            // KV-cache path. Throwing rather than silently falling back
            // to no-cache so a caller who deliberately requested cache
            // notices the missing implementation.
            throw new NotSupportedException(
                "decode_seq2seq(): use_kv_cache=true is reserved for the upcoming "
                + "KV-cache variant. Pass false for v1 (no-cache greedy decode).");
        }

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

        DatumActivity.Scalars.Trace(
            $"[decode_seq2seq] {model.QualifiedName}#{sessionAlias}: "
            + $"prefix.Length={prefix.Length} max_tokens={maxTokens} eos={eosTokenId} "
            + $"encoder=[{encoderSeqLen},{hiddenDim}] mask={(encoderMask is null ? "null" : encoderMask.Length.ToString())}");

        // ───────── No-cache generation loop ─────────
        //
        // Each step rebuilds the full input_ids = prefix || generated and
        // re-runs the decoder. Output logits are [1, seq, vocab]; argmax
        // over the last position is the next token. Loop until EOS or
        // max_tokens. Returned array contains only the generated tokens
        // (prefix is excluded — the caller already supplied it).
        List<long> generated = new(capacity: Math.Min(maxTokens, 32));
        int[] encoderShape = [1, encoderSeqLen, hiddenDim];
        int[]? encoderMaskShape = encoderMask is null ? null : new[] { 1, encoderSeqLen };

        for (int step = 0; step < maxTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Build the current input_ids tensor: prefix || generated.
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

            if (nextToken == eosTokenId)
            {
                break;
            }
            generated.Add(nextToken);
        }

        DatumActivity.Scalars.Trace(
            $"[decode_seq2seq] {model.QualifiedName}#{sessionAlias}: "
            + $"generated.Count={generated.Count} (stopped on "
            + $"{(generated.Count == maxTokens ? "max_tokens" : "eos")})");

        long[] result = generated.ToArray();
        return ValueRef.FromPrimitiveArray(result, DataKind.Int64);
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

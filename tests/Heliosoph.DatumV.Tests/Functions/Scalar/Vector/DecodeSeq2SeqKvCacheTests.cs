using Heliosoph.DatumV.Functions.Scalar.Vector;
using Heliosoph.DatumV.Inference;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Tests.Inference;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Vector;

/// <summary>
/// Regression tests for the KV-cache decode loop behind <c>decode_seq2seq</c>
/// (the path TrOCR / Whisper / cached seq2seq exports take). Driven by a stub
/// decoder session — no ONNX model, no download — so the cache bookkeeping is
/// exercised deterministically.
/// </summary>
/// <remarks>
/// The bug these guard: the cross-attention (encoder) KV cache is computed once
/// from <c>encoder_hidden_states</c> on the prefill pass and is invariant
/// afterwards, but the loop used to re-read it from the decoder's
/// <c>present.*.encoder.*</c> outputs every step. Optimum's merged decoder
/// returns EMPTY encoder-present tensors on the with-past branch, so that
/// re-read wiped the cross cache after the first generated token — cross
/// attention then saw nothing and the decoder free-ran on its language prior
/// (correct first token, garbage tail). The stub below reproduces that exact
/// behaviour and emits a sentinel "garbage" token whenever it is asked to
/// decode with an empty cross cache on the cached branch.
/// </remarks>
public sealed class DecodeSeq2SeqKvCacheTests
{
    private const int Vocab = 16;
    private const int Hidden = 2;
    private const int EncoderSeq = 2;
    private const long Eos = 2;
    private const long Garbage = 15;
    private static readonly long[] CorrectTokens = [10, 11, 12];

    /// <summary>
    /// The bug repro: the export returns empty <c>present.*.encoder.*</c> on
    /// the cached branch. A correct loop must NOT re-read (and thereby wipe)
    /// the cross cache, so the full token sequence decodes. The old code
    /// dropped the cross cache after token 1 and the stub then emits
    /// <see cref="Garbage"/> for every remaining step.
    /// </summary>
    [Fact]
    public async Task KvCache_EmptyEncoderPresentOnCachedBranch_DecodesFullSequence()
    {
        long[] generated = await RunDecodeAsync(emptyEncoderPresentOnCachedBranch: true);
        Assert.Equal(CorrectTokens, generated);
        Assert.DoesNotContain(Garbage, generated);
    }

    /// <summary>
    /// A well-behaved export that passes the cross cache through as non-empty
    /// <c>present.*.encoder.*</c> on the cached branch. Confirms the
    /// capture-on-prefill fix is also correct here (idempotent) — i.e. it
    /// doesn't regress Whisper-style decoders that do echo the cross cache.
    /// </summary>
    [Fact]
    public async Task KvCache_PassthroughEncoderPresent_DecodesFullSequence()
    {
        long[] generated = await RunDecodeAsync(emptyEncoderPresentOnCachedBranch: false);
        Assert.Equal(CorrectTokens, generated);
    }

    private static async Task<long[]> RunDecodeAsync(bool emptyEncoderPresentOnCachedBranch)
    {
        TensorSpec inputIds = new("input_ids", DataKind.Int64, [1, null]);
        TensorSpec encoderHidden = new("encoder_hidden_states", DataKind.Float32, [1, null, Hidden]);
        TensorSpec useCacheBranch = new("use_cache_branch", DataKind.Boolean, [1]);
        TensorSpec pastDecKey = new("past_key_values.0.decoder.key", DataKind.Float32, [1, 1, null, 1]);
        TensorSpec pastDecVal = new("past_key_values.0.decoder.value", DataKind.Float32, [1, 1, null, 1]);
        TensorSpec pastEncKey = new("past_key_values.0.encoder.key", DataKind.Float32, [1, 1, null, 1]);
        TensorSpec pastEncVal = new("past_key_values.0.encoder.value", DataKind.Float32, [1, 1, null, 1]);
        TensorSpec logits = new("logits", DataKind.Float32, [1, null, Vocab]);

        KvCacheStubSession decoder = new(
            inputs: [inputIds, encoderHidden, useCacheBranch, pastDecKey, pastDecVal, pastEncKey, pastEncVal],
            outputs: [logits],
            emptyEncoderPresentOnCachedBranch);

        long[] generated = await DecodeSeq2SeqFunction.GenerateWithKvCacheAsync(
            decoder,
            inputIdsSpec: inputIds,
            encoderHiddenSpec: encoderHidden,
            encoderMaskSpec: null,
            logitsSpec: logits,
            prefix: [Eos],                 // TrOCR: decoder_start_token = 2 (= EOS value)
            eosTokenId: Eos,
            maxTokens: 10,
            suppressAbove: -1,
            encoderFeatures: new float[EncoderSeq * Hidden],
            encoderShape: [1, EncoderSeq, Hidden],
            encoderMask: null,
            encoderMaskShape: null,
            sessionAlias: "decoder",
            cancellationToken: default);

        return generated;
    }

    /// <summary>
    /// Stub decoder that models a merged cached seq2seq export. Emits the next
    /// "correct" token from <see cref="CorrectTokens"/> keyed on the incoming
    /// self-attention cache length (the decode position) — UNLESS it is on the
    /// cached branch with an empty cross-attention cache, in which case it can
    /// no longer see the encoder and emits <see cref="Garbage"/>. The
    /// self-attention cache always grows by one; the cross cache is full on
    /// prefill and (optionally) empty on the cached branch.
    /// </summary>
    private sealed class KvCacheStubSession : IInferenceSession
    {
        private readonly bool _emptyEncoderPresentOnCachedBranch;

        public KvCacheStubSession(
            IReadOnlyList<TensorSpec> inputs,
            IReadOnlyList<TensorSpec> outputs,
            bool emptyEncoderPresentOnCachedBranch)
        {
            Inputs = inputs;
            Outputs = outputs;
            _emptyEncoderPresentOnCachedBranch = emptyEncoderPresentOnCachedBranch;
        }

        public IReadOnlyList<TensorSpec> Inputs { get; }
        public IReadOnlyList<TensorSpec> Outputs { get; }
        public InferenceBackendId Backend => InferenceBackendId.OnnxRuntime;
        public InferenceDevice Device => InferenceDevice.OnnxRuntimeCpu;
        public long EstimatedResidentBytes => 0;
        public TensorBag CreateInputBag() => new StubTensorBag();
        public void Dispose() { }

        public ValueTask<TensorBag> RunAsync(TensorBag inputs, CancellationToken cancellationToken)
        {
            bool cached = inputs["use_cache_branch"].AsSpan<bool>()[0];
            int pastDecSeq = inputs["past_key_values.0.decoder.key"].Shape[2];
            int pastEncSeq = inputs["past_key_values.0.encoder.key"].Shape[2];
            int currentSeq = inputs["input_ids"].Shape[1];

            long token;
            if (cached && pastEncSeq == 0)
            {
                // Cross-attention cache was wiped — the decoder can't see the
                // image. This is the failure the fix prevents.
                token = Garbage;
            }
            else
            {
                int position = pastDecSeq; // 0 on prefill, +1 each generated token
                token = position < CorrectTokens.Length ? CorrectTokens[position] : Eos;
            }

            StubTensorBag output = new();

            // logits [1, currentSeq, Vocab]; the loop argmaxes the last position.
            float[] logits = new float[currentSeq * Vocab];
            logits[((currentSeq - 1) * Vocab) + (int)token] = 1f;
            output.Add<float>("logits", DataKind.Float32, [1, currentSeq, Vocab], logits);

            // Self-attention (decoder) cache grows by currentSeq positions
            // (heads=1, head_dim=1, so one float per position).
            int decLen = pastDecSeq + currentSeq;
            output.Add<float>("present.0.decoder.key", DataKind.Float32, [1, 1, decLen, 1], new float[decLen]);
            output.Add<float>("present.0.decoder.value", DataKind.Float32, [1, 1, decLen, 1], new float[decLen]);

            // Cross-attention (encoder) present: full on prefill; on the cached
            // branch either empty (optimum merged behaviour — the bug trigger)
            // or a non-empty passthrough (well-behaved export).
            int encLen = !cached
                ? EncoderSeq
                : (_emptyEncoderPresentOnCachedBranch ? 0 : EncoderSeq);
            output.Add<float>("present.0.encoder.key", DataKind.Float32, [1, 1, encLen, 1], new float[encLen]);
            output.Add<float>("present.0.encoder.value", DataKind.Float32, [1, 1, encLen, 1], new float[encLen]);

            return ValueTask.FromResult<TensorBag>(output);
        }
    }
}

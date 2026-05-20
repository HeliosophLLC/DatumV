using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;

namespace Heliosoph.DatumV.Tests.Models;

/// <summary>
/// Unit tests for <see cref="IBatchSizePolicy"/> implementations. The
/// <see cref="DoublingBatchSizePolicy"/> tests use the
/// <see cref="IVramProbe"/> seam to inject deterministic VRAM readings —
/// no real GPU required, runs the same on CI as on a CUDA dev box.
/// </summary>
public sealed class BatchSizePolicyTests
{
    private const long GB = 1024L * 1024 * 1024;
    private const long MB = 1024L * 1024;

    // ───────────────────── StaticBatchSizePolicy ─────────────────────

    [Fact]
    public void Static_ChooseBatchSize_NullPreferred_ReturnsRowsRemaining()
    {
        IBatchSizePolicy policy = StaticBatchSizePolicy.Instance;
        FakeModel model = new(preferredBatchSize: null);

        Assert.Equal(10, policy.ChooseBatchSize(model, 10));
        Assert.Equal(1, policy.ChooseBatchSize(model, 1));
    }

    [Fact]
    public void Static_ChooseBatchSize_WithPreferred_ReturnsMinWithRowsRemaining()
    {
        IBatchSizePolicy policy = StaticBatchSizePolicy.Instance;
        FakeModel model = new(preferredBatchSize: 4);

        Assert.Equal(4, policy.ChooseBatchSize(model, 10));
        Assert.Equal(2, policy.ChooseBatchSize(model, 2));
        Assert.Equal(4, policy.ChooseBatchSize(model, 4));
    }

    // ──────────────────── DoublingBatchSizePolicy ────────────────────

    /// <summary>
    /// Cold start: with probe available, the policy starts every model
    /// at batch=1 so the first measurement yields a clean per-row delta.
    /// </summary>
    [Fact]
    public void Doubling_FirstDispatch_StartsAtOne()
    {
        FakeVramProbe probe = new(usedBytes: 8 * GB, totalBytes: 24 * GB);
        DoublingBatchSizePolicy policy = new(probe);
        FakeModel model = new(preferredBatchSize: null);

        Assert.Equal(1, policy.ChooseBatchSize(model, 100));
    }

    /// <summary>
    /// After a successful measurement the policy doubles the batch size
    /// for the next dispatch (1 → 2 → 4 → 8) as long as the conservative
    /// prediction fits the remaining VRAM budget.
    /// </summary>
    [Fact]
    public void Doubling_RampsAfterMeasurement()
    {
        FakeVramProbe probe = new(usedBytes: 8 * GB, totalBytes: 24 * GB);
        DoublingBatchSizePolicy policy = new(probe);
        FakeModel model = new(preferredBatchSize: null);

        // Each row's activation cost is small enough that doubling
        // comfortably fits. Per-row delta of 10 MB → step-N prediction
        // is N × 10 MB × 1.2 = trivial against 16 GB headroom.
        Assert.Equal(1, policy.ChooseBatchSize(model, 100));
        policy.RecordDispatch(model, batchSize: 1,
            vramBefore: 8 * GB, vramAfter: 8 * GB + 10 * MB, dispatchMs: 50);

        Assert.Equal(2, policy.ChooseBatchSize(model, 100));
        policy.RecordDispatch(model, batchSize: 2,
            vramBefore: 8 * GB, vramAfter: 8 * GB + 20 * MB, dispatchMs: 100);

        Assert.Equal(4, policy.ChooseBatchSize(model, 100));
        policy.RecordDispatch(model, batchSize: 4,
            vramBefore: 8 * GB, vramAfter: 8 * GB + 40 * MB, dispatchMs: 200);

        Assert.Equal(8, policy.ChooseBatchSize(model, 100));
    }

    /// <summary>
    /// When the conservative prediction (next-batch × per-row × 1.2) plus
    /// current used + safety margin would exceed total VRAM, the policy
    /// settles at the current batch instead of doubling.
    /// </summary>
    [Fact]
    public void Doubling_SettlesWhenPredictionExceedsBudget()
    {
        // 10 GB total, 8 GB already used, per-row activation 1 GB. Safety
        // margin for a 10 GB device is max(512 MB, 1 GB) = 1 GB. Next-batch
        // prediction at 2 = 2 × 1 GB × 1.2 = 2.4 GB. 8 + 2.4 + 1 = 11.4 GB
        // > 10 GB → settled at 1.
        FakeVramProbe probe = new(usedBytes: 8 * GB, totalBytes: 10 * GB);
        DoublingBatchSizePolicy policy = new(probe);
        FakeModel model = new(preferredBatchSize: null);

        Assert.Equal(1, policy.ChooseBatchSize(model, 100));
        policy.RecordDispatch(model, batchSize: 1,
            vramBefore: 8 * GB, vramAfter: 8 * GB + 1 * GB, dispatchMs: 50);

        // Settled — next call still returns 1.
        Assert.Equal(1, policy.ChooseBatchSize(model, 100));
        // And recording another dispatch can't un-settle.
        policy.RecordDispatch(model, batchSize: 1,
            vramBefore: 8 * GB, vramAfter: 8 * GB + 1 * GB, dispatchMs: 50);
        Assert.Equal(1, policy.ChooseBatchSize(model, 100));
    }

    /// <summary>
    /// Model with a declared <see cref="IModel.PreferredBatchSize"/> ceiling
    /// settles at that ceiling regardless of VRAM headroom. Lets LLMs and
    /// other models with non-VRAM batch-size constraints cap themselves
    /// without the policy overriding.
    /// </summary>
    [Fact]
    public void Doubling_SettlesAtModelPreferredCeiling()
    {
        FakeVramProbe probe = new(usedBytes: 1 * GB, totalBytes: 24 * GB);
        DoublingBatchSizePolicy policy = new(probe);
        FakeModel model = new(preferredBatchSize: 2);

        Assert.Equal(1, policy.ChooseBatchSize(model, 100));
        policy.RecordDispatch(model, batchSize: 1,
            vramBefore: 1 * GB, vramAfter: 1 * GB + 10 * MB, dispatchMs: 50);

        Assert.Equal(2, policy.ChooseBatchSize(model, 100));
        policy.RecordDispatch(model, batchSize: 2,
            vramBefore: 1 * GB, vramAfter: 1 * GB + 20 * MB, dispatchMs: 100);

        // Already at the model's ceiling; doubling to 4 would exceed it.
        Assert.Equal(2, policy.ChooseBatchSize(model, 100));
    }

/// <summary>
    /// Probe unavailable (non-NVIDIA host, CPU EP, NVML not installed)
    /// degrades the doubling policy to static behaviour: same answer
    /// every call, no ramp, no measurement bookkeeping. Without this
    /// fallback the policy would peg every model at batch=1 on every
    /// non-CUDA machine.
    /// </summary>
    [Fact]
    public void Doubling_ProbeUnavailable_FallsBackToStatic()
    {
        FakeVramProbe probe = new(available: false);
        DoublingBatchSizePolicy policy = new(probe);

        FakeModel modelNull = new(preferredBatchSize: null);
        Assert.Equal(10, policy.ChooseBatchSize(modelNull, 10));

        FakeModel modelFour = new(preferredBatchSize: 4);
        Assert.Equal(4, policy.ChooseBatchSize(modelFour, 10));
        Assert.Equal(2, policy.ChooseBatchSize(modelFour, 2));
    }

    /// <summary>
    /// MIO passes <c>vramBefore = -1</c> / <c>vramAfter = -1</c> when the
    /// probe failed bracketing a specific dispatch. The policy must
    /// ignore those measurements (not record a bogus delta, not settle
    /// prematurely) so subsequent dispatches with real measurements can
    /// still drive the ramp.
    /// </summary>
    [Fact]
    public void Doubling_NegativeVramSentinels_IgnoreUpdate()
    {
        FakeVramProbe probe = new(usedBytes: 8 * GB, totalBytes: 24 * GB);
        DoublingBatchSizePolicy policy = new(probe);
        FakeModel model = new(preferredBatchSize: null);

        Assert.Equal(1, policy.ChooseBatchSize(model, 100));

        // First dispatch produced no measurement — sentinel pair.
        policy.RecordDispatch(model, batchSize: 1, vramBefore: -1, vramAfter: -1, dispatchMs: 50);

        // Still at 1: no ramp, no settle. Next dispatch retries.
        Assert.Equal(1, policy.ChooseBatchSize(model, 100));

        // Real measurement this time.
        policy.RecordDispatch(model, batchSize: 1,
            vramBefore: 8 * GB, vramAfter: 8 * GB + 10 * MB, dispatchMs: 50);

        Assert.Equal(2, policy.ChooseBatchSize(model, 100));
    }

    /// <summary>
    /// Duration-based spill detection: per-row dispatch time inflates
    /// non-linearly (≥ 2× best) when a dispatch falls off the model's
    /// VRAM cliff. The policy halves the batch and settles immediately
    /// without trying to grow again. Triggers on dispatch-time alone —
    /// no VRAM growth required, because shared-GPU-memory spill stays
    /// invisible to NVML.
    /// </summary>
    [Fact]
    public void Doubling_DurationJump_DetectsSpillAndShrinks()
    {
        FakeVramProbe probe = new(usedBytes: 8 * GB, totalBytes: 24 * GB);
        DoublingBatchSizePolicy policy = new(probe);
        FakeModel model = new(preferredBatchSize: null);

        // Healthy ramp: per-row time stays in a tight 50 ms band, so
        // BestMsPerRow drops as the batch grows (better amortisation).
        Assert.Equal(1, policy.ChooseBatchSize(model, 100));
        policy.RecordDispatch(model, batchSize: 1,
            vramBefore: 8 * GB, vramAfter: 8 * GB + 10 * MB, dispatchMs: 50);

        Assert.Equal(2, policy.ChooseBatchSize(model, 100));
        policy.RecordDispatch(model, batchSize: 2,
            vramBefore: 8 * GB, vramAfter: 8 * GB + 20 * MB, dispatchMs: 90);

        Assert.Equal(4, policy.ChooseBatchSize(model, 100));
        policy.RecordDispatch(model, batchSize: 4,
            vramBefore: 8 * GB, vramAfter: 8 * GB + 40 * MB, dispatchMs: 170);

        // Best per-row time so far: 42.5 ms (170 ms / 4). Now batch=8 hits
        // the cliff: 8 × 800 ms / 8 = 800 ms per row — 18× best. Spill
        // detection trips and the policy shrinks to 4 and settles.
        Assert.Equal(8, policy.ChooseBatchSize(model, 100));
        policy.RecordDispatch(model, batchSize: 8,
            vramBefore: 8 * GB, vramAfter: 8 * GB + 80 * MB, dispatchMs: 6400);

        Assert.Equal(4, policy.ChooseBatchSize(model, 100));
        // Settled — further dispatches don't change the batch even if
        // they're fast.
        policy.RecordDispatch(model, batchSize: 4,
            vramBefore: 8 * GB, vramAfter: 8 * GB + 40 * MB, dispatchMs: 170);
        Assert.Equal(4, policy.ChooseBatchSize(model, 100));
    }

    // ──────────────────────── Test doubles ───────────────────────────

    /// <summary>
    /// Mutable <see cref="IVramProbe"/> stub. Tests mutate
    /// <see cref="UsedBytes"/> / <see cref="TotalBytes"/> mid-sequence to
    /// simulate sibling-model loads or other processes consuming VRAM.
    /// </summary>
    private sealed class FakeVramProbe : IVramProbe
    {
        public long UsedBytes { get; set; }
        public long TotalBytes { get; set; }
        public bool Available { get; set; }

        public FakeVramProbe(long usedBytes, long totalBytes)
        {
            UsedBytes = usedBytes;
            TotalBytes = totalBytes;
            Available = true;
        }

        public FakeVramProbe(bool available)
        {
            Available = available;
        }

        public bool TryGetUsage(out long usedBytes, out long totalBytes)
        {
            if (!Available)
            {
                usedBytes = 0;
                totalBytes = 0;
                return false;
            }
            usedBytes = UsedBytes;
            totalBytes = TotalBytes;
            return true;
        }
    }

    /// <summary>
    /// Minimal <see cref="IModel"/> stub — only properties the policy
    /// reads (<see cref="Name"/>, <see cref="PreferredBatchSize"/>) are
    /// implemented. The dispatch methods throw because the policy never
    /// calls them.
    /// </summary>
    private sealed class FakeModel : IModel
    {
        private static int _nextId;

        public FakeModel(int? preferredBatchSize)
        {
            // Per-instance unique name so the policy's keyed state doesn't
            // bleed across tests within a single test run.
            Name = $"fake_{System.Threading.Interlocked.Increment(ref _nextId)}";
            PreferredBatchSize = preferredBatchSize;
        }

        public string Name { get; }
        public int? PreferredBatchSize { get; }
        public bool IsDeterministic => true;
        public IReadOnlyList<DataKind> InputKinds { get; } = [DataKind.String];
        public DataKind OutputKind => DataKind.String;

        public Task<IReadOnlyList<ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
            CancellationToken cancellationToken)
            => throw new NotImplementedException("BatchSizePolicy tests never dispatch the model.");
    }
}

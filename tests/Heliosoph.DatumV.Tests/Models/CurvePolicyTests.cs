using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Models.Calibration;

namespace Heliosoph.DatumV.Tests.Models;

/// <summary>
/// Unit tests for <see cref="CurvePolicy"/>. Each test wires a fresh
/// <see cref="CalibrationRegistry"/> + <see cref="IVramProbe"/> stub and
/// builds <see cref="ModelCalibration"/> entries directly so the policy's
/// curve-lookup logic is exercised without going through the calibration
/// coordinator.
/// </summary>
public sealed class CurvePolicyTests
{
    private const long GB = 1024L * 1024 * 1024;
    private const long MB = 1024L * 1024;

    [Fact]
    public void Uncalibrated_ReturnsOne()
    {
        CalibrationRegistry registry = new();
        FakeVramProbe probe = new(usedBytes: 4 * GB, totalBytes: 24 * GB);
        CurvePolicy policy = new(registry, probe);
        FakeModel model = new("alpha");

        // No entry registered → falls back to batch=1.
        Assert.Equal(1, policy.ChooseBatchSize(model, 100));
    }

    [Fact]
    public void Stale_ReturnsOne()
    {
        CalibrationRegistry registry = new();
        ModelCalibration cal = new("xxh64:abc");
        cal.Record(16, totalVramBytes: 4 * GB, DateTimeOffset.UtcNow);
        cal.MarkCalibrated();
        // Drop via spill — flips to Stale.
        cal.RecordSpill(16);
        registry.Replace("alpha", cal);

        FakeVramProbe probe = new(usedBytes: 4 * GB, totalBytes: 24 * GB);
        CurvePolicy policy = new(registry, probe);
        FakeModel model = new("alpha");

        Assert.Equal(1, policy.ChooseBatchSize(model, 100));
    }

    [Fact]
    public void ProbeUnavailable_ReturnsOne()
    {
        CalibrationRegistry registry = new();
        ModelCalibration cal = new("xxh64:abc");
        cal.Record(8, totalVramBytes: 1 * GB, DateTimeOffset.UtcNow);
        cal.MarkCalibrated();
        registry.Replace("alpha", cal);

        FakeVramProbe probe = new(available: false);
        CurvePolicy policy = new(registry, probe);
        FakeModel model = new("alpha");

        Assert.Equal(1, policy.ChooseBatchSize(model, 100));
    }

    [Fact]
    public void CalibratedWithHeadroom_PicksLargestFitting()
    {
        CalibrationRegistry registry = new();
        ModelCalibration cal = new("xxh64:abc");
        // Curve: 1 → 100 MB, 8 → 800 MB, 32 → 3 GB.
        cal.Record(1,  100 * MB, DateTimeOffset.UtcNow);
        cal.Record(8,  800 * MB, DateTimeOffset.UtcNow);
        cal.Record(32, 3 * GB,    DateTimeOffset.UtcNow);
        cal.MarkCalibrated();
        registry.Replace("alpha", cal);

        // 24 GB total, 4 GB used → 20 GB free. Safety = max(512 MB, 2.4 GB) = 2.4 GB.
        // Available marginal = 20 GB - 2.4 GB = 17.6 GB. All three entries fit;
        // pick the largest (32).
        FakeVramProbe probe = new(usedBytes: 4 * GB, totalBytes: 24 * GB);
        CurvePolicy policy = new(registry, probe);
        FakeModel model = new("alpha");

        Assert.Equal(32, policy.ChooseBatchSize(model, 100));
    }

    [Fact]
    public void CalibratedTightBudget_PicksSmallerEntry()
    {
        CalibrationRegistry registry = new();
        ModelCalibration cal = new("xxh64:abc");
        cal.Record(1, 100 * MB, DateTimeOffset.UtcNow);
        cal.Record(8, 5 * GB,   DateTimeOffset.UtcNow);
        cal.Record(32, 20 * GB, DateTimeOffset.UtcNow);
        cal.MarkCalibrated();
        registry.Replace("alpha", cal);

        // 24 GB total, 18 GB used → 6 GB free. Safety = 2.4 GB. Available
        // marginal = 3.6 GB. batch=32 (20 GB) and batch=8 (5 GB) don't fit;
        // batch=1 (100 MB) does.
        FakeVramProbe probe = new(usedBytes: 18 * GB, totalBytes: 24 * GB);
        CurvePolicy policy = new(registry, probe);
        FakeModel model = new("alpha");

        Assert.Equal(1, policy.ChooseBatchSize(model, 100));
    }

    [Fact]
    public void RowsRemainingClampsBatch()
    {
        CalibrationRegistry registry = new();
        ModelCalibration cal = new("xxh64:abc");
        cal.Record(32, 100 * MB, DateTimeOffset.UtcNow);
        cal.MarkCalibrated();
        registry.Replace("alpha", cal);

        FakeVramProbe probe = new(usedBytes: 4 * GB, totalBytes: 24 * GB);
        CurvePolicy policy = new(registry, probe);
        FakeModel model = new("alpha");

        // Curve says 32 fits, but only 5 rows left.
        Assert.Equal(5, policy.ChooseBatchSize(model, 5));
    }

    [Fact]
    public void RecordDispatch_DoesNotModifyCurveOnSnapshotMeasurements()
    {
        // With absolute-totals semantics, RecordDispatch no longer
        // refines the curve from per-query snapshot readings — a single
        // online dispatch can't produce an absolute total (ORT's arena
        // hides allocation growth between same-shape dispatches), so
        // any "update" would corrupt the calibrated value. The entry's
        // observation_count therefore stays pinned at 1 from the ramp.
        CalibrationRegistry registry = new();
        ModelCalibration cal = new("xxh64:abc");
        cal.Record(8, 1 * GB, DateTimeOffset.UtcNow);
        cal.MarkCalibrated();
        registry.Replace("alpha", cal);

        FakeVramProbe probe = new(usedBytes: 4 * GB, totalBytes: 24 * GB);
        CurvePolicy policy = new(registry, probe);
        FakeModel model = new("alpha");

        policy.RecordDispatch(model, batchSize: 8,
            vramBefore: 4 * GB, vramAfter: 4 * GB + (long)(1.05 * GB),
            dispatchMs: 200);

        Assert.Equal(1, cal.Curve[8].ObservationCount);
        Assert.Equal(1 * GB, cal.Curve[8].TotalVramBytes);
    }

    [Fact]
    public void RecordDispatch_DurationSpill_RecordsSpillAndMarksStale()
    {
        CalibrationRegistry registry = new();
        ModelCalibration cal = new("xxh64:abc");
        cal.Record(8,  1 * GB, DateTimeOffset.UtcNow);
        cal.Record(16, 2 * GB, DateTimeOffset.UtcNow);
        cal.Record(32, 4 * GB, DateTimeOffset.UtcNow);
        cal.MarkCalibrated();
        registry.Replace("alpha", cal);

        FakeVramProbe probe = new(usedBytes: 4 * GB, totalBytes: 24 * GB);
        CurvePolicy policy = new(registry, probe);
        FakeModel model = new("alpha");

        // Establish a baseline: a clean dispatch at batch=16 (~50ms per row).
        policy.RecordDispatch(model, batchSize: 16,
            vramBefore: 4 * GB, vramAfter: 4 * GB + 2 * GB,
            dispatchMs: 800);  // 50 ms / row → bestMsPerRow = 50

        // Now a batch=32 dispatch that takes 4000 ms total (125 ms / row,
        // 2.5× best) — spill territory.
        policy.RecordDispatch(model, batchSize: 32,
            vramBefore: 4 * GB, vramAfter: 4 * GB + 4 * GB,
            dispatchMs: 4000);

        Assert.Equal(ModelCalibration.State.Stale, cal.Status);
        // The 32 entry (and anything above) should have been dropped.
        Assert.False(cal.Curve.ContainsKey(32));
        // Smaller known-good entries stay.
        Assert.True(cal.Curve.ContainsKey(8));
        Assert.True(cal.Curve.ContainsKey(16));
    }

    // ─── Stubs (mirrored from BatchSizePolicyTests so this file stays standalone) ───

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

        public FakeVramProbe(bool available) { Available = available; }

        public bool TryGetUsage(out long usedBytes, out long totalBytes)
        {
            if (!Available) { usedBytes = 0; totalBytes = 0; return false; }
            usedBytes = UsedBytes;
            totalBytes = TotalBytes;
            return true;
        }
    }

    private sealed class FakeModel : IModel
    {
        public FakeModel(string name) { Name = name; }
        public string Name { get; }
        public bool IsDeterministic => true;
        public IReadOnlyList<DataKind> InputKinds { get; } = [DataKind.String];
        public DataKind OutputKind => DataKind.String;

        public Task<IReadOnlyList<ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
            CancellationToken cancellationToken)
            => throw new NotImplementedException("CurvePolicy tests never dispatch the model.");
    }
}

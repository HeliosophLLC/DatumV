using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Models.Calibration;

namespace Heliosoph.DatumV.Tests.Models.Calibration;

/// <summary>
/// Unit tests for <see cref="CalibrationCoordinator"/>. Tests use a real
/// <see cref="ModelCatalog"/> with entries that point at temp files (so
/// <see cref="ModelFileFingerprint"/> succeeds) and a fake dispatch
/// delegate so the coordinator can be exercised without a real ONNX
/// session.
/// </summary>
public sealed class CalibrationCoordinatorTests : IDisposable
{
    private readonly string _tempModelDir = Path.Combine(
        Path.GetTempPath(), $"calibration_coordinator_test_{Guid.NewGuid():N}");

    public CalibrationCoordinatorTests()
    {
        Directory.CreateDirectory(_tempModelDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempModelDir))
        {
            try { Directory.Delete(_tempModelDir, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FakeModel : IModel
    {
        public required string Name { get; init; }
        public bool IsDeterministic => true;
        public IReadOnlyList<DataKind> InputKinds => [DataKind.String];
        public DataKind OutputKind => DataKind.String;

        public Task<IReadOnlyList<ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
            CancellationToken cancellationToken)
            => throw new NotImplementedException("Coordinator tests don't invoke InferBatch directly.");
    }

    private string CreateFakeModelFile(string name, byte[]? bytes = null)
    {
        string relativePath = name + ".onnx";
        string absolute = Path.Combine(_tempModelDir, relativePath);
        File.WriteAllBytes(absolute, bytes ?? [0xAA, 0xBB, 0xCC, 0xDD]);
        return relativePath;
    }

    private ModelCatalogEntry MakeEntry(string name)
    {
        string relativePath = CreateFakeModelFile(name);
        return new ModelCatalogEntry(
            Name: name,
            Backend: "fake",
            RelativePath: relativePath,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => new FakeModel { Name = name });
    }

    [Fact]
    public async Task EnsureCalibratedAsync_FirstCall_RunsRampAndRecordsCurve()
    {
        using ModelCatalog catalog = new(_tempModelDir);
        catalog.Register(MakeEntry("alpha"));

        List<int> seenBatchSizes = [];
        await catalog.CalibrationCoordinator.EnsureCalibratedAsync(
            "alpha",
            batch => { seenBatchSizes.Add(batch); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal(CalibrationCoordinator.DefaultRampBatchSizes, seenBatchSizes);

        ModelCalibration? cal = catalog.CalibrationRegistry.Get("alpha");
        Assert.NotNull(cal);
        Assert.Equal(ModelCalibration.State.Calibrated, cal.Status);
        Assert.Equal(CalibrationCoordinator.DefaultRampBatchSizes.Count, cal.Curve.Count);
        foreach (int batch in CalibrationCoordinator.DefaultRampBatchSizes)
        {
            Assert.True(cal.Curve.ContainsKey(batch));
        }
    }

    [Fact]
    public async Task EnsureCalibratedAsync_DiscoverMaxBatchSizeReturnsOne_HaltsAfterFirstStep()
    {
        // Mirrors a fixed-batch-1 ONNX model (e.g. yolox_x with input
        // shape [1, 3, 640, 640]): the body can technically be invoked
        // for N rows but the underlying session can only dispatch one,
        // so the doubling ramp at 2..32 measures the same internal-
        // batch=1 cost over and over. The discovery callback returns
        // 1 from step 1 onward; the coordinator must halt the ramp.
        using ModelCatalog catalog = new(_tempModelDir);
        catalog.Register(MakeEntry("alpha-fixed1"));

        List<int> seenBatchSizes = [];
        await catalog.CalibrationCoordinator.EnsureCalibratedAsync(
            "alpha-fixed1",
            batch => { seenBatchSizes.Add(batch); return Task.CompletedTask; },
            CancellationToken.None,
            discoverMaxBatchSize: () => 1);

        Assert.Equal([1], seenBatchSizes);

        ModelCalibration? cal = catalog.CalibrationRegistry.Get("alpha-fixed1");
        Assert.NotNull(cal);
        Assert.Equal(ModelCalibration.State.Calibrated, cal.Status);
        Assert.Single(cal.Curve);
        Assert.True(cal.Curve.ContainsKey(1));
    }

    [Fact]
    public async Task EnsureCalibratedAsync_AlreadyCalibrated_SkipsDispatch()
    {
        using ModelCatalog catalog = new(_tempModelDir);
        catalog.Register(MakeEntry("beta"));

        int dispatchCount = 0;
        await catalog.CalibrationCoordinator.EnsureCalibratedAsync(
            "beta",
            _ => { Interlocked.Increment(ref dispatchCount); return Task.CompletedTask; },
            CancellationToken.None);

        int firstRunDispatches = dispatchCount;

        // Second call — already calibrated, dispatch should never fire.
        await catalog.CalibrationCoordinator.EnsureCalibratedAsync(
            "beta",
            _ => { Interlocked.Increment(ref dispatchCount); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal(firstRunDispatches, dispatchCount);
    }

    [Fact]
    public async Task EnsureCalibratedAsync_ConcurrentSameModel_RunsRampOnce()
    {
        using ModelCatalog catalog = new(_tempModelDir);
        catalog.Register(MakeEntry("gamma"));

        int dispatchCount = 0;
        Task gate = Task.Run(async () => { await Task.Delay(50); });

        Task<int> StartCalibrationWaitingOnGate()
        {
            return Task.Run(async () =>
            {
                await catalog.CalibrationCoordinator.EnsureCalibratedAsync(
                    "gamma",
                    async batch =>
                    {
                        await gate; // hold ramp open so concurrent callers definitely overlap
                        Interlocked.Increment(ref dispatchCount);
                    },
                    CancellationToken.None);
                return dispatchCount;
            });
        }

        Task<int> a = StartCalibrationWaitingOnGate();
        Task<int> b = StartCalibrationWaitingOnGate();
        Task<int> c = StartCalibrationWaitingOnGate();

        await Task.WhenAll(a, b, c);

        // Ramp ran once across the six batch sizes — not three times.
        Assert.Equal(CalibrationCoordinator.DefaultRampBatchSizes.Count, dispatchCount);
    }

    [Fact]
    public async Task EnsureCalibratedAsync_DifferentModels_SerializesRamps()
    {
        using ModelCatalog catalog = new(_tempModelDir);
        catalog.Register(MakeEntry("delta"));
        catalog.Register(MakeEntry("epsilon"));

        // Disable spill detection — under heavy parallel test load the
        // coordinator's per-row jitter check can fire on synthetic ramps
        // whose dispatchMs varies with OS scheduling rather than real VRAM
        // pressure, halting the ramp and leaving Status=Stale.
        catalog.CalibrationCoordinator.MinDispatchMsForSpillDetection =
            double.PositiveInfinity;

        int concurrentRamps = 0;
        int maxConcurrent = 0;
        object syncLock = new();

        async Task DispatchTracker(int batch)
        {
            int now = Interlocked.Increment(ref concurrentRamps);
            lock (syncLock) maxConcurrent = Math.Max(maxConcurrent, now);
            await Task.Delay(10);
            Interlocked.Decrement(ref concurrentRamps);
        }

        Task d = Task.Run(() => catalog.CalibrationCoordinator.EnsureCalibratedAsync(
            "delta", DispatchTracker, CancellationToken.None));
        Task e = Task.Run(() => catalog.CalibrationCoordinator.EnsureCalibratedAsync(
            "epsilon", DispatchTracker, CancellationToken.None));

        await Task.WhenAll(d, e);

        Assert.Equal(1, maxConcurrent);
        Assert.Equal(ModelCalibration.State.Calibrated, catalog.CalibrationRegistry.Get("delta")!.Status);
        Assert.Equal(ModelCalibration.State.Calibrated, catalog.CalibrationRegistry.Get("epsilon")!.Status);
    }

    [Fact]
    public async Task EnsureCalibratedAsync_DispatchThrows_ReleasesGateForNextCall()
    {
        using ModelCatalog catalog = new(_tempModelDir);
        catalog.Register(MakeEntry("zeta"));
        catalog.Register(MakeEntry("eta"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CalibrationCoordinator.EnsureCalibratedAsync(
                "zeta",
                _ => throw new InvalidOperationException("boom"),
                CancellationToken.None));

        // A second model must still be able to calibrate even though the
        // first ramp threw — the gate must release in the finally block.
        await catalog.CalibrationCoordinator.EnsureCalibratedAsync(
            "eta",
            _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(ModelCalibration.State.Calibrated,
            catalog.CalibrationRegistry.Get("eta")!.Status);
    }

    [Fact]
    public async Task EnsureCalibratedAsync_UnknownModel_Throws()
    {
        using ModelCatalog catalog = new(_tempModelDir);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CalibrationCoordinator.EnsureCalibratedAsync(
                "no-such-model",
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Contains("no catalog entry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureCalibratedAsync_SyntheticModel_Throws()
    {
        using ModelCatalog catalog = new(_tempModelDir);
        catalog.Register(new ModelCatalogEntry(
            Name: "synthetic",
            Backend: "echo",
            RelativePath: null,  // no file fingerprint possible
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => new FakeModel { Name = "synthetic" }));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CalibrationCoordinator.EnsureCalibratedAsync(
                "synthetic",
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Contains("RelativePath", ex.Message);
    }

    [Fact]
    public async Task EnsureCalibratedAsync_CallerCancellation_DoesNotKillInFlightRamp()
    {
        using ModelCatalog catalog = new(_tempModelDir);
        catalog.Register(MakeEntry("theta"));

        TaskCompletionSource rampStarted = new();
        TaskCompletionSource rampGate = new();

        // Caller A holds the ramp open via the dispatch delegate, then cancels.
        using CancellationTokenSource ctsA = new();
        Task aCall = Task.Run(() => catalog.CalibrationCoordinator.EnsureCalibratedAsync(
            "theta",
            async batch =>
            {
                if (batch == 1) rampStarted.SetResult();
                await rampGate.Task;
            },
            ctsA.Token));

        // Wait for the ramp to start, then cancel caller A.
        await rampStarted.Task;
        ctsA.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aCall);

        // Caller B awaits the SAME in-flight ramp — its wait must NOT be
        // affected by A's cancellation.
        Task bCall = Task.Run(() => catalog.CalibrationCoordinator.EnsureCalibratedAsync(
            "theta",
            _ => Task.CompletedTask, // delegate not used because ramp is already running
            CancellationToken.None));

        // Now release the gate; the ramp completes and B unblocks.
        rampGate.SetResult();
        await bCall;

        Assert.Equal(ModelCalibration.State.Calibrated,
            catalog.CalibrationRegistry.Get("theta")!.Status);
    }
}

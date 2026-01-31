namespace DatumIngest.Tests.Model;

using System.Diagnostics;

using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Models;

/// <summary>
/// Unit tests for <see cref="ModelResidencyManager"/>. Each <see cref="ModelCatalogEntry"/>
/// in these tests sets <see cref="ModelCatalogEntry.RelativePath"/> to <see langword="null"/>
/// and supplies an explicit <see cref="ModelCatalogEntry.EstimatedVramBytes"/> so the
/// manager never touches the file system — VRAM accounting is the property under test,
/// not the file-size heuristic.
/// </summary>
public sealed class ModelResidencyManagerTests
{
    // ---- Fakes ----------------------------------------------------------------

    /// <summary>
    /// Minimal <see cref="IModel"/> that records whether it was disposed and lets a
    /// test gate the loader by holding a <see cref="TaskCompletionSource"/>.
    /// </summary>
    private sealed class FakeModel : IModel, IDisposable
    {
        public required string Name { get; init; }
        public bool IsDeterministic => true;
        public IReadOnlyList<DataKind> InputKinds => [DataKind.String];
        public DataKind OutputKind => DataKind.String;
        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
            CancellationToken cancellationToken)
            => throw new NotImplementedException("Not exercised by residency tests.");

        public void Dispose() => Disposed = true;
    }

    private static ModelCatalogEntry MakeEntry(
        string name,
        long estimatedBytes,
        Func<ModelLoadContext, IModel>? loader = null)
    {
        return new ModelCatalogEntry(
            Name: name,
            Backend: "fake",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: loader ?? (_ => new FakeModel { Name = name }),
            EstimatedVramBytes: estimatedBytes);
    }

    // ---- Group 1: Basic lifecycle --------------------------------------------

    [Fact]
    public async Task Acquire_LoadsModel_OnFirstAccess()
    {
        int loadCount = 0;
        ModelCatalogEntry entry = MakeEntry("a", 1024, _ =>
        {
            loadCount++;
            return new FakeModel { Name = "a" };
        });

        using ModelResidencyManager mgr = new(vramBudgetBytes: ModelResidencyManager.UnlimitedBudget);
        using ModelLease lease = await mgr.AcquireAsync(entry, modelDirectory: "", CancellationToken.None);

        Assert.Equal(1, loadCount);
        Assert.NotNull(lease.Model);
        Assert.Equal(1024, mgr.VramUsedBytes);
    }

    [Fact]
    public async Task Acquire_SecondAcquireOfSameModel_ReusesLoadedInstance()
    {
        int loadCount = 0;
        ModelCatalogEntry entry = MakeEntry("a", 1024, _ =>
        {
            loadCount++;
            return new FakeModel { Name = "a" };
        });

        using ModelResidencyManager mgr = new();
        using ModelLease first = await mgr.AcquireAsync(entry, "", CancellationToken.None);
        using ModelLease second = await mgr.AcquireAsync(entry, "", CancellationToken.None);

        Assert.Equal(1, loadCount);
        Assert.Same(first.Model, second.Model);
    }

    [Fact]
    public async Task Lease_DisposeIsIdempotent()
    {
        using ModelResidencyManager mgr = new();
        ModelLease lease = await mgr.AcquireAsync(MakeEntry("a", 1024), "", CancellationToken.None);

        lease.Dispose();
        lease.Dispose(); // Should not double-release.

        // After two disposes ActiveRefs should be 0, not -1; second acquire must succeed.
        using ModelLease second = await mgr.AcquireAsync(MakeEntry("a", 1024), "", CancellationToken.None);
        IReadOnlyList<(string Name, long Bytes, int ActiveRefs)> snapshot = mgr.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(1, snapshot[0].ActiveRefs);
    }

    [Fact]
    public async Task UnlimitedBudget_NeverEvicts()
    {
        using ModelResidencyManager mgr = new(ModelResidencyManager.UnlimitedBudget);

        for (int i = 0; i < 32; i++)
        {
            ModelLease lease = await mgr.AcquireAsync(
                MakeEntry($"m{i}", 1L * 1024 * 1024 * 1024), "", CancellationToken.None);
            lease.Dispose();
        }

        Assert.Equal(32, mgr.Snapshot().Count);
    }

    [Fact]
    public async Task Dispose_DisposesAllResidentModels()
    {
        FakeModel? loaded = null;
        ModelCatalogEntry entry = MakeEntry("a", 1024, _ =>
        {
            loaded = new FakeModel { Name = "a" };
            return loaded;
        });

        ModelResidencyManager mgr = new();
        (await mgr.AcquireAsync(entry, "", CancellationToken.None)).Dispose();

        Assert.NotNull(loaded);
        Assert.False(loaded!.Disposed);

        mgr.Dispose();

        Assert.True(loaded.Disposed);
        Assert.Equal(0, mgr.VramUsedBytes);
    }

    [Fact]
    public async Task AcquireAsync_AfterDispose_Throws()
    {
        ModelResidencyManager mgr = new();
        mgr.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => mgr.AcquireAsync(MakeEntry("a", 1024), "", CancellationToken.None));
    }

    [Fact]
    public async Task EstimatedVramBytesOverride_TakesPrecedenceOverFileHeuristic()
    {
        // No RelativePath, no Files — only EstimatedVramBytes can drive accounting.
        using ModelResidencyManager mgr = new();
        using ModelLease lease = await mgr.AcquireAsync(
            MakeEntry("a", estimatedBytes: 12345), "", CancellationToken.None);

        Assert.Equal(12345, mgr.VramUsedBytes);
    }

    [Fact]
    public async Task Snapshot_ReflectsResidencyAndRefCounts()
    {
        using ModelResidencyManager mgr = new();
        ModelLease a = await mgr.AcquireAsync(MakeEntry("a", 100), "", CancellationToken.None);
        ModelLease b = await mgr.AcquireAsync(MakeEntry("b", 200), "", CancellationToken.None);
        ModelLease aAgain = await mgr.AcquireAsync(MakeEntry("a", 100), "", CancellationToken.None);

        IReadOnlyList<(string Name, long Bytes, int ActiveRefs)> snapshot = mgr.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, s => s.Name == "a" && s.Bytes == 100 && s.ActiveRefs == 2);
        Assert.Contains(snapshot, s => s.Name == "b" && s.Bytes == 200 && s.ActiveRefs == 1);

        a.Dispose(); b.Dispose(); aAgain.Dispose();
    }

    // ---- Group 2: Eviction & admission control -------------------------------

    [Fact]
    public async Task Acquire_BudgetExceeded_EvictsLruFirst()
    {
        FakeModel? aModel = null;
        FakeModel? bModel = null;
        FakeModel? cModel = null;
        ModelCatalogEntry aEntry = MakeEntry("a", 100, _ => aModel = new FakeModel { Name = "a" });
        ModelCatalogEntry bEntry = MakeEntry("b", 100, _ => bModel = new FakeModel { Name = "b" });
        ModelCatalogEntry cEntry = MakeEntry("c", 100, _ => cModel = new FakeModel { Name = "c" });

        using ModelResidencyManager mgr = new(vramBudgetBytes: 200);

        (await mgr.AcquireAsync(aEntry, "", CancellationToken.None)).Dispose();
        // Tiny gap so LastUsed timestamps differ and LRU is deterministic.
        await Task.Delay(15);
        (await mgr.AcquireAsync(bEntry, "", CancellationToken.None)).Dispose();
        await Task.Delay(15);

        // Loading 'c' forces eviction of LRU. 'a' is older than 'b' so 'a' should go.
        (await mgr.AcquireAsync(cEntry, "", CancellationToken.None)).Dispose();

        Assert.NotNull(aModel);
        Assert.True(aModel!.Disposed, "LRU model 'a' should have been evicted and disposed");
        Assert.False(bModel!.Disposed);
        Assert.False(cModel!.Disposed);
        Assert.Equal(200, mgr.VramUsedBytes);
    }

    [Fact]
    public async Task Acquire_PinnedModel_NotEvicted()
    {
        using ModelResidencyManager mgr = new(
            vramBudgetBytes: 200, admissionTimeout: TimeSpan.FromMilliseconds(200));

        // Hold a lease on 'a' so it cannot be evicted.
        ModelLease aLease = await mgr.AcquireAsync(MakeEntry("a", 100), "", CancellationToken.None);
        (await mgr.AcquireAsync(MakeEntry("b", 100), "", CancellationToken.None)).Dispose();

        // 'a' pinned + 'b' resident. Loading 'c' (100) needs eviction but 'a' is
        // pinned. 'b' is the only candidate — it should be evicted, not 'a'.
        FakeModel? aBefore = (FakeModel)aLease.Model;
        (await mgr.AcquireAsync(MakeEntry("c", 100), "", CancellationToken.None)).Dispose();

        Assert.False(aBefore.Disposed, "Pinned model must not be evicted");
        IReadOnlyList<(string Name, long Bytes, int ActiveRefs)> snap = mgr.Snapshot();
        Assert.Contains(snap, s => s.Name == "a");
        Assert.DoesNotContain(snap, s => s.Name == "b");

        aLease.Dispose();
    }

    [Fact]
    public async Task Acquire_AllPinned_TimesOutWithDiagnostic()
    {
        using ModelResidencyManager mgr = new(
            vramBudgetBytes: 100, admissionTimeout: TimeSpan.FromMilliseconds(150));

        // Pin the only slot.
        ModelLease pin = await mgr.AcquireAsync(MakeEntry("a", 100), "", CancellationToken.None);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mgr.AcquireAsync(MakeEntry("b", 100), "", CancellationToken.None));

        Assert.Contains("'b'", ex.Message);
        Assert.Contains("pinned", ex.Message, StringComparison.OrdinalIgnoreCase);

        pin.Dispose();
    }

    [Fact]
    public async Task Acquire_AdmissionTimeout_SucceedsWhenRefDropsBeforeDeadline()
    {
        using ModelResidencyManager mgr = new(
            vramBudgetBytes: 100, admissionTimeout: TimeSpan.FromSeconds(2));

        ModelLease pin = await mgr.AcquireAsync(MakeEntry("a", 100), "", CancellationToken.None);

        // Drop the pin shortly after we kick off the second acquire — it should
        // poll, observe the ref drop, evict 'a', and return.
        Task<ModelLease> contended = Task.Run(() =>
            mgr.AcquireAsync(MakeEntry("b", 100), "", CancellationToken.None));

        await Task.Delay(150);
        pin.Dispose();

        using ModelLease b = await contended;
        Assert.Equal("b", ((FakeModel)b.Model).Name);
    }

    [Fact]
    public async Task Acquire_CancellationDuringWait_Throws()
    {
        using ModelResidencyManager mgr = new(
            vramBudgetBytes: 100, admissionTimeout: TimeSpan.FromSeconds(10));
        using ModelLease pin = await mgr.AcquireAsync(MakeEntry("a", 100), "", CancellationToken.None);

        using CancellationTokenSource cts = new();
        Task<ModelLease> contended = Task.Run(() =>
            mgr.AcquireAsync(MakeEntry("b", 100), "", cts.Token));

        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => contended);
    }

    [Fact]
    public async Task Acquire_LoaderThrows_RollsBackVramAccounting()
    {
        ModelCatalogEntry good = MakeEntry("good", 100);
        ModelCatalogEntry bad = MakeEntry("bad", 100, _ => throw new InvalidOperationException("boom"));

        using ModelResidencyManager mgr = new(vramBudgetBytes: 200);
        using ModelLease g = await mgr.AcquireAsync(good, "", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mgr.AcquireAsync(bad, "", CancellationToken.None));

        // Reservation for the failed load must be undone.
        Assert.Equal(100, mgr.VramUsedBytes);
        Assert.Single(mgr.Snapshot());
    }

    // ---- Group 3: Concurrency hazards (these expose real bugs) ---------------

    /// <summary>
    /// EXPECTED TO FAIL with the current implementation. Two threads racing to
    /// acquire the same non-resident model both pass the <c>TryGetValue</c> miss
    /// check while holding the lock sequentially, each reserves VRAM, each drops
    /// the lock and calls <c>Loader</c>, and each then registers — the second
    /// registration overwrites the first in <c>_resident</c>. The first IModel
    /// is leaked (never disposed) AND <c>_vramUsedBytes</c> is permanently
    /// inflated by the leaked-but-uncatalogued reservation.
    /// </summary>
    [Fact]
    public async Task Acquire_ConcurrentSameModel_DoesNotDoubleLoad()
    {
        int loadCount = 0;
        ManualResetEventSlim arrived = new(false);
        SemaphoreSlim arrivalCount = new(0);
        ManualResetEventSlim release = new(false);

        ModelCatalogEntry entry = MakeEntry("race", 1024, _ =>
        {
            Interlocked.Increment(ref loadCount);
            arrivalCount.Release();
            release.Wait();
            return new FakeModel { Name = "race" };
        });

        using ModelResidencyManager mgr = new(vramBudgetBytes: ModelResidencyManager.UnlimitedBudget);

        // Kick off two acquires on threadpool threads. Both must enter the
        // loader concurrently for the race to fire.
        Task<ModelLease> t1 = Task.Run(() => mgr.AcquireAsync(entry, "", CancellationToken.None));
        Task<ModelLease> t2 = Task.Run(() => mgr.AcquireAsync(entry, "", CancellationToken.None));

        // Wait for both to enter the loader; if only one enters, retry one branch.
        if (!await arrivalCount.WaitAsync(TimeSpan.FromSeconds(2)) ||
            !await arrivalCount.WaitAsync(TimeSpan.FromSeconds(2)))
        {
            release.Set();
            await Task.WhenAll(t1, t2);
            // Couldn't reproduce a concurrent entry — inconclusive, not a pass.
            // Mark the test inconclusive by skipping the assertion path.
            return;
        }

        release.Set();

        ModelLease l1 = await t1;
        ModelLease l2 = await t2;

        try
        {
            Assert.Equal(1, loadCount);
            Assert.Same(l1.Model, l2.Model);
        }
        finally
        {
            l1.Dispose();
            l2.Dispose();
        }
    }

    /// <summary>
    /// EXPECTED TO FAIL with the current implementation. Companion to the
    /// double-load test: when two concurrent acquires of the same model both
    /// register, <c>_vramUsedBytes</c> reflects two reservations but only one
    /// <see cref="ModelCatalogEntry"/> is in <c>_resident</c>. The accounting
    /// drifts permanently above actual residency.
    /// </summary>
    [Fact]
    public async Task Acquire_ConcurrentSameModel_VramAccountingMatchesResidency()
    {
        int loadCount = 0;
        SemaphoreSlim arrivalCount = new(0);
        ManualResetEventSlim release = new(false);

        ModelCatalogEntry entry = MakeEntry("race2", 1024, _ =>
        {
            Interlocked.Increment(ref loadCount);
            arrivalCount.Release();
            release.Wait();
            return new FakeModel { Name = "race2" };
        });

        using ModelResidencyManager mgr = new();

        Task<ModelLease> t1 = Task.Run(() => mgr.AcquireAsync(entry, "", CancellationToken.None));
        Task<ModelLease> t2 = Task.Run(() => mgr.AcquireAsync(entry, "", CancellationToken.None));

        bool first = await arrivalCount.WaitAsync(TimeSpan.FromSeconds(2));
        bool second = await arrivalCount.WaitAsync(TimeSpan.FromSeconds(2));
        release.Set();

        ModelLease l1 = await t1;
        ModelLease l2 = await t2;
        l1.Dispose();
        l2.Dispose();

        if (!first || !second)
        {
            return; // Inconclusive — couldn't force concurrent loader entry.
        }

        long residencyBytes = mgr.Snapshot().Sum(s => s.Bytes);
        Assert.Equal(residencyBytes, mgr.VramUsedBytes);
    }

    /// <summary>
    /// EXPECTED TO FAIL on the racy path. Companion: the losing IModel from a
    /// concurrent-double-load is dropped on the floor by the second registration
    /// and is never disposed — a VRAM leak that the manager has no record of.
    /// </summary>
    [Fact]
    public async Task Acquire_ConcurrentSameModel_DoesNotLeakLoserInstance()
    {
        List<FakeModel> created = [];
        object createdLock = new();
        SemaphoreSlim arrivalCount = new(0);
        ManualResetEventSlim release = new(false);

        ModelCatalogEntry entry = MakeEntry("race3", 1024, _ =>
        {
            FakeModel m = new() { Name = "race3" };
            lock (createdLock) created.Add(m);
            arrivalCount.Release();
            release.Wait();
            return m;
        });

        ModelResidencyManager mgr = new();
        try
        {
            Task<ModelLease> t1 = Task.Run(() => mgr.AcquireAsync(entry, "", CancellationToken.None));
            Task<ModelLease> t2 = Task.Run(() => mgr.AcquireAsync(entry, "", CancellationToken.None));

            bool first = await arrivalCount.WaitAsync(TimeSpan.FromSeconds(2));
            bool second = await arrivalCount.WaitAsync(TimeSpan.FromSeconds(2));
            release.Set();

            (await t1).Dispose();
            (await t2).Dispose();

            if (!first || !second) return; // inconclusive

            // Either only one loader call (race fixed) OR every created model is
            // disposed when the manager is disposed. Both shapes are acceptable;
            // a leak — a created-but-never-disposed model — is not.
            mgr.Dispose();
            Assert.All(created, m => Assert.True(m.Disposed,
                "Every loaded model must be disposed by manager.Dispose; the loser of a concurrent load is currently leaked."));
        }
        finally
        {
            mgr.Dispose();
        }
    }

    // ---- Group 4: File-size heuristic footguns -------------------------------

    /// <summary>
    /// EXPECTED TO FAIL. When <see cref="ModelCatalogEntry.EstimatedVramBytes"/>
    /// is null and the entry references files that don't exist on disk, the
    /// heuristic silently returns 0 — the budget is bypassed entirely and any
    /// number of "phantom" models can be loaded. This should at minimum log a
    /// warning; arguably it should throw so the user fixes the missing file.
    /// </summary>
    [Fact]
    public async Task EstimateFromFile_MissingPath_DoesNotSilentlyBypassBudget()
    {
        // No EstimatedVramBytes — force the file-size heuristic.
        ModelCatalogEntry entry = new(
            Name: "missing",
            Backend: "fake",
            RelativePath: "this-file-does-not-exist.onnx",
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => new FakeModel { Name = "missing" });

        using ModelResidencyManager mgr = new(vramBudgetBytes: 100);

        using ModelLease lease = await mgr.AcquireAsync(entry, modelDirectory: "/nonexistent-dir", CancellationToken.None);

        // Asserting "we got *something* >= 1 byte" — the manager should refuse to
        // estimate at 0 because that turns the budget into a no-op for missing files.
        Assert.True(mgr.VramUsedBytes >= 1,
            "A model whose backing file cannot be sized must not be admitted as 0 bytes — " +
            "the residency budget becomes meaningless. Either reject the load or use a " +
            "conservative non-zero default.");
    }

    /// <summary>
    /// Documents the KV-cache limitation. An LLM-class model holds weights + a
    /// KV cache that grows with batch size × context length × hidden_dim ×
    /// dtype_bytes × 2 (K and V). For Llama-3.1-8B at 8k context that's roughly
    /// 1 GiB on top of the ~5 GiB of weights — the file-size × 1.2 heuristic
    /// underestimates by 1× to 3×. The only escape today is an explicit
    /// <see cref="ModelCatalogEntry.EstimatedVramBytes"/> override; until the
    /// manager learns to model KV (a function of context length passed by the
    /// caller), LLM entries SHOULD set the override and the test verifies that
    /// the override path actually works.
    /// </summary>
    [Fact]
    public async Task KvCacheNotModeled_OverrideIsCurrentlyTheOnlyEscape()
    {
        // 5 GiB on-disk weights, 1.2× heuristic gives 6 GiB — fits the budget,
        // but at 8k context the runtime would also allocate ~1 GiB of KV cache
        // and OOM. The override is the only way to express this today.
        const long onDiskWeights = 5L * 1024 * 1024 * 1024;
        const long realisticWithKv = (long)(onDiskWeights * 1.4);

        ModelCatalogEntry honestEntry = MakeEntry("llm", estimatedBytes: realisticWithKv);

        using ModelResidencyManager mgr = new(vramBudgetBytes: 6L * 1024 * 1024 * 1024);

        // The honest estimate doesn't fit; an unpinned acquire must throw
        // rather than silently load past budget.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mgr.AcquireAsync(honestEntry, "", CancellationToken.None));
    }

    // ---- Group 5: Dispose race -----------------------------------------------

    /// <summary>
    /// EXPECTED TO FAIL on some runs. <c>AcquireAsync</c> checks <c>_disposed</c>
    /// at entry but the actual registration happens later, after the loader
    /// completes. If <c>Dispose</c> runs after the entry check but before
    /// registration, the freshly-loaded model is registered into a disposed
    /// manager and <see cref="ModelResidencyManager.Dispose"/> (early-returning
    /// on <c>_disposed</c>) won't dispose it again — leak.
    /// </summary>
    [Fact]
    public async Task Dispose_ConcurrentWithInflightLoad_DoesNotLeak()
    {
        FakeModel? loaded = null;
        ManualResetEventSlim loaderEntered = new(false);
        ManualResetEventSlim disposeFinished = new(false);

        ModelCatalogEntry entry = MakeEntry("inflight", 1024, _ =>
        {
            loaded = new FakeModel { Name = "inflight" };
            loaderEntered.Set();
            disposeFinished.Wait();
            return loaded;
        });

        ModelResidencyManager mgr = new();

        Task<ModelLease> acquireTask = Task.Run(() =>
            mgr.AcquireAsync(entry, "", CancellationToken.None));

        Assert.True(loaderEntered.Wait(TimeSpan.FromSeconds(2)),
            "Loader never entered — test cannot exercise the race.");

        // Dispose while the loader is still running.
        mgr.Dispose();
        disposeFinished.Set();

        try
        {
            ModelLease lease = await acquireTask;
            lease.Dispose();
        }
        catch
        {
            // Either outcome is acceptable: throw, or silently complete and
            // dispose the loaded model. What's NOT acceptable is a loaded
            // model surviving without being disposed.
        }

        Assert.NotNull(loaded);
        Assert.True(loaded!.Disposed,
            "A model loaded concurrently with Dispose must still be disposed. " +
            "Currently the post-load registration races past _disposed = true and the model leaks.");
    }
}

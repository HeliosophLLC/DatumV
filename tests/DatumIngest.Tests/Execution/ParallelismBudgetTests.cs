using DatumIngest.Execution;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="ParallelismBudget"/>.
/// </summary>
public sealed class ParallelismBudgetTests : ServiceTestBase
{
    /// <summary>
    /// Verifies that the constructor rejects zero or negative max workers.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Constructor_RejectsInvalidMaxWorkers(int maxWorkers)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParallelismBudget(maxWorkers));
    }

    /// <summary>
    /// Verifies that <see cref="ParallelismBudget.MaxWorkers"/> reflects the
    /// value passed to the constructor.
    /// </summary>
    [Fact]
    public void MaxWorkers_ReflectsConstructorValue()
    {
        using ParallelismBudget budget = new(8);

        Assert.Equal(8, budget.MaxWorkers);
    }

    /// <summary>
    /// Verifies that all slots are available immediately after construction.
    /// </summary>
    [Fact]
    public void AvailableWorkers_EqualsMaxWorkersInitially()
    {
        using ParallelismBudget budget = new(4);

        Assert.Equal(4, budget.AvailableWorkers);
    }

    /// <summary>
    /// Verifies that acquiring slots decreases <see cref="ParallelismBudget.AvailableWorkers"/>
    /// and releasing restores them.
    /// </summary>
    [Fact]
    public async Task AcquireAndRelease_TracksAvailableWorkers()
    {
        using ParallelismBudget budget = new(4);

        int acquired = await budget.AcquireAsync(3, CancellationToken.None);

        Assert.Equal(3, acquired);
        Assert.Equal(1, budget.AvailableWorkers);

        budget.Release(3);

        Assert.Equal(4, budget.AvailableWorkers);
    }

    /// <summary>
    /// Verifies that acquiring all slots blocks a subsequent acquisition until
    /// slots are released.
    /// </summary>
    [Fact]
    public async Task AcquireAsync_BlocksWhenBudgetExhausted()
    {
        using ParallelismBudget budget = new(2);

        await budget.AcquireAsync(2, CancellationToken.None);
        Assert.Equal(0, budget.AvailableWorkers);

        // A third acquisition should not complete within the timeout.
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => budget.AcquireAsync(1, timeout.Token));

        // Available workers unchanged — nothing was acquired.
        Assert.Equal(0, budget.AvailableWorkers);

        // Release one slot — a new acquisition should now succeed.
        budget.Release(1);
        int acquired = await budget.AcquireAsync(1, CancellationToken.None);
        Assert.Equal(1, acquired);
    }

    /// <summary>
    /// Verifies that cancellation during a blocked acquisition throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    [Fact]
    public async Task AcquireAsync_ThrowsWhenCancelled()
    {
        using ParallelismBudget budget = new(1);
        await budget.AcquireAsync(1, CancellationToken.None);

        using CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => budget.AcquireAsync(1, cancellationTokenSource.Token));
    }

    /// <summary>
    /// Verifies that multiple concurrent acquisitions are serialized correctly
    /// and do not exceed the budget.
    /// </summary>
    [Fact]
    public async Task ConcurrentAcquisitions_RespectBudget()
    {
        using ParallelismBudget budget = new(2);
        int peakConcurrent = 0;
        int currentConcurrent = 0;

        async Task WorkerAsync()
        {
            await budget.AcquireAsync(1, CancellationToken.None);
            int current = Interlocked.Increment(ref currentConcurrent);
            InterlockedMax(ref peakConcurrent, current);
            await Task.Delay(20);
            Interlocked.Decrement(ref currentConcurrent);
            budget.Release(1);
        }

        Task[] workers = Enumerable.Range(0, 6).Select(_ => WorkerAsync()).ToArray();
        await Task.WhenAll(workers);

        Assert.True(peakConcurrent <= 2, $"Peak concurrent workers ({peakConcurrent}) exceeded budget of 2.");
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int current = Volatile.Read(ref location);
        while (value > current)
        {
            int previous = Interlocked.CompareExchange(ref location, value, current);
            if (previous == current)
            {
                break;
            }

            current = previous;
        }
    }

    // ─────────────── TryAcquire (non-blocking) ───────────────

    /// <summary>
    /// Verifies that <see cref="ParallelismBudget.TryAcquire"/> acquires up to
    /// the requested count when slots are available.
    /// </summary>
    [Fact]
    public void TryAcquire_AcquiresAvailableSlots()
    {
        using ParallelismBudget budget = new(4);

        int acquired = budget.TryAcquire(3);

        Assert.Equal(3, acquired);
        Assert.Equal(1, budget.AvailableWorkers);
    }

    /// <summary>
    /// Verifies that <see cref="ParallelismBudget.TryAcquire"/> returns fewer
    /// slots when fewer are available than requested.
    /// </summary>
    [Fact]
    public async Task TryAcquire_ReturnsPartialWhenInsufficient()
    {
        using ParallelismBudget budget = new(4);

        // Hold 3 of 4 slots.
        await budget.AcquireAsync(3, CancellationToken.None);

        // Try to acquire 3 more — only 1 should be available.
        int acquired = budget.TryAcquire(3);

        Assert.Equal(1, acquired);
        Assert.Equal(0, budget.AvailableWorkers);
    }

    /// <summary>
    /// Verifies that <see cref="ParallelismBudget.TryAcquire"/> returns 0
    /// immediately when no slots are available.
    /// </summary>
    [Fact]
    public async Task TryAcquire_ReturnsZeroWhenExhausted()
    {
        using ParallelismBudget budget = new(2);
        await budget.AcquireAsync(2, CancellationToken.None);

        int acquired = budget.TryAcquire(1);

        Assert.Equal(0, acquired);
    }

    /// <summary>
    /// Verifies that slots acquired via <see cref="ParallelismBudget.TryAcquire"/>
    /// can be released normally.
    /// </summary>
    [Fact]
    public void TryAcquire_ReleasedSlotsAreReturned()
    {
        using ParallelismBudget budget = new(4);

        int acquired = budget.TryAcquire(2);
        Assert.Equal(2, acquired);
        Assert.Equal(2, budget.AvailableWorkers);

        budget.Release(acquired);
        Assert.Equal(4, budget.AvailableWorkers);
    }
}

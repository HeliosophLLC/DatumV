using Heliosoph.DatumV.Execution;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Tests for <see cref="MemoryAccountant"/>: residency counter, budget enforcement,
/// idempotent disposal, and on-demand sampling.
/// </summary>
public sealed class MemoryAccountantTests
{
    [Fact]
    public void WouldExceedBudget_IsFalse_WhenBudgetIsNull()
    {
        using MemoryAccountant accountant = new(memoryBudgetBytes: null);
        accountant.NotifyMaterialized(1_000_000_000);

        Assert.False(accountant.WouldExceedBudget());
        Assert.False(accountant.WouldExceedBudget(pendingDelta: 1_000_000));
    }

    [Fact]
    public void WouldExceedBudget_TransitionsAtTheThreshold()
    {
        using MemoryAccountant accountant = new(memoryBudgetBytes: 1000);

        Assert.False(accountant.WouldExceedBudget());

        accountant.NotifyMaterialized(900);
        Assert.False(accountant.WouldExceedBudget());
        Assert.True(accountant.WouldExceedBudget(pendingDelta: 200));

        accountant.NotifyMaterialized(200);
        Assert.True(accountant.WouldExceedBudget());
    }

    [Fact]
    public void NotifyMaterialized_AndReleased_BalanceToZero()
    {
        using MemoryAccountant accountant = new();

        accountant.NotifyMaterialized(500);
        accountant.NotifyMaterialized(700);
        Assert.Equal(1200, accountant.CurrentResidentBytes);

        accountant.NotifyReleased(500);
        Assert.Equal(700, accountant.CurrentResidentBytes);

        accountant.NotifyReleased(700);
        Assert.Equal(0, accountant.CurrentResidentBytes);
    }

    [Fact]
    public void Notify_IsThreadSafe()
    {
        using MemoryAccountant accountant = new();

        const int perWorker = 10_000;
        const int workers = 8;

        Parallel.For(0, workers, _ =>
        {
            for (int i = 0; i < perWorker; i++)
            {
                accountant.NotifyMaterialized(1);
            }
        });

        Assert.Equal(workers * perWorker, accountant.CurrentResidentBytes);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        MemoryAccountant accountant = new();
        accountant.Dispose();
        accountant.Dispose();
        // No throw — Dispose can be called more than once safely.
    }

    [Fact]
    public void Sample_AppendsToProfile_UsingArenaProbe()
    {
        long arenaBytes = 0;
        using MemoryAccountant accountant = new(
            memoryBudgetBytes: null,
            arenaBytesProbe: () => arenaBytes);

        accountant.NotifyMaterialized(256);
        arenaBytes = 1024;

        accountant.Sample();

        MemorySample latest = accountant.Profile.Latest;
        Assert.Equal(256, latest.RowBytes);
        Assert.Equal(1024, latest.ArenaBytes);
        Assert.True(latest.ElapsedMs >= 0);
    }

    [Fact]
    public void Sample_WithoutArenaProbe_RecordsZeroArenaBytes()
    {
        using MemoryAccountant accountant = new();
        accountant.NotifyMaterialized(64);

        accountant.Sample();

        Assert.Equal(64, accountant.Profile.Latest.RowBytes);
        Assert.Equal(0, accountant.Profile.Latest.ArenaBytes);
    }
}

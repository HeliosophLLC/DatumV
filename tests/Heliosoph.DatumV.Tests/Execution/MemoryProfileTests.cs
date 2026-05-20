using Heliosoph.DatumV.Execution;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Tests for <see cref="MemoryProfile"/>: append, latest snapshot, and ordered
/// retrieval.
/// </summary>
public sealed class MemoryProfileTests
{
    [Fact]
    public void Latest_OnEmptyProfile_IsDefault()
    {
        MemoryProfile profile = new();

        MemorySample latest = profile.Latest;

        Assert.Equal(0, latest.ElapsedMs);
        Assert.Equal(0, latest.RowBytes);
        Assert.Equal(0, latest.ArenaBytes);
    }

    [Fact]
    public void Append_UpdatesLatest()
    {
        MemoryProfile profile = new();

        profile.Append(new MemorySample(100, 1024, 2048));

        MemorySample latest = profile.Latest;
        Assert.Equal(100, latest.ElapsedMs);
        Assert.Equal(1024, latest.RowBytes);
        Assert.Equal(2048, latest.ArenaBytes);
    }

    [Fact]
    public void Snapshot_PreservesAppendOrder()
    {
        MemoryProfile profile = new();
        profile.Append(new MemorySample(0, 1, 10));
        profile.Append(new MemorySample(1000, 2, 20));
        profile.Append(new MemorySample(2000, 3, 30));

        IReadOnlyList<MemorySample> snapshot = profile.Snapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(0, snapshot[0].ElapsedMs);
        Assert.Equal(1000, snapshot[1].ElapsedMs);
        Assert.Equal(2000, snapshot[2].ElapsedMs);
    }

    [Fact]
    public void Snapshot_IsStableAcrossSubsequentAppends()
    {
        MemoryProfile profile = new();
        profile.Append(new MemorySample(0, 1, 10));
        profile.Append(new MemorySample(1000, 2, 20));

        IReadOnlyList<MemorySample> snapshot = profile.Snapshot();
        profile.Append(new MemorySample(2000, 3, 30));

        Assert.Equal(2, snapshot.Count);
    }

    [Fact]
    public async Task Latest_IsThreadSafe()
    {
        MemoryProfile profile = new();

        const int writes = 1000;
        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < writes; i++)
            {
                profile.Append(new MemorySample(i, i * 2, i * 3));
            }
        });

        Task reader = Task.Run(() =>
        {
            for (int i = 0; i < writes; i++)
            {
                _ = profile.Latest;
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.Equal(writes - 1, profile.Latest.ElapsedMs);
    }
}

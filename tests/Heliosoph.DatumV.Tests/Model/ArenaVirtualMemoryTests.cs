using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Model;

/// <summary>
/// Tests specific to the anonymous <see cref="Arena"/> backing's reserve-once-commit-on-demand
/// model. The load-bearing property: the base pointer is stable for the arena's lifetime, so
/// spans held by concurrent readers do not dangle on a grow triggered by another thread.
/// </summary>
public sealed class ArenaVirtualMemoryTests : ServiceTestBase
{
    [Fact]
    public unsafe void Anonymous_Grow_PreservesBasePointer()
    {
        using Arena arena = CreateArena(initialCapacity: 1024 * 1024); // 1 MB

        arena.AppendBytes(new byte[100]);

        nint before;
        ReadOnlySpan<byte> firstView = arena.GetBytes(0, 1);
        fixed (byte* p = firstView) before = (nint)p;

        // Force several grows, well past the 1 MB initial commit.
        for (int i = 0; i < 5; i++)
        {
            arena.AppendBytes(new byte[2 * 1024 * 1024]);
        }

        nint after;
        ReadOnlySpan<byte> againView = arena.GetBytes(0, 1);
        fixed (byte* p = againView) after = (nint)p;

        Assert.Equal(before, after);
    }

    [Fact]
    public void Anonymous_Grow_PreservesContentsAtOriginalAddress()
    {
        using Arena arena = CreateArena(initialCapacity: 4096);

        // Sentinel near the start.
        byte[] sentinel = [0xDE, 0xAD, 0xBE, 0xEF];
        (long offset, int len) = arena.AppendBytes(sentinel);

        // Grow many times by pushing the position well past the initial commit.
        for (int i = 0; i < 8; i++)
        {
            arena.AppendBytes(new byte[1024 * 1024]);
        }

        // The sentinel must still be readable at the same offset, with identical bytes.
        // (The address is the same; the bytes were never moved.)
        Assert.Equal(sentinel, arena.GetBytes(offset, len).ToArray());
    }

    [Fact]
    public async Task Anonymous_ConcurrentReadsAcrossGrow_DoNotCrash()
    {
        // Reproduces the scenario that surfaced 2026-05-25: a parallel worker holds
        // a span over the arena while another worker's write triggers a grow. Under
        // the old mmap-and-remap design this AVE'd. With VA-reserve the pointer is
        // stable, so this loop must complete cleanly.
        using Arena arena = CreateArena(initialCapacity: 4096);

        // Pre-seed with a bunch of small payloads that workers will read.
        List<(long offset, int len)> seeded = [];
        for (int i = 0; i < 200; i++)
        {
            byte[] payload = new byte[64];
            for (int b = 0; b < payload.Length; b++) payload[b] = (byte)(i + b);
            seeded.Add(arena.AppendBytes(payload));
        }

        // Spin up workers that interleave reads (touching every byte of the span)
        // with writes large enough to force grows.
        int workers = Math.Max(4, Environment.ProcessorCount);
        Task[] tasks = new Task[workers];
        for (int t = 0; t < workers; t++)
        {
            int seed = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 500; i++)
                {
                    // Read: touch every byte so the span is actually dereferenced.
                    (long offset, int len) = seeded[(i + seed) % seeded.Count];
                    ReadOnlySpan<byte> view = arena.GetBytes(offset, len);
                    int sum = 0;
                    for (int b = 0; b < view.Length; b++) sum += view[b];
                    GC.KeepAlive(sum);

                    // Every few iterations, trigger a sizeable write to keep grows happening.
                    if ((i & 0x7) == 0)
                    {
                        arena.AppendBytes(new byte[8 * 1024]);
                    }
                }
            });
        }

        await Task.WhenAll(tasks);
        // No assertion needed — survival without AVE IS the assertion.
    }

    [Fact]
    public void Anonymous_LargeReservation_DoesNotConsumePhysicalMemory()
    {
        // Reservation is 8 GB per arena but only the initial commit (1 MB rounded
        // up to page granularity) is actually backed by physical memory. We can't
        // assert physical-RSS bookkeeping portably, but we CAN assert the cheap
        // operation: hundreds of arenas existing concurrently without OOM.
        Arena[] arenas = new Arena[256];
        try
        {
            for (int i = 0; i < arenas.Length; i++)
            {
                arenas[i] = CreateArena();
                // Trigger first commit so the reservation actually happens.
                arenas[i].AppendBytes([0x00]);
            }
        }
        finally
        {
            foreach (Arena a in arenas)
            {
                a?.Dispose();
            }
        }
    }

    [Fact]
    public void Anonymous_DisposeWithoutWrite_DoesNotThrow()
    {
        // Arena that never wrote anything — no reservation ever happened.
        Arena arena = CreateArena();
        arena.Dispose();
    }

    [Fact]
    public void Anonymous_DisposeReleasesReservation()
    {
        // Repeated allocate-and-dispose must not leak VA — if it did, this loop
        // would eventually fail to reserve. At 8 GB × 2000 iterations = 16 TB of
        // cumulative reservations, far more than process VA, so this only works
        // if Dispose actually frees the VA.
        for (int i = 0; i < 2000; i++)
        {
            using Arena arena = CreateArena();
            arena.AppendBytes([(byte)i]);
        }
    }

    [Fact]
    public void Anonymous_PointerStableAcrossReset()
    {
        // Reset is the pool-reuse path: position rewinds, mapping retained. The
        // base pointer must stay valid since the same arena instance may be
        // immediately rented out again.
        using Arena arena = CreateArena();
        arena.AppendBytes(new byte[1024]);

        nint before;
        unsafe
        {
            ReadOnlySpan<byte> view = arena.GetBytes(0, 1);
            fixed (byte* p = view) before = (nint)p;
        }

        arena.Reset();
        arena.AppendBytes(new byte[1024]);

        nint after;
        unsafe
        {
            ReadOnlySpan<byte> view = arena.GetBytes(0, 1);
            fixed (byte* p = view) after = (nint)p;
        }

        Assert.Equal(before, after);
    }
}

using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Indexing.Bitmap;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Indexing;

/// <summary>
/// Tests that accumulators correctly implement the "indexable = self-contained" rule:
/// only inline strings (≤16 UTF-8 bytes) and fixed-size scalars are retained; non-inline
/// reference-type values cause the column to be dropped from the index.
/// </summary>
public sealed class AccumulatorRetentionTests : ServiceTestBase
{
    // ───────────────────── ChunkAccumulator min/max ─────────────────────

    [Fact]
    public void ChunkAccumulator_InlineStringMin_RetainedAcrossBatches()
    {
        ChunkAccumulator accumulator = new();

        // Short (inline) strings are self-contained; retention works without arena plumbing.
        Arena batch1 = CreateArena();
        accumulator.Add(DataValue.FromString("abc", batch1));
        batch1.Dispose();

        Arena batch2 = CreateArena();
        accumulator.Add(DataValue.FromString("xyz", batch2));
        batch2.Dispose();

        ChunkColumnStatistics stats = accumulator.ToStatistics(rowCount: 2);
        Assert.NotNull(stats.Minimum);
        Assert.NotNull(stats.Maximum);
        Assert.Equal("abc", stats.Minimum!.Value.AsString());
        Assert.Equal("xyz", stats.Maximum!.Value.AsString());
    }

    [Fact]
    public void ChunkAccumulator_NonInlineString_InvalidatesMinMax()
    {
        ChunkAccumulator accumulator = new();

        Arena source = CreateArena();
        // A non-inline (>16 UTF-8 byte) string on any row invalidates min/max for the column.
        accumulator.Add(DataValue.FromString("abc", source));
        accumulator.Add(DataValue.FromString("this string is longer than sixteen bytes", source));
        accumulator.Add(DataValue.FromString("def", source));

        ChunkColumnStatistics stats = accumulator.ToStatistics(rowCount: 3);
        Assert.Null(stats.Minimum);
        Assert.Null(stats.Maximum);
    }

    [Fact]
    public void ChunkAccumulator_NumericMinMax_RetainedAcrossBatches()
    {
        ChunkAccumulator accumulator = new();

        Arena batch1 = CreateArena();
        accumulator.Add(DataValue.FromInt32(42));
        accumulator.Add(DataValue.FromInt32(7));
        batch1.Dispose();

        Arena batch2 = CreateArena();
        accumulator.Add(DataValue.FromInt32(100));
        batch2.Dispose();

        ChunkColumnStatistics stats = accumulator.ToStatistics(rowCount: 3);
        Assert.Equal(7, stats.Minimum!.Value.AsInt32());
        Assert.Equal(100, stats.Maximum!.Value.AsInt32());
    }

    [Fact]
    public void ChunkAccumulator_CardinalityStillTracked_ForNonInline()
    {
        // Cardinality estimation uses RawContentHash which is source-arena-independent,
        // so even non-inline strings still contribute to the estimate.
        ChunkAccumulator accumulator = new();

        Arena source = CreateArena();
        accumulator.Add(DataValue.FromString("longstringlongstringlongstring A", source));
        accumulator.Add(DataValue.FromString("longstringlongstringlongstring B", source));
        accumulator.Add(DataValue.FromString("longstringlongstringlongstring A", source));

        ChunkColumnStatistics stats = accumulator.ToStatistics(rowCount: 3);

        // Min/max invalidated (non-inline), but cardinality still observes distinctness.
        Assert.Null(stats.Minimum);
        Assert.InRange(stats.EstimatedCardinality, 1, 3); // HLL is approximate; tolerate ±.
    }

    // ───────────────────── BitmapChunkAccumulator ─────────────────────

    [Fact]
    public void BitmapChunkAccumulator_InlineStringKeys_DedupAcrossBatches()
    {
        BitmapChunkAccumulator accumulator = new();

        Arena batch1 = CreateArena();
        accumulator.BeginChunk(chunkRowCapacity: 1);
        accumulator.Add(DataValue.FromString("red", batch1), rowOffsetInChunk: 0);
        accumulator.FinalizeChunk(actualRowCount: 1);
        batch1.Dispose();

        Arena batch2 = CreateArena();
        accumulator.BeginChunk(chunkRowCapacity: 1);
        accumulator.Add(DataValue.FromString("red", batch2), rowOffsetInChunk: 0);
        accumulator.FinalizeChunk(actualRowCount: 1);
        batch2.Dispose();

        BitmapColumnIndex? index = accumulator.Build();
        Assert.NotNull(index);
        Assert.Single(index!.DistinctValues);
    }

    [Fact]
    public void BitmapChunkAccumulator_NonInlineString_AbandonsColumn()
    {
        BitmapChunkAccumulator accumulator = new();

        Arena source = CreateArena();
        accumulator.BeginChunk(chunkRowCapacity: 2);
        accumulator.Add(DataValue.FromString("short", source), rowOffsetInChunk: 0);
        // First non-inline string triggers abandonment of the whole column.
        accumulator.Add(DataValue.FromString("this string is longer than sixteen bytes", source), rowOffsetInChunk: 1);

        Assert.True(accumulator.IsAbandoned);

        BitmapColumnIndex? index = accumulator.Build();
        Assert.Null(index);
    }

    [Fact]
    public void BitmapChunkAccumulator_InlineOnly_BuildsCorrectly()
    {
        BitmapChunkAccumulator accumulator = new(cardinalityThreshold: 256);

        Arena source = CreateArena();
        accumulator.BeginChunk(chunkRowCapacity: 6);
        accumulator.Add(DataValue.FromString("red", source), rowOffsetInChunk: 0);
        accumulator.Add(DataValue.FromString("blue", source), rowOffsetInChunk: 1);
        accumulator.Add(DataValue.FromString("red", source), rowOffsetInChunk: 2);
        accumulator.Add(DataValue.FromString("green", source), rowOffsetInChunk: 3);
        accumulator.Add(DataValue.FromString("blue", source), rowOffsetInChunk: 4);
        accumulator.Add(DataValue.FromString("red", source), rowOffsetInChunk: 5);
        accumulator.FinalizeChunk(actualRowCount: 6);

        BitmapColumnIndex? index = accumulator.Build();
        Assert.NotNull(index);
        Assert.Equal(3, index!.DistinctValues.Count);

        ChunkBitmap red = index.GetChunkBitmap(DataValue.FromString("red", source), 0);
        Assert.Equal(3, red.PopCount());
    }
}

namespace DatumIngest.Tests.Statistics;

using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;

public sealed class HistogramAccumulatorTests
{
    [Fact]
    public void Add_SingleValue_SingleBin()
    {
        HistogramAccumulator accumulator = new();

        accumulator.Add(DataValue.FromFloat32(5.0f));

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;
        Assert.Single(result.Counts);
        Assert.Equal(1, result.Counts[0]);
    }

    [Fact]
    public void Add_UniformValues_DistributesAcrossBins()
    {
        HistogramAccumulator accumulator = new(binCount: 10);

        for (int i = 0; i <= 100; i++)
        {
            accumulator.Add(DataValue.FromFloat32(i));
        }

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;

        // Integer data (0–100) with 101 distinct values and 10 bins →
        // binWidth rounds up to 11, giving 10 bins with integer-aligned edges.
        Assert.Equal(0.0, result.BinEdges[0]);

        // All edges should be integers.
        foreach (double edge in result.BinEdges)
        {
            Assert.Equal(Math.Floor(edge), edge);
        }

        long totalInBins = result.Counts.Sum();
        Assert.Equal(101, totalInBins);
    }

    [Fact]
    public void Add_UInt8Values_TracksCorrectly()
    {
        HistogramAccumulator accumulator = new(binCount: 5);

        accumulator.Add(DataValue.FromUInt8(0));
        accumulator.Add(DataValue.FromUInt8(50));
        accumulator.Add(DataValue.FromUInt8(100));
        accumulator.Add(DataValue.FromUInt8(200));
        accumulator.Add(DataValue.FromUInt8(255));

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;
        Assert.Equal(5, result.Counts.Count);
        Assert.Equal(0.0, result.BinEdges[0]);

        // Integer-aligned binning: range 0–255 → 256 distinct values → binWidth = ceil(256/5) = 52
        // Last edge = 0 + 5 * 52 = 260 (extends past max to maintain integer alignment).
        Assert.Equal(260.0, result.BinEdges[^1]);

        long totalInBins = result.Counts.Sum();
        Assert.Equal(5, totalInBins);
    }

    [Fact]
    public void Add_IdenticalValues_HandledGracefully()
    {
        HistogramAccumulator accumulator = new(binCount: 10);

        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromFloat32(42.0f));
        }

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;
        Assert.Single(result.Counts);
        Assert.Equal(100, result.Counts[0]);
    }

    [Fact]
    public void Add_NullValues_Ignored()
    {
        HistogramAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.Float32));
        accumulator.Add(DataValue.FromFloat32(1.0f));

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;
        Assert.Equal(1, accumulator.TotalCount);
    }

    [Fact]
    public void Add_NoValues_EmptyHistogram()
    {
        HistogramAccumulator accumulator = new();

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;
        Assert.Empty(result.BinEdges);
        Assert.Empty(result.Counts);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        HistogramAccumulator accumulator = new();
        Assert.Equal("histogram", accumulator.GetResult().Name);
    }

    [Fact]
    public void Merge_TwoAccumulators_CombinesSamples()
    {
        HistogramAccumulator first = new(binCount: 5);
        HistogramAccumulator second = new(binCount: 5);

        for (int i = 0; i < 50; i++)
        {
            first.Add(DataValue.FromFloat32(i));
        }

        for (int i = 50; i < 100; i++)
        {
            second.Add(DataValue.FromFloat32(i));
        }

        first.Merge(second);
        HistogramResult result = (HistogramResult)first.GetResult().Value!;

        Assert.Equal(100, first.TotalCount);
        long totalInBins = result.Counts.Sum();
        Assert.Equal(100, totalInBins);
    }

    [Fact]
    public void GetResult_IntegerData_ProducesIntegerAlignedEdges()
    {
        HistogramAccumulator accumulator = new(binCount: 50);

        // Simulate age-like data: integers 17–90
        for (int age = 17; age <= 90; age++)
        {
            for (int i = 0; i < 10; i++)
            {
                accumulator.Add(DataValue.FromFloat32(age));
            }
        }

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;

        // All edges must be integers — no fractional bin boundaries.
        foreach (double edge in result.BinEdges)
        {
            Assert.True(Math.Floor(edge) == edge, $"Bin edge {edge} is not an integer.");
        }

        Assert.Equal(17.0, result.BinEdges[0]);

        long totalInBins = result.Counts.Sum();
        Assert.Equal(740, totalInBins);
    }

    [Fact]
    public void GetResult_FewDistinctIntegers_OneBinPerValue()
    {
        HistogramAccumulator accumulator = new(binCount: 50);

        // 5 distinct integers — fewer than bin count → one bin per integer.
        int[] values = [10, 20, 30, 40, 50];

        foreach (int value in values)
        {
            for (int i = 0; i < 100; i++)
            {
                accumulator.Add(DataValue.FromFloat32(value));
            }
        }

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;

        // Range is 10–50 = 41 distinct integers, which is < 50 bins → one bin per integer.
        Assert.Equal(41, result.Counts.Count);
        Assert.Equal(10.0, result.BinEdges[0]);
        Assert.Equal(51.0, result.BinEdges[^1]);

        long totalInBins = result.Counts.Sum();
        Assert.Equal(500, totalInBins);

        // Value 10 should be in the first bin (index 0), with count 100.
        Assert.Equal(100, result.Counts[0]);
        // Value 20 should be at index 10 (20 - 10 = 10).
        Assert.Equal(100, result.Counts[10]);
    }

    [Fact]
    public void GetResult_ContinuousData_UsesEqualWidthBins()
    {
        HistogramAccumulator accumulator = new(binCount: 10);

        // Non-integer values — should use equal-width continuous binning.
        accumulator.Add(DataValue.FromFloat32(0.1f));
        accumulator.Add(DataValue.FromFloat32(0.5f));
        accumulator.Add(DataValue.FromFloat32(1.7f));
        accumulator.Add(DataValue.FromFloat32(3.14f));
        accumulator.Add(DataValue.FromFloat32(9.99f));

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;

        // Continuous data — edges need not be integers.
        Assert.Equal(5, result.Counts.Count);
        Assert.Equal(6, result.BinEdges.Count);

        long totalInBins = result.Counts.Sum();
        Assert.Equal(5, totalInBins);
    }

    [Fact]
    public void GetResult_MixedIntegerAndFractional_UsesContinuousBins()
    {
        HistogramAccumulator accumulator = new(binCount: 10);

        // Mix of integer and fractional — should detect as continuous.
        accumulator.Add(DataValue.FromFloat32(1.0f));
        accumulator.Add(DataValue.FromFloat32(2.0f));
        accumulator.Add(DataValue.FromFloat32(3.5f));
        accumulator.Add(DataValue.FromFloat32(4.0f));
        accumulator.Add(DataValue.FromFloat32(5.0f));

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;

        // One fractional value breaks integer detection → continuous binning.
        double lastEdge = result.BinEdges[^1];
        Assert.Equal(5.0, lastEdge, 0.01);

        long totalInBins = result.Counts.Sum();
        Assert.Equal(5, totalInBins);
    }

    /// <summary>
    /// Regression test: integer data spanning a very large range (e.g. MIMIC-III ROW_ID
    /// from 0 to 2,000,000+) caused <see cref="int"/> overflow in the bin-index calculation
    /// inside <c>BuildIntegerAlignedBins</c>. The <c>(int)binWidth</c> cast wrapped to a
    /// negative value, producing a negative array index and
    /// <see cref="IndexOutOfRangeException"/>.
    /// </summary>
    [Fact]
    public void GetResult_LargeIntegerRange_DoesNotThrowOverflow()
    {
        HistogramAccumulator accumulator = new(binCount: 20);

        // Simulate a column with integer IDs spanning 0 to 5,000,000.
        Random random = new(42);
        for (int i = 0; i < 1_000; i++)
        {
            double value = random.Next(0, 5_000_001);
            accumulator.Add(DataValue.FromFloat64(value));
        }

        // Must not throw IndexOutOfRangeException.
        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;

        Assert.True(result.BinEdges.Count >= 2, "Should have at least 2 bin edges.");
        Assert.True(result.Counts.Count >= 1, "Should have at least 1 bin.");
        Assert.Equal(1_000, result.Counts.Sum());
        Assert.True(result.IntegerValued, "All values are whole numbers.");
    }

    /// <summary>
    /// Reproduces the MIMIC-III NOTEEVENTS crash: de-identified dates shifted to
    /// year 2138 produce epoch values around 5.3 billion — well beyond
    /// <see cref="int.MaxValue"/>. The original <c>(int)</c> cast on
    /// <c>((long)sample - intMin)</c> and <c>(int)binWidth</c> caused overflow
    /// and a negative array index.
    /// </summary>
    [Fact]
    public void GetResult_EpochTimestampRange_DoesNotThrowOverflow()
    {
        HistogramAccumulator accumulator = new(binCount: 50);

        // Epoch seconds for de-identified dates spanning year 2100–2138.
        // These values exceed int.MaxValue (~2.1 billion).
        double baseEpoch = 4_102_444_800.0; // ~2100-01-01 UTC
        Random random = new(42);
        for (int i = 0; i < 500; i++)
        {
            double value = baseEpoch + random.Next(0, 1_200_000_000);
            accumulator.Add(DataValue.FromFloat64(value));
        }

        // Must not throw IndexOutOfRangeException.
        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;

        Assert.True(result.BinEdges.Count >= 2);
        Assert.Equal(500, result.Counts.Sum());
        Assert.True(result.IntegerValued);
    }

    /// <summary>
    /// Verifies correctness with an extremely wide integer range where <c>binWidth</c>
    /// exceeds <see cref="int.MaxValue"/>, ensuring the <see cref="long"/> division
    /// path in <c>BuildIntegerAlignedBins</c> produces valid bin indices.
    /// </summary>
    [Fact]
    public void GetResult_ExtremeIntegerRange_BinWidthExceedsIntMax()
    {
        HistogramAccumulator accumulator = new(binCount: 10);

        // Range: 0 to 10 billion. binWidth ≈ 1 billion, well above int.MaxValue.
        accumulator.Add(DataValue.FromFloat64(0.0));
        accumulator.Add(DataValue.FromFloat64(1_000_000_000.0));
        accumulator.Add(DataValue.FromFloat64(5_000_000_000.0));
        accumulator.Add(DataValue.FromFloat64(10_000_000_000.0));

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;

        Assert.True(result.BinEdges.Count >= 2);
        Assert.Equal(4, result.Counts.Sum());
        Assert.Equal(0.0, result.BinEdges[0]);
    }
}

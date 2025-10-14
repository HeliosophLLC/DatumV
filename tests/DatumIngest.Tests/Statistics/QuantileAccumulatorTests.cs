namespace DatumIngest.Tests.Statistics;

using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;

public sealed class QuantileAccumulatorTests : IDisposable
{
    private readonly Arena _arena = new();

    public void Dispose() => _arena.Dispose();

    [Fact]
    public void Add_SingleValue_AllPercentilesEqualThatValue()
    {
        QuantileAccumulator accumulator = new();

        accumulator.Add(DataValue.FromFloat32(42.0f), _arena);

        QuantileResult result = (QuantileResult)accumulator.GetResult().Value!;
        Assert.Equal(42.0, result.P01);
        Assert.Equal(42.0, result.P25);
        Assert.Equal(42.0, result.P50);
        Assert.Equal(42.0, result.P75);
        Assert.Equal(42.0, result.P99);
        Assert.Equal(0.0, result.Iqr);
        Assert.Equal(42.0, result.LowerFence);
        Assert.Equal(42.0, result.UpperFence);
        Assert.Equal(0, result.OutlierCount);
        Assert.Equal(0.0, result.OutlierRatio);
    }

    [Fact]
    public void Add_UniformDistribution_CorrectPercentiles()
    {
        QuantileAccumulator accumulator = new();

        // Add values 1 through 100
        for (int i = 1; i <= 100; i++)
        {
            accumulator.Add(DataValue.FromFloat32(i), _arena);
        }

        QuantileResult result = (QuantileResult)accumulator.GetResult().Value!;

        // Linear interpolation: index = p * (n-1)
        // P25: 0.25 * 99 = 24.75 → lerp(25, 26, 0.75) = 25.75
        Assert.Equal(25.75, result.P25, 1e-6);
        // P50: 0.50 * 99 = 49.5 → lerp(50, 51, 0.5) = 50.5
        Assert.Equal(50.5, result.P50, 1e-6);
        // P75: 0.75 * 99 = 74.25 → lerp(75, 76, 0.25) = 75.25
        Assert.Equal(75.25, result.P75, 1e-6);

        // IQR = 75.25 - 25.75 = 49.5
        Assert.Equal(49.5, result.Iqr, 1e-6);
        // LowerFence = 25.75 - 1.5 * 49.5 = -48.5
        Assert.Equal(-48.5, result.LowerFence, 1e-6);
        // UpperFence = 75.25 + 1.5 * 49.5 = 149.5
        Assert.Equal(149.5, result.UpperFence, 1e-6);
        // All values 1-100 are within [-48.5, 149.5]
        Assert.Equal(0, result.OutlierCount);
        Assert.Equal(0.0, result.OutlierRatio);
    }

    [Fact]
    public void Add_NullValues_AreSkipped()
    {
        QuantileAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);
        accumulator.Add(DataValue.FromFloat32(10.0f), _arena);
        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);
        accumulator.Add(DataValue.FromFloat32(20.0f), _arena);

        Assert.Equal(2, accumulator.SampleCount);
        QuantileResult result = (QuantileResult)accumulator.GetResult().Value!;
        // P01 with 2 samples: 0.01 * 1 = 0.01 → lerp(10, 20, 0.01) = 10.1
        Assert.Equal(10.1, result.P01, 1e-6);
        // P99 with 2 samples: 0.99 * 1 = 0.99 → lerp(10, 20, 0.99) = 19.9
        Assert.Equal(19.9, result.P99, 1e-6);
    }

    [Fact]
    public void Add_NonNumericValues_AreSkipped()
    {
        QuantileAccumulator accumulator = new();

        accumulator.Add(DataValue.FromString("hello", _arena), _arena);
        accumulator.Add(DataValue.FromFloat32(5.0f), _arena);

        Assert.Equal(1, accumulator.SampleCount);
        QuantileResult result = (QuantileResult)accumulator.GetResult().Value!;
        Assert.Equal(5.0, result.P50);
    }

    [Fact]
    public void Add_NoValues_AllPercentilesNaN()
    {
        QuantileAccumulator accumulator = new();

        QuantileResult result = (QuantileResult)accumulator.GetResult().Value!;
        Assert.True(double.IsNaN(result.P01));
        Assert.True(double.IsNaN(result.P25));
        Assert.True(double.IsNaN(result.P50));
        Assert.True(double.IsNaN(result.P75));
        Assert.True(double.IsNaN(result.P99));
        Assert.True(double.IsNaN(result.Iqr));
        Assert.True(double.IsNaN(result.LowerFence));
        Assert.True(double.IsNaN(result.UpperFence));
        Assert.Equal(0, result.OutlierCount);
        Assert.Equal(0.0, result.OutlierRatio);
    }

    [Fact]
    public void Add_UInt8Values_TracksCorrectly()
    {
        QuantileAccumulator accumulator = new();

        accumulator.Add(DataValue.FromUInt8(0), _arena);
        accumulator.Add(DataValue.FromUInt8(50), _arena);
        accumulator.Add(DataValue.FromUInt8(100), _arena);
        accumulator.Add(DataValue.FromUInt8(200), _arena);
        accumulator.Add(DataValue.FromUInt8(255), _arena);

        QuantileResult result = (QuantileResult)accumulator.GetResult().Value!;
        // P01 with 5 samples: 0.01 * 4 = 0.04 → lerp(0, 50, 0.04) = 2.0
        Assert.Equal(2.0, result.P01, 1e-6);
        // P50: 0.50 * 4 = 2.0 → exact index 2 → value 100
        Assert.Equal(100.0, result.P50, 1e-6);
        // P99: 0.99 * 4 = 3.96 → lerp(200, 255, 0.96) = 252.8
        Assert.Equal(252.8, result.P99, 1e-6);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        QuantileAccumulator accumulator = new();
        Assert.Equal("quantile", accumulator.GetResult().Name);
    }

    [Fact]
    public void ToString_FormatsReadably()
    {
        QuantileAccumulator accumulator = new();
        for (int i = 1; i <= 100; i++)
        {
            accumulator.Add(DataValue.FromFloat32(i), _arena);
        }

        QuantileResult result = (QuantileResult)accumulator.GetResult().Value!;
        string formatted = result.ToString();

        Assert.Contains("P50=", formatted);
        Assert.Contains("P25=", formatted);
        Assert.Contains("P75=", formatted);
    }

    [Fact]
    public void Add_TwoIdenticalValues_AllPercentilesEqual()
    {
        QuantileAccumulator accumulator = new();
        accumulator.Add(DataValue.FromFloat32(7.0f), _arena);
        accumulator.Add(DataValue.FromFloat32(7.0f), _arena);

        QuantileResult result = (QuantileResult)accumulator.GetResult().Value!;
        Assert.Equal(7.0, result.P01);
        Assert.Equal(7.0, result.P50);
        Assert.Equal(7.0, result.P99);
        Assert.Equal(0.0, result.Iqr);
        Assert.Equal(0, result.OutlierCount);
    }

    [Fact]
    public void IqrOutliers_WithKnownOutliers_DetectsCorrectly()
    {
        QuantileAccumulator accumulator = new();

        // Bulk of values: 10 through 90
        for (int i = 10; i <= 90; i++)
        {
            accumulator.Add(DataValue.FromFloat32(i), _arena);
        }

        // Add clear outliers well beyond what IQR fences allow
        accumulator.Add(DataValue.FromFloat32(-500.0f), _arena);
        accumulator.Add(DataValue.FromFloat32(600.0f), _arena);

        QuantileResult result = (QuantileResult)accumulator.GetResult().Value!;

        Assert.True(result.Iqr > 0);
        Assert.True(result.LowerFence < 10);
        Assert.True(result.UpperFence > 90);
        Assert.Equal(2, result.OutlierCount);
        Assert.True(result.OutlierRatio > 0);
    }

    [Fact]
    public void IqrOutliers_ValueOnFence_NotAnOutlier()
    {
        QuantileAccumulator accumulator = new();

        // 5 evenly spaced values: 0, 25, 50, 75, 100
        accumulator.Add(DataValue.FromFloat32(0.0f), _arena);
        accumulator.Add(DataValue.FromFloat32(25.0f), _arena);
        accumulator.Add(DataValue.FromFloat32(50.0f), _arena);
        accumulator.Add(DataValue.FromFloat32(75.0f), _arena);
        accumulator.Add(DataValue.FromFloat32(100.0f), _arena);

        QuantileResult result = (QuantileResult)accumulator.GetResult().Value!;

        // P25 and P75 computed via interpolation on 5 samples:
        // P25: 0.25 * 4 = 1.0 → index 1 → 25.0
        // P75: 0.75 * 4 = 3.0 → index 3 → 75.0
        // IQR = 50, LowerFence = 25 - 75 = -50, UpperFence = 75 + 75 = 150
        // All values [0, 25, 50, 75, 100] are within [-50, 150]
        Assert.Equal(0, result.OutlierCount);
    }

    [Fact]
    public void IqrOutliers_AllSameValues_NoOutliers()
    {
        QuantileAccumulator accumulator = new();

        for (int i = 0; i < 50; i++)
        {
            accumulator.Add(DataValue.FromFloat32(42.0f), _arena);
        }

        QuantileResult result = (QuantileResult)accumulator.GetResult().Value!;

        Assert.Equal(0.0, result.Iqr);
        Assert.Equal(42.0, result.LowerFence);
        Assert.Equal(42.0, result.UpperFence);
        Assert.Equal(0, result.OutlierCount);
        Assert.Equal(0.0, result.OutlierRatio);
    }

    [Fact]
    public void IqrOutliers_ToString_IncludesIqrFields()
    {
        QuantileAccumulator accumulator = new();
        for (int i = 1; i <= 100; i++)
        {
            accumulator.Add(DataValue.FromFloat32(i), _arena);
        }

        QuantileResult result = (QuantileResult)accumulator.GetResult().Value!;
        string formatted = result.ToString();

        Assert.Contains("IQR=", formatted);
        Assert.Contains("Fences=", formatted);
        Assert.Contains("Outliers=", formatted);
    }
}

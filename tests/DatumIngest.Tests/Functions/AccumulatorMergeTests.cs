using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="IAggregateAccumulator.Merge"/> on all built-in
/// aggregate function accumulators. Each test creates two accumulators,
/// feeds disjoint partitions of data, merges them, and verifies the result
/// matches a single accumulator that saw all data.
/// </summary>
public class AccumulatorMergeTests : ServiceTestBase
{
    private static readonly DatumIngest.Functions.InvocationFrame _testFrame = DatumIngest.Functions.InvocationFrame.Symmetric(new DatumIngest.Model.Arena());

    // ─────────────── COUNT ───────────────

    [Fact]
    public void Count_Merge_CountStar()
    {
        CountFunction function = new();
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Accumulate(ReadOnlySpan<DataValue>.Empty, in _testFrame);
        left.Accumulate(ReadOnlySpan<DataValue>.Empty, in _testFrame);
        right.Accumulate(ReadOnlySpan<DataValue>.Empty, in _testFrame);

        left.Merge(right, in _testFrame);

        Assert.Equal(3L, left.Result(in _testFrame).AsInt64());
    }

    [Fact]
    public void Count_Merge_WithNulls()
    {
        CountFunction function = new();
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        DataValue[] nonNull = [DataValue.FromFloat32(1f)];
        DataValue[] nullValue = [DataValue.Null(DataKind.Float32)];

        left.Accumulate(nonNull, in _testFrame);
        left.Accumulate(nullValue, in _testFrame);
        right.Accumulate(nonNull, in _testFrame);
        right.Accumulate(nonNull, in _testFrame);

        left.Merge(right, in _testFrame);

        // 1 non-null from left + 2 from right = 3
        Assert.Equal(3L, left.Result(in _testFrame).AsInt64());
    }

    // ─────────────── SUM ───────────────

    [Fact]
    public void Sum_Merge_CombinesValues()
    {
        SumFunction function = new();
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Accumulate([DataValue.FromFloat32(10f)], in _testFrame);
        left.Accumulate([DataValue.FromFloat32(20f)], in _testFrame);
        right.Accumulate([DataValue.FromFloat32(30f)], in _testFrame);

        left.Merge(right, in _testFrame);

        Assert.Equal(60f, left.Result(in _testFrame).AsFloat32());
    }

    [Fact]
    public void Sum_Merge_BothEmpty_ReturnsNull()
    {
        SumFunction function = new();
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Merge(right, in _testFrame);

        Assert.True(left.Result(in _testFrame).IsNull);
    }

    [Fact]
    public void Sum_Merge_OneEmpty_ReturnsOtherSum()
    {
        SumFunction function = new();
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        right.Accumulate([DataValue.FromFloat32(42f)], in _testFrame);

        left.Merge(right, in _testFrame);

        Assert.Equal(42f, left.Result(in _testFrame).AsFloat32());
    }

    // ─────────────── AVG ───────────────

    [Fact]
    public void Avg_Merge_ProducesCorrectAverage()
    {
        AvgFunction function = new();
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        // Left: 10, 20 → sum=30, count=2
        left.Accumulate([DataValue.FromFloat32(10f)], in _testFrame);
        left.Accumulate([DataValue.FromFloat32(20f)], in _testFrame);

        // Right: 30 → sum=30, count=1
        right.Accumulate([DataValue.FromFloat32(30f)], in _testFrame);

        left.Merge(right, in _testFrame);

        // Combined: sum=60, count=3 → avg=20
        Assert.Equal(20.0, left.Result(in _testFrame).AsFloat64());
    }

    [Fact]
    public void Avg_Merge_WithNulls()
    {
        AvgFunction function = new();
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Accumulate([DataValue.FromFloat32(10f)], in _testFrame);
        left.Accumulate([DataValue.Null(DataKind.Float32)], in _testFrame);
        right.Accumulate([DataValue.FromFloat32(30f)], in _testFrame);

        left.Merge(right, in _testFrame);

        // AVG of 10 and 30 = 20 (null skipped)
        Assert.Equal(20.0, left.Result(in _testFrame).AsFloat64());
    }

    // ─────────────── MIN ───────────────

    [Fact]
    public void Min_Merge_TakesGlobalMinimum()
    {
        MinFunction function = new();
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Accumulate([DataValue.FromFloat32(5f)], in _testFrame);
        left.Accumulate([DataValue.FromFloat32(15f)], in _testFrame);
        right.Accumulate([DataValue.FromFloat32(3f)], in _testFrame);
        right.Accumulate([DataValue.FromFloat32(10f)], in _testFrame);

        left.Merge(right, in _testFrame);

        Assert.Equal(3f, left.Result(in _testFrame).AsFloat32());
    }

    [Fact]
    public void Min_Merge_OneEmpty_ReturnsOtherMin()
    {
        MinFunction function = new();
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        right.Accumulate([DataValue.FromFloat32(7f)], in _testFrame);

        left.Merge(right, in _testFrame);

        Assert.Equal(7f, left.Result(in _testFrame).AsFloat32());
    }

    // ─────────────── MAX ───────────────

    [Fact]
    public void Max_Merge_TakesGlobalMaximum()
    {
        MaxFunction function = new();
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Accumulate([DataValue.FromFloat32(5f)], in _testFrame);
        left.Accumulate([DataValue.FromFloat32(15f)], in _testFrame);
        right.Accumulate([DataValue.FromFloat32(3f)], in _testFrame);
        right.Accumulate([DataValue.FromFloat32(20f)], in _testFrame);

        left.Merge(right, in _testFrame);

        Assert.Equal(20f, left.Result(in _testFrame).AsFloat32());
    }

    // ─────────────── VARIANCE ───────────────

    [Fact]
    public void Variance_Merge_MatchesSinglePassResult()
    {
        VarianceFunction function = new(usePopulation: false, name: "VARIANCE");

        // Single accumulator with all data.
        IAggregateAccumulator reference = function.CreateAccumulator();
        foreach (float value in new[] { 2f, 4f, 4f, 4f, 5f, 5f, 7f, 9f })
        {
            reference.Accumulate([DataValue.FromFloat32(value)], in _testFrame);
        }

        // Split into two partitions and merge.
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        foreach (float value in new[] { 2f, 4f, 4f, 4f })
        {
            left.Accumulate([DataValue.FromFloat32(value)], in _testFrame);
        }

        foreach (float value in new[] { 5f, 5f, 7f, 9f })
        {
            right.Accumulate([DataValue.FromFloat32(value)], in _testFrame);
        }

        left.Merge(right, in _testFrame);

        Assert.Equal(reference.Result(in _testFrame).AsFloat64(), left.Result(in _testFrame).AsFloat64(), precision: 6);
    }

    [Fact]
    public void VariancePopulation_Merge_MatchesSinglePassResult()
    {
        VarianceFunction function = new(usePopulation: true, name: "VAR_POP");

        IAggregateAccumulator reference = function.CreateAccumulator();
        foreach (float value in new[] { 2f, 4f, 4f, 4f, 5f, 5f, 7f, 9f })
        {
            reference.Accumulate([DataValue.FromFloat32(value)], in _testFrame);
        }

        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        foreach (float value in new[] { 2f, 4f, 4f })
        {
            left.Accumulate([DataValue.FromFloat32(value)], in _testFrame);
        }

        foreach (float value in new[] { 4f, 5f, 5f, 7f, 9f })
        {
            right.Accumulate([DataValue.FromFloat32(value)], in _testFrame);
        }

        left.Merge(right, in _testFrame);

        Assert.Equal(reference.Result(in _testFrame).AsFloat64(), left.Result(in _testFrame).AsFloat64(), precision: 6);
    }

    // ─────────────── STDDEV ───────────────

    [Fact]
    public void StandardDeviation_Merge_MatchesSinglePassResult()
    {
        StandardDeviationFunction function = new(usePopulation: false, name: "STDDEV");

        IAggregateAccumulator reference = function.CreateAccumulator();
        foreach (float value in new[] { 10f, 20f, 30f, 40f, 50f })
        {
            reference.Accumulate([DataValue.FromFloat32(value)], in _testFrame);
        }

        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        foreach (float value in new[] { 10f, 20f })
        {
            left.Accumulate([DataValue.FromFloat32(value)], in _testFrame);
        }

        foreach (float value in new[] { 30f, 40f, 50f })
        {
            right.Accumulate([DataValue.FromFloat32(value)], in _testFrame);
        }

        left.Merge(right, in _testFrame);

        Assert.Equal(reference.Result(in _testFrame).AsFloat64(), left.Result(in _testFrame).AsFloat64(), precision: 5);
    }

    // ─────────────── COVARIANCE ───────────────

    [Fact]
    public void Covariance_Merge_MatchesSinglePassResult()
    {
        CovarianceFunction function = new(usePopulation: false, name: "COVAR_SAMP");

        IAggregateAccumulator reference = function.CreateAccumulator();
        (float y, float x)[] data = [(1, 2), (3, 4), (5, 6), (7, 8), (9, 10)];

        foreach ((float y, float x) in data)
        {
            reference.Accumulate([DataValue.FromFloat32(y), DataValue.FromFloat32(x)], in _testFrame);
        }

        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        foreach ((float y, float x) in data[..2])
        {
            left.Accumulate([DataValue.FromFloat32(y), DataValue.FromFloat32(x)], in _testFrame);
        }

        foreach ((float y, float x) in data[2..])
        {
            right.Accumulate([DataValue.FromFloat32(y), DataValue.FromFloat32(x)], in _testFrame);
        }

        left.Merge(right, in _testFrame);

        Assert.Equal(reference.Result(in _testFrame).AsFloat64(), left.Result(in _testFrame).AsFloat64(), precision: 5);
    }

    // ─────────────── CORRELATION ───────────────

    [Fact]
    public void Correlation_Merge_MatchesSinglePassResult()
    {
        CorrelationFunction function = new();

        IAggregateAccumulator reference = function.CreateAccumulator();
        (float y, float x)[] data = [(1, 2), (3, 4), (5, 6), (7, 8), (9, 10)];

        foreach ((float y, float x) in data)
        {
            reference.Accumulate([DataValue.FromFloat32(y), DataValue.FromFloat32(x)], in _testFrame);
        }

        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        foreach ((float y, float x) in data[..3])
        {
            left.Accumulate([DataValue.FromFloat32(y), DataValue.FromFloat32(x)], in _testFrame);
        }

        foreach ((float y, float x) in data[3..])
        {
            right.Accumulate([DataValue.FromFloat32(y), DataValue.FromFloat32(x)], in _testFrame);
        }

        left.Merge(right, in _testFrame);

        Assert.Equal(reference.Result(in _testFrame).AsFloat64(), left.Result(in _testFrame).AsFloat64(), precision: 5);
    }

    // ─────────────── MEDIAN ───────────────

    [Fact]
    public void Median_Merge_CombinesAllValues()
    {
        MedianFunction function = new();
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Accumulate([DataValue.FromFloat32(1f)], in _testFrame);
        left.Accumulate([DataValue.FromFloat32(3f)], in _testFrame);
        right.Accumulate([DataValue.FromFloat32(5f)], in _testFrame);

        left.Merge(right, in _testFrame);

        // Median of [1, 3, 5] = 3
        Assert.Equal(3.0, left.Result(in _testFrame).AsFloat64());
    }

    // ─────────────── MODE ───────────────

    [Fact]
    public void Mode_Merge_CombinesFrequencies()
    {
        ModeFunction function = new();
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        // Left: A=2, B=1
        left.Accumulate([DataValue.FromString("A")], in _testFrame);
        left.Accumulate([DataValue.FromString("A")], in _testFrame);
        left.Accumulate([DataValue.FromString("B")], in _testFrame);

        // Right: B=3
        right.Accumulate([DataValue.FromString("B")], in _testFrame);
        right.Accumulate([DataValue.FromString("B")], in _testFrame);
        right.Accumulate([DataValue.FromString("B")], in _testFrame);

        left.Merge(right, in _testFrame);

        // Combined: A=2, B=4 → mode is B
        Assert.Equal("B", left.Result(in _testFrame).AsString(_testFrame.Target));
    }

    // ─────────────── ARRAY_AGG ───────────────

    [Fact]
    public void ArrayAggregate_Merge_CombinesElements()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Accumulate([DataValue.FromFloat32(1f)], in _testFrame);
        left.Accumulate([DataValue.FromFloat32(2f)], in _testFrame);
        right.Accumulate([DataValue.FromFloat32(3f)], in _testFrame);

        left.Merge(right, in _testFrame);

        DataValue result = left.Result(in _testFrame);
        Assert.Equal(DataKind.Array, result.Kind);
        ReadOnlySpan<DataValue> elements = result.AsArray(_testFrame.Target);
        Assert.Equal(3, elements.Length);
    }

    // ─────────────── STRING_AGG ───────────────

    [Fact]
    public void StringAggregate_Merge_CombinesValues()
    {
        StringAggregateFunction function = new();
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Accumulate([DataValue.FromString("a"), DataValue.FromString(",")], in _testFrame);
        left.Accumulate([DataValue.FromString("b"), DataValue.FromString(",")], in _testFrame);
        right.Accumulate([DataValue.FromString("c"), DataValue.FromString(",")], in _testFrame);

        left.Merge(right, in _testFrame);

        string result = left.Result(in _testFrame).AsString(_testFrame.Target);
        // Should contain all three values separated by commas.
        Assert.Contains("a", result);
        Assert.Contains("b", result);
        Assert.Contains("c", result);
    }

    // ─────────────── DISTINCT decorator ───────────────

    [Fact]
    public void DistinctSum_Merge_DeduplicatesAcrossPartitions()
    {
        SumFunction function = new();
        IAggregateAccumulator leftInner = function.CreateAccumulator();
        IAggregateAccumulator rightInner = function.CreateAccumulator();

        DistinctAccumulatorDecorator left = new(leftInner, argumentCount: 1, in _testFrame);
        DistinctAccumulatorDecorator right = new(rightInner, argumentCount: 1, in _testFrame);

        // Left sees: 1, 2, 3
        left.Accumulate([DataValue.FromFloat32(1f)], in _testFrame);
        left.Accumulate([DataValue.FromFloat32(2f)], in _testFrame);
        left.Accumulate([DataValue.FromFloat32(3f)], in _testFrame);

        // Right sees: 2, 3, 4 (overlapping with left)
        right.Accumulate([DataValue.FromFloat32(2f)], in _testFrame);
        right.Accumulate([DataValue.FromFloat32(3f)], in _testFrame);
        right.Accumulate([DataValue.FromFloat32(4f)], in _testFrame);

        left.Merge(right, in _testFrame);

        // SUM(DISTINCT) of {1, 2, 3, 4} = 10
        Assert.Equal(10f, left.Result(in _testFrame).AsFloat32());
    }

    [Fact]
    public void DistinctCount_Merge_DeduplicatesAcrossPartitions()
    {
        CountFunction function = new();
        IAggregateAccumulator leftInner = function.CreateAccumulator();
        IAggregateAccumulator rightInner = function.CreateAccumulator();

        DistinctAccumulatorDecorator left = new(leftInner, argumentCount: 1, in _testFrame);
        DistinctAccumulatorDecorator right = new(rightInner, argumentCount: 1, in _testFrame);

        left.Accumulate([DataValue.FromFloat32(1f)], in _testFrame);
        left.Accumulate([DataValue.FromFloat32(1f)], in _testFrame);
        left.Accumulate([DataValue.FromFloat32(2f)], in _testFrame);

        right.Accumulate([DataValue.FromFloat32(2f)], in _testFrame);
        right.Accumulate([DataValue.FromFloat32(3f)], in _testFrame);

        left.Merge(right, in _testFrame);

        // COUNT(DISTINCT) of {1, 2, 3} = 3
        Assert.Equal(3L, left.Result(in _testFrame).AsInt64());
    }

    // ─────────────── APPROX_MEDIAN ───────────────

    [Fact]
    public void ApproximateMedian_Merge_CombinesSamples()
    {
        ApproximateMedianFunction function = new();
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Accumulate([DataValue.FromFloat32(1f)], in _testFrame);
        left.Accumulate([DataValue.FromFloat32(3f)], in _testFrame);
        right.Accumulate([DataValue.FromFloat32(5f)], in _testFrame);

        left.Merge(right, in _testFrame);

        // Exact median of [1, 3, 5] = 3 (small sample, no approximation error)
        Assert.Equal(3.0, left.Result(in _testFrame).AsFloat64());
    }

    // ─────────────── Merge with empty accumulator ───────────────

    [Fact]
    public void Variance_Merge_OneEmpty_MatchesSingleAccumulator()
    {
        VarianceFunction function = new(usePopulation: false, name: "VARIANCE");

        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        foreach (float value in new[] { 2f, 4f, 6f, 8f })
        {
            left.Accumulate([DataValue.FromFloat32(value)], in _testFrame);
        }

        // Right is empty — merge should not change left's result.
        left.Merge(right, in _testFrame);

        IAggregateAccumulator reference = function.CreateAccumulator();
        foreach (float value in new[] { 2f, 4f, 6f, 8f })
        {
            reference.Accumulate([DataValue.FromFloat32(value)], in _testFrame);
        }

        Assert.Equal(reference.Result(in _testFrame).AsFloat64(), left.Result(in _testFrame).AsFloat64(), precision: 6);
    }

    [Fact]
    public void Correlation_Merge_OneEmpty_MatchesSingleAccumulator()
    {
        CorrelationFunction function = new();

        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        (float y, float x)[] data = [(1, 10), (2, 20), (3, 30)];
        foreach ((float y, float x) in data)
        {
            left.Accumulate([DataValue.FromFloat32(y), DataValue.FromFloat32(x)], in _testFrame);
        }

        left.Merge(right, in _testFrame);

        IAggregateAccumulator reference = function.CreateAccumulator();
        foreach ((float y, float x) in data)
        {
            reference.Accumulate([DataValue.FromFloat32(y), DataValue.FromFloat32(x)], in _testFrame);
        }

        Assert.Equal(reference.Result(in _testFrame).AsFloat64(), left.Result(in _testFrame).AsFloat64(), precision: 5);
    }
}

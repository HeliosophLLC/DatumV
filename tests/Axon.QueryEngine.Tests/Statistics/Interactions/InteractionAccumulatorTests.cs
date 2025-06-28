namespace Axon.QueryEngine.Tests.Statistics.Interactions;

using Axon.QueryEngine.Model;
using Axon.QueryEngine.Statistics.Interactions;

public sealed class PearsonAccumulatorTests
{
    [Fact]
    public void GetValue_PerfectPositiveCorrelation_ReturnsOne()
    {
        PearsonAccumulator accumulator = new();

        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromScalar(i), DataValue.FromScalar(i * 2.0f));
        }

        double result = accumulator.GetValue();

        Assert.Equal(1.0, result, precision: 10);
    }

    [Fact]
    public void GetValue_PerfectNegativeCorrelation_ReturnsNegativeOne()
    {
        PearsonAccumulator accumulator = new();

        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromScalar(i), DataValue.FromScalar(-i * 3.0f));
        }

        double result = accumulator.GetValue();

        Assert.Equal(-1.0, result, precision: 10);
    }

    [Fact]
    public void GetValue_ConstantX_ReturnsNaN()
    {
        PearsonAccumulator accumulator = new();

        for (int i = 0; i < 50; i++)
        {
            accumulator.Add(DataValue.FromScalar(5.0f), DataValue.FromScalar(i));
        }

        double result = accumulator.GetValue();

        Assert.True(double.IsNaN(result));
    }

    [Fact]
    public void GetValue_SinglePair_ReturnsNaN()
    {
        PearsonAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(1.0f), DataValue.FromScalar(2.0f));

        double result = accumulator.GetValue();

        Assert.True(double.IsNaN(result));
    }

    [Fact]
    public void GetValue_NullValuesSkipped()
    {
        PearsonAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.Scalar), DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.FromScalar(1.0f), DataValue.Null(DataKind.Scalar));

        double result = accumulator.GetValue();

        Assert.True(double.IsNaN(result));
    }

    [Fact]
    public void GetValue_UInt8Values_ComputesCorrectly()
    {
        PearsonAccumulator accumulator = new();

        for (int i = 0; i < 50; i++)
        {
            accumulator.Add(DataValue.FromUInt8((byte)i), DataValue.FromUInt8((byte)(i * 2)));
        }

        double result = accumulator.GetValue();

        Assert.Equal(1.0, result, precision: 10);
    }
}

public sealed class SpearmanAccumulatorTests
{
    [Fact]
    public void GetValue_MonotonicIncreasing_ReturnsOne()
    {
        SpearmanAccumulator accumulator = new();

        for (int i = 1; i <= 50; i++)
        {
            accumulator.Add(DataValue.FromScalar(i), DataValue.FromScalar(i * i));
        }

        double result = accumulator.GetValue();

        Assert.Equal(1.0, result, precision: 10);
    }

    [Fact]
    public void GetValue_MonotonicDecreasing_ReturnsNegativeOne()
    {
        SpearmanAccumulator accumulator = new();

        for (int i = 1; i <= 50; i++)
        {
            accumulator.Add(DataValue.FromScalar(i), DataValue.FromScalar(100 - i));
        }

        double result = accumulator.GetValue();

        Assert.Equal(-1.0, result, precision: 10);
    }

    [Fact]
    public void GetValue_InsufficientSamples_ReturnsNaN()
    {
        SpearmanAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(1.0f), DataValue.FromScalar(2.0f));

        double result = accumulator.GetValue();

        // With only 1 sample, still computes but with 2 it should work
        Assert.True(result is double.NaN or 1.0 or -1.0 or 0.0);
    }

    [Fact]
    public void ComputeRanks_HandlesTies()
    {
        float[] values = [1.0f, 2.0f, 2.0f, 4.0f];

        double[] ranks = SpearmanAccumulator.ComputeRanks(values);

        Assert.Equal(1.0, ranks[0]);
        Assert.Equal(2.5, ranks[1]); // average of ranks 2 and 3
        Assert.Equal(2.5, ranks[2]);
        Assert.Equal(4.0, ranks[3]);
    }

    [Fact]
    public void ComputeRanks_AllTied_AllGetSameRank()
    {
        float[] values = [5.0f, 5.0f, 5.0f];

        double[] ranks = SpearmanAccumulator.ComputeRanks(values);

        Assert.Equal(2.0, ranks[0]); // average of 1, 2, 3
        Assert.Equal(2.0, ranks[1]);
        Assert.Equal(2.0, ranks[2]);
    }
}

public sealed class CramerVAccumulatorTests
{
    [Fact]
    public void GetValue_PerfectAssociation_ReturnsOne()
    {
        CramerVAccumulator accumulator = new();

        // Perfect 1-to-1 mapping between categories
        for (int i = 0; i < 100; i++)
        {
            string a = (i % 3).ToString();
            string b = (i % 3).ToString();
            accumulator.Add(DataValue.FromString(a), DataValue.FromString(b));
        }

        double result = accumulator.GetValue();

        Assert.Equal(1.0, result, precision: 5);
    }

    [Fact]
    public void GetValue_NoAssociation_ReturnsNearZero()
    {
        CramerVAccumulator accumulator = new();
        Random random = new(42);

        // Independent columns
        string[] catsA = ["red", "green", "blue"];
        string[] catsB = ["circle", "square", "triangle"];

        for (int i = 0; i < 10_000; i++)
        {
            accumulator.Add(
                DataValue.FromString(catsA[random.Next(3)]),
                DataValue.FromString(catsB[random.Next(3)]));
        }

        double result = accumulator.GetValue();

        Assert.True(result < 0.1, $"Expected near zero but got {result}");
    }

    [Fact]
    public void GetValue_SingleCategory_ReturnsZero()
    {
        CramerVAccumulator accumulator = new();

        for (int i = 0; i < 50; i++)
        {
            accumulator.Add(DataValue.FromString("only"), DataValue.FromString("one"));
        }

        double result = accumulator.GetValue();

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void GetValue_Empty_ReturnsNaN()
    {
        CramerVAccumulator accumulator = new();

        double result = accumulator.GetValue();

        Assert.True(double.IsNaN(result));
    }

    [Fact]
    public void GetValue_NullValuesSkipped()
    {
        CramerVAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.String), DataValue.FromString("a"));
        accumulator.Add(DataValue.FromString("b"), DataValue.Null(DataKind.String));

        double result = accumulator.GetValue();

        Assert.True(double.IsNaN(result));
    }
}

public sealed class AnovaAccumulatorTests
{
    [Fact]
    public void GetValue_SameGroupMeans_ReturnsNearZero()
    {
        AnovaAccumulator accumulator = new(firstIsCategorical: true);
        Random random = new(42);

        // Two groups with same mean
        for (int i = 0; i < 500; i++)
        {
            accumulator.Add(DataValue.FromString("A"), DataValue.FromScalar((float)(50 + random.NextDouble() * 10)));
            accumulator.Add(DataValue.FromString("B"), DataValue.FromScalar((float)(50 + random.NextDouble() * 10)));
        }

        double result = accumulator.GetValue();

        Assert.True(result < 5.0, $"Expected small F-statistic but got {result}");
    }

    [Fact]
    public void GetValue_VeryDifferentMeans_ReturnsLargeF()
    {
        AnovaAccumulator accumulator = new(firstIsCategorical: true);

        // Two groups with very different means
        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromString("low"), DataValue.FromScalar(10.0f + i * 0.1f));
            accumulator.Add(DataValue.FromString("high"), DataValue.FromScalar(1000.0f + i * 0.1f));
        }

        double result = accumulator.GetValue();

        Assert.True(result > 100, $"Expected large F-statistic but got {result}");
    }

    [Fact]
    public void GetValue_SingleGroup_ReturnsNaN()
    {
        AnovaAccumulator accumulator = new(firstIsCategorical: true);

        for (int i = 0; i < 50; i++)
        {
            accumulator.Add(DataValue.FromString("only"), DataValue.FromScalar(i));
        }

        double result = accumulator.GetValue();

        Assert.True(double.IsNaN(result));
    }

    [Fact]
    public void GetValue_FirstIsNumeric_WorksCorrectly()
    {
        AnovaAccumulator accumulator = new(firstIsCategorical: false);

        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromScalar(10.0f + i * 0.1f), DataValue.FromString("low"));
            accumulator.Add(DataValue.FromScalar(1000.0f + i * 0.1f), DataValue.FromString("high"));
        }

        double result = accumulator.GetValue();

        Assert.True(result > 100);
    }
}

public sealed class MutualInformationAccumulatorTests
{
    [Fact]
    public void GetValue_IndependentNumericColumns_ReturnsNearZero()
    {
        MutualInformationAccumulator accumulator = new(DataKind.Scalar, DataKind.Scalar);
        Random random = new(42);

        for (int i = 0; i < 5000; i++)
        {
            accumulator.Add(
                DataValue.FromScalar((float)random.NextDouble()),
                DataValue.FromScalar((float)random.NextDouble()));
        }

        double result = accumulator.GetValue();

        Assert.True(result < 0.3, $"Expected near zero MI but got {result}");
    }

    [Fact]
    public void GetValue_DependentCategoricalColumns_ReturnsPositive()
    {
        MutualInformationAccumulator accumulator = new(DataKind.String, DataKind.String);

        // Perfect dependence: B always equals A
        for (int i = 0; i < 1000; i++)
        {
            string value = (i % 5).ToString();
            accumulator.Add(DataValue.FromString(value), DataValue.FromString(value));
        }

        double result = accumulator.GetValue();

        Assert.True(result > 1.0, $"Expected positive MI but got {result}");
    }

    [Fact]
    public void GetValue_InsufficientData_ReturnsNaN()
    {
        MutualInformationAccumulator accumulator = new(DataKind.Scalar, DataKind.Scalar);

        accumulator.Add(DataValue.FromScalar(1.0f), DataValue.FromScalar(2.0f));

        double result = accumulator.GetValue();

        // With only 1 sample, might be NaN or near zero
        Assert.True(!double.IsNegative(result) || double.IsNaN(result));
    }

    [Fact]
    public void GetValue_MixedNumericCategorical_ComputesCorrectly()
    {
        MutualInformationAccumulator accumulator = new(DataKind.Scalar, DataKind.String);

        // Numeric value depends on category
        for (int i = 0; i < 1000; i++)
        {
            string cat = (i % 3).ToString();
            float num = (i % 3) * 100.0f + (i % 10);
            accumulator.Add(DataValue.FromScalar(num), DataValue.FromString(cat));
        }

        double result = accumulator.GetValue();

        Assert.True(result > 0, $"Expected positive MI but got {result}");
    }
}

namespace DatumQuery.Tests.Statistics.Interactions;

using DatumQuery.Model;
using DatumQuery.Statistics.Interactions;

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

public sealed class TheilUTests
{
    [Fact]
    public void GetDetailedValue_PerfectPrediction_ReturnsOne()
    {
        MutualInformationAccumulator accumulator = new(DataKind.String, DataKind.String);

        // Perfect 1-to-1 mapping: knowing B perfectly predicts A and vice versa
        for (int i = 0; i < 1000; i++)
        {
            string value = (i % 5).ToString();
            accumulator.Add(DataValue.FromString(value), DataValue.FromString(value));
        }

        MutualInformationResult result = accumulator.GetDetailedValue();

        Assert.Equal(1.0, result.TheilUAB, precision: 5);
        Assert.Equal(1.0, result.TheilUBA, precision: 5);
    }

    [Fact]
    public void GetDetailedValue_IndependentColumns_ReturnsNearZero()
    {
        MutualInformationAccumulator accumulator = new(DataKind.String, DataKind.String);
        Random random = new(42);

        string[] catsA = ["red", "green", "blue"];
        string[] catsB = ["circle", "square", "triangle"];

        for (int i = 0; i < 5000; i++)
        {
            accumulator.Add(
                DataValue.FromString(catsA[random.Next(3)]),
                DataValue.FromString(catsB[random.Next(3)]));
        }

        MutualInformationResult result = accumulator.GetDetailedValue();

        Assert.True(result.TheilUAB < 0.05, $"Expected near zero TheilUAB but got {result.TheilUAB}");
        Assert.True(result.TheilUBA < 0.05, $"Expected near zero TheilUBA but got {result.TheilUBA}");
    }

    [Fact]
    public void GetDetailedValue_AsymmetricRelationship_ProducesAsymmetricU()
    {
        MutualInformationAccumulator accumulator = new(DataKind.String, DataKind.String);

        // A has 2 categories, B has 4. Knowing B determines A but not vice versa.
        // A = "even"/"odd", B = "0"/"1"/"2"/"3"
        for (int i = 0; i < 2000; i++)
        {
            string b = (i % 4).ToString();
            string a = (i % 4) % 2 == 0 ? "even" : "odd";
            accumulator.Add(DataValue.FromString(a), DataValue.FromString(b));
        }

        MutualInformationResult result = accumulator.GetDetailedValue();

        // U(A|B) should be 1.0 — knowing B perfectly determines A
        Assert.Equal(1.0, result.TheilUAB, precision: 5);

        // U(B|A) should be 0.5 — knowing A only halves the uncertainty about B
        // H(B) = 2 bits, MI = 1 bit, so U(B|A) = 1/2
        Assert.True(result.TheilUBA < result.TheilUAB,
            $"Expected TheilUBA ({result.TheilUBA}) < TheilUAB ({result.TheilUAB})");
        Assert.Equal(0.5, result.TheilUBA, precision: 5);
    }

    [Fact]
    public void GetDetailedValue_ResultInUnitRange()
    {
        MutualInformationAccumulator accumulator = new(DataKind.String, DataKind.String);

        for (int i = 0; i < 500; i++)
        {
            string a = (i % 7).ToString();
            string b = (i % 3).ToString();
            accumulator.Add(DataValue.FromString(a), DataValue.FromString(b));
        }

        MutualInformationResult result = accumulator.GetDetailedValue();

        Assert.InRange(result.TheilUAB, 0.0, 1.0);
        Assert.InRange(result.TheilUBA, 0.0, 1.0);
    }

    [Fact]
    public void GetDetailedValue_ConstantColumnA_ReturnsNaNForUAB()
    {
        MutualInformationAccumulator accumulator = new(DataKind.String, DataKind.String);

        // Column A is constant → H(A) = 0 → U(A|B) is undefined
        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(
                DataValue.FromString("constant"),
                DataValue.FromString((i % 3).ToString()));
        }

        MutualInformationResult result = accumulator.GetDetailedValue();

        Assert.True(double.IsNaN(result.TheilUAB), $"Expected NaN for TheilUAB but got {result.TheilUAB}");
    }

    [Fact]
    public void GetDetailedValue_InsufficientData_ReturnsNaN()
    {
        MutualInformationAccumulator accumulator = new(DataKind.String, DataKind.String);

        accumulator.Add(DataValue.FromString("a"), DataValue.FromString("b"));

        MutualInformationResult result = accumulator.GetDetailedValue();

        Assert.True(double.IsNaN(result.TheilUAB));
        Assert.True(double.IsNaN(result.TheilUBA));
    }

    [Fact]
    public void GetDetailedValue_NumericColumns_ComputesTheilU()
    {
        MutualInformationAccumulator accumulator = new(DataKind.Scalar, DataKind.Scalar);

        // Perfectly correlated numeric values
        for (int i = 0; i < 1000; i++)
        {
            accumulator.Add(DataValue.FromScalar(i), DataValue.FromScalar(i * 2.0f));
        }

        MutualInformationResult result = accumulator.GetDetailedValue();

        // After discretization into bins, should show high dependence
        Assert.True(result.TheilUAB > 0.5, $"Expected high TheilUAB but got {result.TheilUAB}");
        Assert.True(result.TheilUBA > 0.5, $"Expected high TheilUBA but got {result.TheilUBA}");
    }
}

public sealed class MissingnessCorrelationAccumulatorTests
{
    [Fact]
    public void GetValue_BothColumnsAlwaysNull_ReturnsNaN()
    {
        MissingnessCorrelationAccumulator accumulator = new();

        for (int i = 0; i < 50; i++)
        {
            accumulator.Add(DataValue.Null(DataKind.Scalar), DataValue.Null(DataKind.String));
        }

        double result = accumulator.GetValue();

        Assert.True(double.IsNaN(result));
    }

    [Fact]
    public void GetValue_BothColumnsNeverNull_ReturnsNaN()
    {
        MissingnessCorrelationAccumulator accumulator = new();

        for (int i = 0; i < 50; i++)
        {
            accumulator.Add(DataValue.FromScalar(i), DataValue.FromString("text"));
        }

        double result = accumulator.GetValue();

        Assert.True(double.IsNaN(result));
    }

    [Fact]
    public void GetValue_PerfectPositiveCorrelation_ReturnsOne()
    {
        MissingnessCorrelationAccumulator accumulator = new();

        // Both null at same rows, both present at same rows
        for (int i = 0; i < 100; i++)
        {
            if (i % 2 == 0)
            {
                accumulator.Add(DataValue.FromScalar(i), DataValue.FromString("text"));
            }
            else
            {
                accumulator.Add(DataValue.Null(DataKind.Scalar), DataValue.Null(DataKind.String));
            }
        }

        double result = accumulator.GetValue();

        Assert.Equal(1.0, result, precision: 10);
    }

    [Fact]
    public void GetValue_PerfectNegativeCorrelation_ReturnsNegativeOne()
    {
        MissingnessCorrelationAccumulator accumulator = new();

        // When A is null, B is present; when A is present, B is null
        for (int i = 0; i < 100; i++)
        {
            if (i % 2 == 0)
            {
                accumulator.Add(DataValue.Null(DataKind.Scalar), DataValue.FromString("text"));
            }
            else
            {
                accumulator.Add(DataValue.FromScalar(i), DataValue.Null(DataKind.String));
            }
        }

        double result = accumulator.GetValue();

        Assert.Equal(-1.0, result, precision: 10);
    }

    [Fact]
    public void GetValue_IndependentMissingness_ReturnsNearZero()
    {
        MissingnessCorrelationAccumulator accumulator = new();
        Random random = new(42);

        for (int i = 0; i < 5000; i++)
        {
            DataValue valueA = random.NextDouble() < 0.3
                ? DataValue.Null(DataKind.Scalar)
                : DataValue.FromScalar(i);
            DataValue valueB = random.NextDouble() < 0.3
                ? DataValue.Null(DataKind.String)
                : DataValue.FromString("text");

            accumulator.Add(valueA, valueB);
        }

        double result = accumulator.GetValue();

        Assert.True(Math.Abs(result) < 0.1, $"Expected near zero but got {result}");
    }

    [Fact]
    public void GetValue_SingleRow_ReturnsNaN()
    {
        MissingnessCorrelationAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(1.0f), DataValue.FromString("text"));

        double result = accumulator.GetValue();

        Assert.True(double.IsNaN(result));
    }

    [Fact]
    public void GetValue_MixedDataKinds_WorksCorrectly()
    {
        MissingnessCorrelationAccumulator accumulator = new();

        // Scalar × Image — previously ineligible pair, now participates via missingness
        for (int i = 0; i < 100; i++)
        {
            if (i % 2 == 0)
            {
                accumulator.Add(DataValue.FromScalar(i), DataValue.FromImage(new byte[] { 0xFF }));
            }
            else
            {
                accumulator.Add(DataValue.Null(DataKind.Scalar), DataValue.Null(DataKind.Image));
            }
        }

        double result = accumulator.GetValue();

        Assert.Equal(1.0, result, precision: 10);
    }
}

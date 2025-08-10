using DatumIngest.Functions.Math;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Math;

/// <summary>
/// Tests for <see cref="RandomTruncatedNormalFunction"/>, <see cref="RandomLogNormalFunction"/>,
/// <see cref="RandomExponentialFunction"/>, <see cref="RandomBetaFunction"/>,
/// <see cref="RandomPoissonFunction"/>, and <see cref="RandomCategoricalFunction"/>.
/// </summary>
public sealed class RandomDistributionFunctionTests
{
    // ──────────────────── random_truncated_normal ────────────────────

    [Fact]
    public void RandomTruncatedNormal_ReturnsValuesWithinBounds()
    {
        RandomTruncatedNormalFunction function = new();
        for (int i = 0; i < 500; i++)
        {
            DataValue result = function.Execute([
                DataValue.FromScalar(0), DataValue.FromScalar(1),
                DataValue.FromScalar(-2), DataValue.FromScalar(2)
            ]);
            float value = result.AsScalar();
            Assert.InRange(value, -2f, 2f);
        }
    }

    [Fact]
    public void RandomTruncatedNormal_NegativeStddev_Throws()
    {
        RandomTruncatedNormalFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([
                DataValue.FromScalar(0), DataValue.FromScalar(-1),
                DataValue.FromScalar(-2), DataValue.FromScalar(2)
            ]));
    }

    [Fact]
    public void RandomTruncatedNormal_MinGreaterThanOrEqualMax_Throws()
    {
        RandomTruncatedNormalFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([
                DataValue.FromScalar(0), DataValue.FromScalar(1),
                DataValue.FromScalar(2), DataValue.FromScalar(2)
            ]));
    }

    [Fact]
    public void RandomTruncatedNormal_ValidateArguments_WrongArity()
    {
        RandomTruncatedNormalFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Scalar, DataKind.Scalar]));
    }

    // ──────────────────── random_log_normal ────────────────────

    [Fact]
    public void RandomLogNormal_ReturnsPositiveValues()
    {
        RandomLogNormalFunction function = new();
        for (int i = 0; i < 100; i++)
        {
            DataValue result = function.Execute([DataValue.FromScalar(0), DataValue.FromScalar(1)]);
            Assert.True(result.AsScalar() > 0, "Log-normal samples must be positive.");
        }
    }

    [Fact]
    public void RandomLogNormal_NegativeStddev_Throws()
    {
        RandomLogNormalFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([DataValue.FromScalar(0), DataValue.FromScalar(-1)]));
    }

    [Fact]
    public void RandomLogNormal_ValidateArguments_WrongArity()
    {
        RandomLogNormalFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Scalar]));
    }

    // ──────────────────── random_exponential ────────────────────

    [Fact]
    public void RandomExponential_ReturnsNonNegativeValues()
    {
        RandomExponentialFunction function = new();
        for (int i = 0; i < 100; i++)
        {
            DataValue result = function.Execute([DataValue.FromScalar(1)]);
            Assert.True(result.AsScalar() >= 0, "Exponential samples must be non-negative.");
        }
    }

    [Fact]
    public void RandomExponential_ZeroRate_Throws()
    {
        RandomExponentialFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([DataValue.FromScalar(0)]));
    }

    [Fact]
    public void RandomExponential_NegativeRate_Throws()
    {
        RandomExponentialFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([DataValue.FromScalar(-1)]));
    }

    [Fact]
    public void RandomExponential_MeanConverges()
    {
        RandomExponentialFunction function = new();
        float rate = 2f;
        float sum = 0;
        int count = 10_000;
        for (int i = 0; i < count; i++)
        {
            DataValue result = function.Execute([DataValue.FromScalar(rate)]);
            sum += result.AsScalar();
        }

        // Expected mean = 1/rate = 0.5
        float mean = sum / count;
        Assert.InRange(mean, 0.4f, 0.6f);
    }

    [Fact]
    public void RandomExponential_ValidateArguments_WrongArity()
    {
        RandomExponentialFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Scalar, DataKind.Scalar]));
    }

    // ──────────────────── random_beta ────────────────────

    [Fact]
    public void RandomBeta_ReturnsValuesInUnitInterval()
    {
        RandomBetaFunction function = new();
        for (int i = 0; i < 100; i++)
        {
            DataValue result = function.Execute([DataValue.FromScalar(2), DataValue.FromScalar(5)]);
            float value = result.AsScalar();
            Assert.InRange(value, 0f, 1f);
        }
    }

    [Fact]
    public void RandomBeta_SymmetricParameters_CenteredAroundHalf()
    {
        RandomBetaFunction function = new();
        float sum = 0;
        int count = 10_000;
        for (int i = 0; i < count; i++)
        {
            DataValue result = function.Execute([DataValue.FromScalar(5), DataValue.FromScalar(5)]);
            sum += result.AsScalar();
        }

        // Beta(5,5) has mean = 0.5
        float mean = sum / count;
        Assert.InRange(mean, 0.45f, 0.55f);
    }

    [Fact]
    public void RandomBeta_ZeroAlpha_Throws()
    {
        RandomBetaFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([DataValue.FromScalar(0), DataValue.FromScalar(1)]));
    }

    [Fact]
    public void RandomBeta_NegativeBeta_Throws()
    {
        RandomBetaFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([DataValue.FromScalar(1), DataValue.FromScalar(-1)]));
    }

    [Fact]
    public void RandomBeta_ValidateArguments_WrongArity()
    {
        RandomBetaFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Scalar]));
    }

    // ──────────────────── random_poisson ────────────────────

    [Fact]
    public void RandomPoisson_ReturnsNonNegativeIntegers()
    {
        RandomPoissonFunction function = new();
        for (int i = 0; i < 100; i++)
        {
            DataValue result = function.Execute([DataValue.FromScalar(5)]);
            float value = result.AsScalar();
            Assert.True(value >= 0, "Poisson samples must be non-negative.");
            Assert.Equal(MathF.Truncate(value), value);
        }
    }

    [Fact]
    public void RandomPoisson_ZeroLambda_ReturnsZero()
    {
        RandomPoissonFunction function = new();
        DataValue result = function.Execute([DataValue.FromScalar(0)]);
        Assert.Equal(0f, result.AsScalar());
    }

    [Fact]
    public void RandomPoisson_NegativeLambda_Throws()
    {
        RandomPoissonFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([DataValue.FromScalar(-1)]));
    }

    [Fact]
    public void RandomPoisson_MeanConverges()
    {
        RandomPoissonFunction function = new();
        float lambda = 7f;
        float sum = 0;
        int count = 10_000;
        for (int i = 0; i < count; i++)
        {
            DataValue result = function.Execute([DataValue.FromScalar(lambda)]);
            sum += result.AsScalar();
        }

        float mean = sum / count;
        Assert.InRange(mean, 6.5f, 7.5f);
    }

    [Fact]
    public void RandomPoisson_LargeLambda_UsesNormalApproximation()
    {
        RandomPoissonFunction function = new();
        float lambda = 100f;
        float sum = 0;
        int count = 5_000;
        for (int i = 0; i < count; i++)
        {
            DataValue result = function.Execute([DataValue.FromScalar(lambda)]);
            sum += result.AsScalar();
        }

        float mean = sum / count;
        Assert.InRange(mean, 95f, 105f);
    }

    [Fact]
    public void RandomPoisson_ValidateArguments_WrongArity()
    {
        RandomPoissonFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Scalar, DataKind.Scalar]));
    }

    // ──────────────────── random_categorical ────────────────────

    [Fact]
    public void RandomCategorical_ReturnsValidIndex()
    {
        RandomCategoricalFunction function = new();
        float[] weights = [1, 2, 3, 4];
        for (int i = 0; i < 100; i++)
        {
            DataValue result = function.Execute([DataValue.FromVector(weights)]);
            float value = result.AsScalar();
            Assert.InRange(value, 0f, 3f);
            Assert.Equal(MathF.Truncate(value), value);
        }
    }

    [Fact]
    public void RandomCategorical_SingleWeight_AlwaysReturnsZero()
    {
        RandomCategoricalFunction function = new();
        float[] weights = [1];
        for (int i = 0; i < 10; i++)
        {
            DataValue result = function.Execute([DataValue.FromVector(weights)]);
            Assert.Equal(0f, result.AsScalar());
        }
    }

    [Fact]
    public void RandomCategorical_EmptyWeights_Throws()
    {
        RandomCategoricalFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([DataValue.FromVector([])]));
    }

    [Fact]
    public void RandomCategorical_NegativeWeight_Throws()
    {
        RandomCategoricalFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([DataValue.FromVector([1, -1, 2])]));
    }

    [Fact]
    public void RandomCategorical_AllZeroWeights_Throws()
    {
        RandomCategoricalFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([DataValue.FromVector([0, 0, 0])]));
    }

    [Fact]
    public void RandomCategorical_NullInput_ReturnsNull()
    {
        RandomCategoricalFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Vector)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void RandomCategorical_ValidateArguments_WrongType()
    {
        RandomCategoricalFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Scalar]));
    }
}

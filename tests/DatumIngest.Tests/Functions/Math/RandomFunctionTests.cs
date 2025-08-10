using DatumIngest.Functions.Math;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Math;

/// <summary>
/// Tests for <see cref="HashSplitFunction"/>, <see cref="RandomIntFunction"/>,
/// <see cref="RandomRangeFunction"/>, <see cref="RandomNormalFunction"/>,
/// and <see cref="RandomBooleanFunction"/>.
/// </summary>
public sealed class RandomFunctionTests
{
    // ──────────────────── hash_split ────────────────────

    [Fact]
    public void HashSplit_Deterministic_SameKeyAndSeed_ReturnsSameValue()
    {
        HashSplitFunction function = new();
        DataValue result1 = function.Execute([DataValue.FromString("row-42"), DataValue.FromScalar(7)]);
        DataValue result2 = function.Execute([DataValue.FromString("row-42"), DataValue.FromScalar(7)]);
        Assert.Equal(result1.AsScalar(), result2.AsScalar());
    }

    [Fact]
    public void HashSplit_DifferentKeys_ProduceDifferentValues()
    {
        HashSplitFunction function = new();
        DataValue result1 = function.Execute([DataValue.FromString("row-1"), DataValue.FromScalar(0)]);
        DataValue result2 = function.Execute([DataValue.FromString("row-2"), DataValue.FromScalar(0)]);
        Assert.NotEqual(result1.AsScalar(), result2.AsScalar());
    }

    [Fact]
    public void HashSplit_DifferentSeeds_ProduceDifferentValues()
    {
        HashSplitFunction function = new();
        DataValue result1 = function.Execute([DataValue.FromString("same-key"), DataValue.FromScalar(1)]);
        DataValue result2 = function.Execute([DataValue.FromString("same-key"), DataValue.FromScalar(2)]);
        Assert.NotEqual(result1.AsScalar(), result2.AsScalar());
    }

    [Fact]
    public void HashSplit_ReturnsValueInUnitInterval()
    {
        HashSplitFunction function = new();
        for (int i = 0; i < 100; i++)
        {
            DataValue result = function.Execute([DataValue.FromScalar(i), DataValue.FromScalar(0)]);
            float value = result.AsScalar();
            Assert.InRange(value, 0f, 1f);
            Assert.True(value < 1f, "hash_split() must return values strictly less than 1.");
        }
    }

    [Fact]
    public void HashSplit_NullKey_ReturnsNull()
    {
        HashSplitFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String), DataValue.FromScalar(0)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void HashSplit_AcceptsAnyKeyType()
    {
        HashSplitFunction function = new();

        // String key
        DataValue stringResult = function.Execute([DataValue.FromString("key"), DataValue.FromScalar(0)]);
        Assert.False(stringResult.IsNull);

        // Scalar key
        DataValue scalarResult = function.Execute([DataValue.FromScalar(42), DataValue.FromScalar(0)]);
        Assert.False(scalarResult.IsNull);

        // UUID key
        DataValue uuidResult = function.Execute([DataValue.FromUuid(Guid.NewGuid()), DataValue.FromScalar(0)]);
        Assert.False(uuidResult.IsNull);
    }

    [Fact]
    public void HashSplit_ValidateArguments_WrongArity()
    {
        HashSplitFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.String]));
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.String, DataKind.Scalar, DataKind.Scalar]));
    }

    [Fact]
    public void HashSplit_ValidateArguments_WrongSeedType()
    {
        HashSplitFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public void HashSplit_ValidateArguments_AcceptsAnyKeyKind()
    {
        HashSplitFunction function = new();
        DataKind result = function.ValidateArguments([DataKind.String, DataKind.Scalar]);
        Assert.Equal(DataKind.Scalar, result);

        result = function.ValidateArguments([DataKind.Uuid, DataKind.UInt8]);
        Assert.Equal(DataKind.Scalar, result);
    }

    // ──────────────────── random_int ────────────────────

    [Fact]
    public void RandomInt_ReturnsIntegerInRange()
    {
        RandomIntFunction function = new();
        for (int i = 0; i < 100; i++)
        {
            DataValue result = function.Execute([DataValue.FromScalar(1), DataValue.FromScalar(10)]);
            float value = result.AsScalar();
            Assert.True(value >= 1 && value <= 10, $"Expected [1, 10], got {value}.");
            Assert.Equal(MathF.Truncate(value), value);
        }
    }

    [Fact]
    public void RandomInt_MinEqualsMax_ReturnsThatValue()
    {
        RandomIntFunction function = new();
        DataValue result = function.Execute([DataValue.FromScalar(5), DataValue.FromScalar(5)]);
        Assert.Equal(5f, result.AsScalar());
    }

    [Fact]
    public void RandomInt_MinGreaterThanMax_Throws()
    {
        RandomIntFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([DataValue.FromScalar(10), DataValue.FromScalar(1)]));
    }

    [Fact]
    public void RandomInt_ValidateArguments_WrongArity()
    {
        RandomIntFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Scalar]));
    }

    [Fact]
    public void RandomInt_ValidateArguments_WrongType()
    {
        RandomIntFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.String, DataKind.Scalar]));
    }

    // ──────────────────── random_range ────────────────────

    [Fact]
    public void RandomRange_ReturnsValueInRange()
    {
        RandomRangeFunction function = new();
        for (int i = 0; i < 100; i++)
        {
            DataValue result = function.Execute([DataValue.FromScalar(-5), DataValue.FromScalar(5)]);
            float value = result.AsScalar();
            Assert.InRange(value, -5f, 5f);
        }
    }

    [Fact]
    public void RandomRange_MinEqualMax_Throws()
    {
        RandomRangeFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([DataValue.FromScalar(5), DataValue.FromScalar(5)]));
    }

    [Fact]
    public void RandomRange_ValidateArguments_WrongArity()
    {
        RandomRangeFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Scalar]));
    }

    // ──────────────────── random_normal ────────────────────

    [Fact]
    public void RandomNormal_MeanConvergesOverManySamples()
    {
        RandomNormalFunction function = new();
        float sum = 0;
        int count = 10_000;
        for (int i = 0; i < count; i++)
        {
            DataValue result = function.Execute([DataValue.FromScalar(100), DataValue.FromScalar(1)]);
            sum += result.AsScalar();
        }

        float mean = sum / count;
        Assert.InRange(mean, 99f, 101f);
    }

    [Fact]
    public void RandomNormal_NegativeStddev_Throws()
    {
        RandomNormalFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([DataValue.FromScalar(0), DataValue.FromScalar(-1)]));
    }

    [Fact]
    public void RandomNormal_ZeroStddev_ReturnsMean()
    {
        RandomNormalFunction function = new();
        DataValue result = function.Execute([DataValue.FromScalar(42), DataValue.FromScalar(0)]);
        Assert.Equal(42f, result.AsScalar());
    }

    [Fact]
    public void RandomNormal_ValidateArguments_WrongArity()
    {
        RandomNormalFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Scalar]));
    }

    // ──────────────────── random_boolean ────────────────────

    [Fact]
    public void RandomBoolean_ProbabilityZero_AlwaysFalse()
    {
        RandomBooleanFunction function = new();
        for (int i = 0; i < 100; i++)
        {
            DataValue result = function.Execute([DataValue.FromScalar(0)]);
            Assert.False(result.AsBoolean());
        }
    }

    [Fact]
    public void RandomBoolean_ProbabilityOne_AlwaysTrue()
    {
        RandomBooleanFunction function = new();
        for (int i = 0; i < 100; i++)
        {
            DataValue result = function.Execute([DataValue.FromScalar(1)]);
            Assert.True(result.AsBoolean());
        }
    }

    [Fact]
    public void RandomBoolean_InvalidProbability_Throws()
    {
        RandomBooleanFunction function = new();
        Assert.Throws<ArgumentException>(() => function.Execute([DataValue.FromScalar(-0.1f)]));
        Assert.Throws<ArgumentException>(() => function.Execute([DataValue.FromScalar(1.1f)]));
    }

    [Fact]
    public void RandomBoolean_ReturnsBoolean()
    {
        RandomBooleanFunction function = new();
        DataKind kind = function.ValidateArguments([DataKind.Scalar]);
        Assert.Equal(DataKind.Boolean, kind);
    }
}

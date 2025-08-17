using DatumIngest.Functions.Math;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Math;

/// <summary>
/// Tests for <see cref="RandomVectorFunction"/>, <see cref="RandomNormalVectorFunction"/>,
/// <see cref="RandomPermutationFunction"/>, and <see cref="RandomChoiceFunction"/>.
/// </summary>
public sealed class RandomVectorFunctionTests
{
    // ──────────────────── random_vector ────────────────────

    [Fact]
    public void RandomVector_ReturnsCorrectLength()
    {
        RandomVectorFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(10)]);
        float[] vector = result.AsVector();
        Assert.Equal(10, vector.Length);
    }

    [Fact]
    public void RandomVector_AllValuesInUnitInterval()
    {
        RandomVectorFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(100)]);
        float[] vector = result.AsVector();
        foreach (float value in vector)
        {
            Assert.InRange(value, 0f, 1f);
            Assert.True(value < 1f, "Values must be strictly less than 1.");
        }
    }

    [Fact]
    public void RandomVector_ZeroLength_Throws()
    {
        RandomVectorFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([DataValue.FromFloat32(0)]));
    }

    [Fact]
    public void RandomVector_NegativeLength_Throws()
    {
        RandomVectorFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([DataValue.FromFloat32(-5)]));
    }

    [Fact]
    public void RandomVector_ValidateArguments_ReturnsVector()
    {
        RandomVectorFunction function = new();
        DataKind kind = function.ValidateArguments([DataKind.Float32]);
        Assert.Equal(DataKind.Vector, kind);
    }

    [Fact]
    public void RandomVector_QueryUnitCost_IsTwo()
    {
        RandomVectorFunction function = new();
        Assert.Equal(2, function.QueryUnitCost);
    }

    // ──────────────────── random_normal_vector ────────────────────

    [Fact]
    public void RandomNormalVector_ReturnsCorrectLength()
    {
        RandomNormalVectorFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(20),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(1)
        ]);
        float[] vector = result.AsVector();
        Assert.Equal(20, vector.Length);
    }

    [Fact]
    public void RandomNormalVector_MeanConverges()
    {
        RandomNormalVectorFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(10_000),
            DataValue.FromFloat32(5),
            DataValue.FromFloat32(1)
        ]);
        float[] vector = result.AsVector();
        float mean = vector.Sum() / vector.Length;
        Assert.InRange(mean, 4.8f, 5.2f);
    }

    [Fact]
    public void RandomNormalVector_NegativeStddev_Throws()
    {
        RandomNormalVectorFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([
                DataValue.FromFloat32(10),
                DataValue.FromFloat32(0),
                DataValue.FromFloat32(-1)
            ]));
    }

    [Fact]
    public void RandomNormalVector_ZeroLength_Throws()
    {
        RandomNormalVectorFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([
                DataValue.FromFloat32(0),
                DataValue.FromFloat32(0),
                DataValue.FromFloat32(1)
            ]));
    }

    [Fact]
    public void RandomNormalVector_ValidateArguments_WrongArity()
    {
        RandomNormalVectorFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32, DataKind.Float32]));
    }

    // ──────────────────── random_permutation ────────────────────

    [Fact]
    public void RandomPermutation_ContainsAllIndices()
    {
        RandomPermutationFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(10)]);
        float[] vector = result.AsVector();
        Assert.Equal(10, vector.Length);

        HashSet<float> seen = new(vector);
        Assert.Equal(10, seen.Count);
        for (int i = 0; i < 10; i++)
        {
            Assert.Contains((float)i, seen);
        }
    }

    [Fact]
    public void RandomPermutation_LengthOne_ReturnsSingleZero()
    {
        RandomPermutationFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(1)]);
        float[] vector = result.AsVector();
        Assert.Single(vector);
        Assert.Equal(0f, vector[0]);
    }

    [Fact]
    public void RandomPermutation_ZeroLength_Throws()
    {
        RandomPermutationFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.Execute([DataValue.FromFloat32(0)]));
    }

    [Fact]
    public void RandomPermutation_ValidateArguments_ReturnsVector()
    {
        RandomPermutationFunction function = new();
        DataKind kind = function.ValidateArguments([DataKind.Float32]);
        Assert.Equal(DataKind.Vector, kind);
    }

    // ──────────────────── random_choice ────────────────────

    [Fact]
    public void RandomChoice_ReturnsCorrectCount()
    {
        RandomChoiceFunction function = new();
        DataValue[] source = [
            DataValue.FromFloat32(10), DataValue.FromFloat32(20),
            DataValue.FromFloat32(30), DataValue.FromFloat32(40),
            DataValue.FromFloat32(50)
        ];
        DataValue array = DataValue.FromArray(DataKind.Float32, source);

        DataValue result = function.Execute([array, DataValue.FromFloat32(3)]);
        DataValue[] chosen = result.AsArray();
        Assert.Equal(3, chosen.Length);
    }

    [Fact]
    public void RandomChoice_NoDuplicates()
    {
        RandomChoiceFunction function = new();
        DataValue[] source = [
            DataValue.FromFloat32(1), DataValue.FromFloat32(2),
            DataValue.FromFloat32(3), DataValue.FromFloat32(4),
            DataValue.FromFloat32(5)
        ];
        DataValue array = DataValue.FromArray(DataKind.Float32, source);

        for (int trial = 0; trial < 50; trial++)
        {
            DataValue result = function.Execute([array, DataValue.FromFloat32(3)]);
            DataValue[] chosen = result.AsArray();
            HashSet<float> values = new(chosen.Select(v => v.AsFloat32()));
            Assert.Equal(3, values.Count);
        }
    }

    [Fact]
    public void RandomChoice_CountEqualsLength_ReturnsAll()
    {
        RandomChoiceFunction function = new();
        DataValue[] source = [
            DataValue.FromFloat32(1), DataValue.FromFloat32(2), DataValue.FromFloat32(3)
        ];
        DataValue array = DataValue.FromArray(DataKind.Float32, source);

        DataValue result = function.Execute([array, DataValue.FromFloat32(3)]);
        DataValue[] chosen = result.AsArray();
        Assert.Equal(3, chosen.Length);
        HashSet<float> values = new(chosen.Select(v => v.AsFloat32()));
        Assert.Contains(1f, values);
        Assert.Contains(2f, values);
        Assert.Contains(3f, values);
    }

    [Fact]
    public void RandomChoice_CountZero_ReturnsEmpty()
    {
        RandomChoiceFunction function = new();
        DataValue[] source = [DataValue.FromFloat32(1), DataValue.FromFloat32(2)];
        DataValue array = DataValue.FromArray(DataKind.Float32, source);

        DataValue result = function.Execute([array, DataValue.FromFloat32(0)]);
        DataValue[] chosen = result.AsArray();
        Assert.Empty(chosen);
    }

    [Fact]
    public void RandomChoice_CountExceedsLength_Throws()
    {
        RandomChoiceFunction function = new();
        DataValue[] source = [DataValue.FromFloat32(1), DataValue.FromFloat32(2)];
        DataValue array = DataValue.FromArray(DataKind.Float32, source);

        Assert.Throws<ArgumentException>(() =>
            function.Execute([array, DataValue.FromFloat32(5)]));
    }

    [Fact]
    public void RandomChoice_NullArray_ReturnsNull()
    {
        RandomChoiceFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.Array),
            DataValue.FromFloat32(1)
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void RandomChoice_ValidateArguments_WrongFirstType()
    {
        RandomChoiceFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Vector, DataKind.Float32]));
    }

    [Fact]
    public void RandomChoice_ValidateArguments_ReturnsArray()
    {
        RandomChoiceFunction function = new();
        DataKind kind = function.ValidateArguments([DataKind.Array, DataKind.Float32]);
        Assert.Equal(DataKind.Array, kind);
    }
}

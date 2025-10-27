using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="ArgMaxFunction"/> (ARG_MAX and ARG_MIN aggregates).
/// </summary>
public class ArgMaxFunctionTests : ServiceTestBase
{
    // ─────────────── ARG_MAX ───────────────

    [Fact]
    public void ArgMax_ReturnsValueAtMaxKey()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        // (value, key): want value where key is largest
        accumulator.Accumulate([DataValue.FromString("apple"), DataValue.FromFloat32(10f)]);
        accumulator.Accumulate([DataValue.FromString("banana"), DataValue.FromFloat32(30f)]);
        accumulator.Accumulate([DataValue.FromString("cherry"), DataValue.FromFloat32(20f)]);

        Assert.Equal("banana", accumulator.Result.AsString());
    }

    [Fact]
    public void ArgMin_ReturnsValueAtMinKey()
    {
        ArgMaxFunction function = new(findMaximum: false, "ARG_MIN");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("apple"), DataValue.FromFloat32(10f)]);
        accumulator.Accumulate([DataValue.FromString("banana"), DataValue.FromFloat32(30f)]);
        accumulator.Accumulate([DataValue.FromString("cherry"), DataValue.FromFloat32(20f)]);

        Assert.Equal("apple", accumulator.Result.AsString());
    }

    [Fact]
    public void ArgMax_SkipsNullKeys()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("apple"), DataValue.Null(DataKind.Float32)]);
        accumulator.Accumulate([DataValue.FromString("banana"), DataValue.FromFloat32(5f)]);
        accumulator.Accumulate([DataValue.FromString("cherry"), DataValue.Null(DataKind.Float32)]);

        Assert.Equal("banana", accumulator.Result.AsString());
    }

    [Fact]
    public void ArgMax_AllNullKeys_ReturnsNull()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("apple"), DataValue.Null(DataKind.Float32)]);
        accumulator.Accumulate([DataValue.FromString("banana"), DataValue.Null(DataKind.Float32)]);

        Assert.True(accumulator.Result.IsNull);
    }

    [Fact]
    public void ArgMax_SingleRow_ReturnsThatValue()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("only"), DataValue.FromFloat32(42f)]);

        Assert.Equal("only", accumulator.Result.AsString());
    }

    [Fact]
    public void ArgMax_StringKeys()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromFloat32(1f), DataValue.FromString("alpha")]);
        accumulator.Accumulate([DataValue.FromFloat32(2f), DataValue.FromString("zeta")]);
        accumulator.Accumulate([DataValue.FromFloat32(3f), DataValue.FromString("beta")]);

        // "zeta" is the lexicographic maximum key.
        Assert.Equal(2f, accumulator.Result.AsFloat32());
    }

    [Fact]
    public void ArgMax_TieBreaking_FirstEncounteredWins()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("first"), DataValue.FromFloat32(100f)]);
        accumulator.Accumulate([DataValue.FromString("second"), DataValue.FromFloat32(100f)]);

        // Same max key — first-encountered value wins.
        Assert.Equal("first", accumulator.Result.AsString());
    }

    [Fact]
    public void ArgMax_NullValue_PreservedWhenKeyIsMax()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("apple"), DataValue.FromFloat32(10f)]);
        accumulator.Accumulate([DataValue.Null(DataKind.String), DataValue.FromFloat32(99f)]);

        // The max-key row has a null value — that null is the correct result.
        Assert.True(accumulator.Result.IsNull);
    }

    [Fact]
    public void ArgMax_Merge_TakesBetterPartition()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Accumulate([DataValue.FromString("left_winner"), DataValue.FromFloat32(50f)]);
        right.Accumulate([DataValue.FromString("right_winner"), DataValue.FromFloat32(80f)]);

        left.Merge(right);

        Assert.Equal("right_winner", left.Result.AsString());
    }

    [Fact]
    public void ArgMax_Merge_KeepsLocalWhenBetter()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Accumulate([DataValue.FromString("left_winner"), DataValue.FromFloat32(90f)]);
        right.Accumulate([DataValue.FromString("right_loser"), DataValue.FromFloat32(10f)]);

        left.Merge(right);

        Assert.Equal("left_winner", left.Result.AsString());
    }

    [Fact]
    public void ArgMax_Merge_EmptyOther_NoChange()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Accumulate([DataValue.FromString("only"), DataValue.FromFloat32(1f)]);
        // right is empty — no rows accumulated.

        left.Merge(right);

        Assert.Equal("only", left.Result.AsString());
    }

    [Fact]
    public void ArgMax_Reset_ClearsState()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("first_group"), DataValue.FromFloat32(100f)]);
        Assert.Equal("first_group", accumulator.Result.AsString());

        accumulator.Reset();

        accumulator.Accumulate([DataValue.FromString("second_group"), DataValue.FromFloat32(5f)]);
        Assert.Equal("second_group", accumulator.Result.AsString());
    }

    // ─────────────── ValidateArguments ───────────────

    [Fact]
    public void ArgMax_ValidateArguments_ReturnsValueKind()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");

        DataKind result = function.ValidateArguments([DataKind.String, DataKind.Float32]);

        Assert.Equal(DataKind.String, result);
    }

    [Fact]
    public void ArgMax_ValidateArguments_WrongCount_Throws()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void ArgMax_ValidateArguments_NonComparableKey_Throws()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String, DataKind.Vector]));
    }

    [Fact]
    public void ArgMin_Int32Keys()
    {
        ArgMaxFunction function = new(findMaximum: false, "ARG_MIN");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("a"), DataValue.FromInt32(50)]);
        accumulator.Accumulate([DataValue.FromString("b"), DataValue.FromInt32(10)]);
        accumulator.Accumulate([DataValue.FromString("c"), DataValue.FromInt32(30)]);

        Assert.Equal("b", accumulator.Result.AsString());
    }
}

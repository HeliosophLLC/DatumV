using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="ArgMaxFunction"/> (ARG_MAX and ARG_MIN aggregates).
/// </summary>
public class ArgMaxFunctionTests : ServiceTestBase
{
    private static readonly DatumIngest.Functions.InvocationFrame _testFrame = DatumIngest.Functions.InvocationFrame.Symmetric(new DatumIngest.Model.Arena());

    // ─────────────── ARG_MAX ───────────────

    [Fact]
    public void ArgMax_ReturnsValueAtMaxKey()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        // (value, key): want value where key is largest
        accumulator.Accumulate([DataValue.FromString("apple"), DataValue.FromFloat32(10f)], in _testFrame);
        accumulator.Accumulate([DataValue.FromString("banana"), DataValue.FromFloat32(30f)], in _testFrame);
        accumulator.Accumulate([DataValue.FromString("cherry"), DataValue.FromFloat32(20f)], in _testFrame);

        Assert.Equal("banana", accumulator.Result(in _testFrame).AsString(_testFrame.Target));
    }

    [Fact]
    public void ArgMin_ReturnsValueAtMinKey()
    {
        ArgMaxFunction function = new(findMaximum: false, "ARG_MIN");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("apple"), DataValue.FromFloat32(10f)], in _testFrame);
        accumulator.Accumulate([DataValue.FromString("banana"), DataValue.FromFloat32(30f)], in _testFrame);
        accumulator.Accumulate([DataValue.FromString("cherry"), DataValue.FromFloat32(20f)], in _testFrame);

        Assert.Equal("apple", accumulator.Result(in _testFrame).AsString(_testFrame.Target));
    }

    [Fact]
    public void ArgMax_SkipsNullKeys()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("apple"), DataValue.Null(DataKind.Float32)], in _testFrame);
        accumulator.Accumulate([DataValue.FromString("banana"), DataValue.FromFloat32(5f)], in _testFrame);
        accumulator.Accumulate([DataValue.FromString("cherry"), DataValue.Null(DataKind.Float32)], in _testFrame);

        Assert.Equal("banana", accumulator.Result(in _testFrame).AsString(_testFrame.Target));
    }

    [Fact]
    public void ArgMax_AllNullKeys_ReturnsNull()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("apple"), DataValue.Null(DataKind.Float32)], in _testFrame);
        accumulator.Accumulate([DataValue.FromString("banana"), DataValue.Null(DataKind.Float32)], in _testFrame);

        Assert.True(accumulator.Result(in _testFrame).IsNull);
    }

    [Fact]
    public void ArgMax_SingleRow_ReturnsThatValue()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("only"), DataValue.FromFloat32(42f)], in _testFrame);

        Assert.Equal("only", accumulator.Result(in _testFrame).AsString(_testFrame.Target));
    }

    [Fact]
    public void ArgMax_StringKeys()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromFloat32(1f), DataValue.FromString("alpha")], in _testFrame);
        accumulator.Accumulate([DataValue.FromFloat32(2f), DataValue.FromString("zeta")], in _testFrame);
        accumulator.Accumulate([DataValue.FromFloat32(3f), DataValue.FromString("beta")], in _testFrame);

        // "zeta" is the lexicographic maximum key.
        Assert.Equal(2f, accumulator.Result(in _testFrame).AsFloat32());
    }

    [Fact]
    public void ArgMax_TieBreaking_FirstEncounteredWins()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("first"), DataValue.FromFloat32(100f)], in _testFrame);
        accumulator.Accumulate([DataValue.FromString("second"), DataValue.FromFloat32(100f)], in _testFrame);

        // Same max key — first-encountered value wins.
        Assert.Equal("first", accumulator.Result(in _testFrame).AsString(_testFrame.Target));
    }

    [Fact]
    public void ArgMax_NullValue_PreservedWhenKeyIsMax()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("apple"), DataValue.FromFloat32(10f)], in _testFrame);
        accumulator.Accumulate([DataValue.Null(DataKind.String), DataValue.FromFloat32(99f)], in _testFrame);

        // The max-key row has a null value — that null is the correct result.
        Assert.True(accumulator.Result(in _testFrame).IsNull);
    }

    [Fact]
    public void ArgMax_Merge_TakesBetterPartition()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Accumulate([DataValue.FromString("left_winner"), DataValue.FromFloat32(50f)], in _testFrame);
        right.Accumulate([DataValue.FromString("right_winner"), DataValue.FromFloat32(80f)], in _testFrame);

        left.Merge(right, in _testFrame);

        Assert.Equal("right_winner", left.Result(in _testFrame).AsString(_testFrame.Target));
    }

    [Fact]
    public void ArgMax_Merge_KeepsLocalWhenBetter()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Accumulate([DataValue.FromString("left_winner"), DataValue.FromFloat32(90f)], in _testFrame);
        right.Accumulate([DataValue.FromString("right_loser"), DataValue.FromFloat32(10f)], in _testFrame);

        left.Merge(right, in _testFrame);

        Assert.Equal("left_winner", left.Result(in _testFrame).AsString(_testFrame.Target));
    }

    [Fact]
    public void ArgMax_Merge_EmptyOther_NoChange()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator left = function.CreateAccumulator();
        IAggregateAccumulator right = function.CreateAccumulator();

        left.Accumulate([DataValue.FromString("only"), DataValue.FromFloat32(1f)], in _testFrame);
        // right is empty — no rows accumulated.

        left.Merge(right, in _testFrame);

        Assert.Equal("only", left.Result(in _testFrame).AsString(_testFrame.Target));
    }

    [Fact]
    public void ArgMax_Reset_ClearsState()
    {
        ArgMaxFunction function = new(findMaximum: true, "ARG_MAX");
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("first_group"), DataValue.FromFloat32(100f)], in _testFrame);
        Assert.Equal("first_group", accumulator.Result(in _testFrame).AsString(_testFrame.Target));

        accumulator.Reset();

        accumulator.Accumulate([DataValue.FromString("second_group"), DataValue.FromFloat32(5f)], in _testFrame);
        Assert.Equal("second_group", accumulator.Result(in _testFrame).AsString(_testFrame.Target));
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

        accumulator.Accumulate([DataValue.FromString("a"), DataValue.FromInt32(50)], in _testFrame);
        accumulator.Accumulate([DataValue.FromString("b"), DataValue.FromInt32(10)], in _testFrame);
        accumulator.Accumulate([DataValue.FromString("c"), DataValue.FromInt32(30)], in _testFrame);

        Assert.Equal("b", accumulator.Result(in _testFrame).AsString(_testFrame.Target));
    }
}

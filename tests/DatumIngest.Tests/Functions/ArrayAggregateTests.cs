using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for the <see cref="ArrayAggregateFunction"/> aggregate function.
/// </summary>
public class ArrayAggregateTests : ServiceTestBase
{
    private static readonly DatumIngest.Functions.InvocationFrame _testFrame = DatumIngest.Functions.InvocationFrame.Symmetric(new DatumIngest.Model.Arena());

    // ─────────────── VALIDATION ───────────────

    [Fact]
    public async Task ValidateArguments_SingleArgument_ReturnsElementKind()
    {
        ArrayAggregateFunction function = new();

        // Post-IAggregateFunction migration: ValidateArguments returns the
        // per-element kind. Array-ness is signalled via ReturnRule.
        DataKind result = function.ValidateArguments([DataKind.Float32]);

        Assert.Equal(DataKind.Float32, result);
        Assert.True(function.ReturnRule?.ProducesArray);
    }

    [Fact]
    public async Task ValidateArguments_AcceptsAnyKind_PerElementKindEqualsArgumentKind()
    {
        ArrayAggregateFunction function = new();

        foreach (DataKind kind in Enum.GetValues<DataKind>())
        {
            DataKind result = function.ValidateArguments([kind]);
            // Element kind echoes the argument kind across the board — ARRAY_AGG
            // doesn't promote or coerce.
            Assert.Equal(kind, result);
        }

        Assert.True(function.ReturnRule?.ProducesArray);
    }

    [Fact]
    public async Task ValidateArguments_NoArguments_Throws()
    {
        ArrayAggregateFunction function = new();

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments(ReadOnlySpan<DataKind>.Empty));
    }

    [Fact]
    public async Task ValidateArguments_TwoArguments_Throws()
    {
        ArrayAggregateFunction function = new();

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32, DataKind.String]));
    }

    // ─────────────── NAME ───────────────

    [Fact]
    public async Task Name_ReturnsArrayAgg()
    {
        Assert.Equal("ARRAY_AGG", ArrayAggregateFunction.Name);
    }

    // ─────────────── REGISTRY ───────────────

    [Fact]
    public async Task Registry_ContainsArrayAgg()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IAggregateFunction? function = registry.TryGetAggregate("ARRAY_AGG");

        Assert.NotNull(function);
        Assert.Equal("ARRAY_AGG", function.Name);
    }

    // ─────────────── SCALAR VALUES ───────────────

    [Fact]
    public async Task Accumulate_ScalarValues_CollectsAll()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromFloat32(10f)], in _testFrame);
        accumulator.Accumulate([DataValue.FromFloat32(20f)], in _testFrame);
        accumulator.Accumulate([DataValue.FromFloat32(30f)], in _testFrame);

        DataValue result = (await accumulator.ResultAsync(_testFrame));
        // Typed-array shape: Kind = element kind, IsArray flag set.
        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Float32, result.ArrayElementKind);

        ReadOnlySpan<float> elements = result.AsArraySpan<float>(_testFrame.Target);
        Assert.Equal(3, elements.Length);
        Assert.Equal(10f, elements[0]);
        Assert.Equal(20f, elements[1]);
        Assert.Equal(30f, elements[2]);
    }

    // ─────────────── STRING VALUES ───────────────

    [Fact]
    public async Task Accumulate_StringValues_CollectsAll()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("apple")], in _testFrame);
        accumulator.Accumulate([DataValue.FromString("banana")], in _testFrame);
        accumulator.Accumulate([DataValue.FromString("cherry")], in _testFrame);

        DataValue result = (await accumulator.ResultAsync(_testFrame));
        Assert.Equal(DataKind.String, result.Kind);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.String, result.ArrayElementKind);

        string[] elements = result.AsStringArray(_testFrame.Target);
        Assert.Equal(3, elements.Length);
        Assert.Equal("apple", elements[0]);
        Assert.Equal("banana", elements[1]);
        Assert.Equal("cherry", elements[2]);
    }

    // ─────────────── DATE VALUES ───────────────

    [Fact]
    public async Task Accumulate_DateValues_CollectsAll()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DateOnly date1 = new(2024, 1, 15);
        DateOnly date2 = new(2024, 6, 30);

        accumulator.Accumulate([DataValue.FromDate(date1)], in _testFrame);
        accumulator.Accumulate([DataValue.FromDate(date2)], in _testFrame);

        DataValue result = (await accumulator.ResultAsync(_testFrame));
        Assert.Equal(DataKind.Date, result.Kind);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Date, result.ArrayElementKind);

        // Date is stored as int32 day-number in the typed-array packing.
        ReadOnlySpan<int> elements = result.AsArraySpan<int>(_testFrame.Target);
        Assert.Equal(2, elements.Length);
        Assert.Equal(date1, DateOnly.FromDayNumber(elements[0]));
        Assert.Equal(date2, DateOnly.FromDayNumber(elements[1]));
    }

    // ─────────────── NULL HANDLING ───────────────

    [Fact]
    public async Task Accumulate_SkipsNulls()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromFloat32(1f)], in _testFrame);
        accumulator.Accumulate([DataValue.Null(DataKind.Float32)], in _testFrame);
        accumulator.Accumulate([DataValue.FromFloat32(3f)], in _testFrame);

        DataValue result = (await accumulator.ResultAsync(_testFrame));
        ReadOnlySpan<float> elements = result.AsArraySpan<float>(_testFrame.Target);
        Assert.Equal(2, elements.Length);
        Assert.Equal(1f, elements[0]);
        Assert.Equal(3f, elements[1]);
    }

    [Fact]
    public async Task Accumulate_AllNulls_ReturnsNull()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.Null(DataKind.Float32)], in _testFrame);
        accumulator.Accumulate([DataValue.Null(DataKind.Float32)], in _testFrame);

        DataValue result = (await accumulator.ResultAsync(_testFrame));
        Assert.True(result.IsNull);
        // Typed null array: Kind = element kind, IsArray flag set.
        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.True(result.IsArray);
    }

    [Fact]
    public async Task Accumulate_Empty_ReturnsNull()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue result = (await accumulator.ResultAsync(_testFrame));
        Assert.True(result.IsNull);
        // Empty fallback element kind is Float32 (per ARRAY_AGG convention).
        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.True(result.IsArray);
    }

    // ─────────────── SINGLE ELEMENT ───────────────

    [Fact]
    public async Task Accumulate_SingleValue_ReturnsSingleElementArray()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromFloat32(42f)], in _testFrame);

        DataValue result = (await accumulator.ResultAsync(_testFrame));
        ReadOnlySpan<float> elements = result.AsArraySpan<float>(_testFrame.Target);
        Assert.Equal(1, elements.Length);
        Assert.Equal(42f, elements[0]);
    }

    // ─────────────── PRESERVES INSERTION ORDER ───────────────

    [Fact]
    public async Task Accumulate_PreservesInsertionOrder()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("c")], in _testFrame);
        accumulator.Accumulate([DataValue.FromString("a")], in _testFrame);
        accumulator.Accumulate([DataValue.FromString("b")], in _testFrame);

        string[] elements = (await accumulator.ResultAsync(_testFrame)).AsStringArray(_testFrame.Target);
        Assert.Equal("c", elements[0]);
        Assert.Equal("a", elements[1]);
        Assert.Equal("b", elements[2]);
    }
}

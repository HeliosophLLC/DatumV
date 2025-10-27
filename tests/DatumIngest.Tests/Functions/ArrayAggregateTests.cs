using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for the <see cref="ArrayAggregateFunction"/> aggregate function.
/// </summary>
public class ArrayAggregateTests : ServiceTestBase
{
    // ─────────────── VALIDATION ───────────────

    [Fact]
    public void ValidateArguments_SingleArgument_ReturnsArray()
    {
        ArrayAggregateFunction function = new();

        DataKind result = function.ValidateArguments([DataKind.Float32]);

        Assert.Equal(DataKind.Array, result);
    }

    [Fact]
    public void ValidateArguments_AcceptsAnyKind()
    {
        ArrayAggregateFunction function = new();

        foreach (DataKind kind in Enum.GetValues<DataKind>())
        {
            DataKind result = function.ValidateArguments([kind]);
            Assert.Equal(DataKind.Array, result);
        }
    }

    [Fact]
    public void ValidateArguments_NoArguments_Throws()
    {
        ArrayAggregateFunction function = new();

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments(ReadOnlySpan<DataKind>.Empty));
    }

    [Fact]
    public void ValidateArguments_TwoArguments_Throws()
    {
        ArrayAggregateFunction function = new();

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32, DataKind.String]));
    }

    // ─────────────── NAME ───────────────

    [Fact]
    public void Name_ReturnsArrayAgg()
    {
        ArrayAggregateFunction function = new();

        Assert.Equal("ARRAY_AGG", function.Name);
    }

    // ─────────────── REGISTRY ───────────────

    [Fact]
    public void Registry_ContainsArrayAgg()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IAggregateFunction? function = registry.TryGetAggregate("ARRAY_AGG");

        Assert.NotNull(function);
        Assert.Equal("ARRAY_AGG", function.Name);
    }

    // ─────────────── SCALAR VALUES ───────────────

    [Fact]
    public void Accumulate_ScalarValues_CollectsAll()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromFloat32(10f)]);
        accumulator.Accumulate([DataValue.FromFloat32(20f)]);
        accumulator.Accumulate([DataValue.FromFloat32(30f)]);

        DataValue result = accumulator.Result;
        Assert.Equal(DataKind.Array, result.Kind);
        Assert.Equal(DataKind.Float32, result.ArrayElementKind);

        DataValue[] elements = result.AsArray();
        Assert.Equal(3, elements.Length);
        Assert.Equal(10f, elements[0].AsFloat32());
        Assert.Equal(20f, elements[1].AsFloat32());
        Assert.Equal(30f, elements[2].AsFloat32());
    }

    // ─────────────── STRING VALUES ───────────────

    [Fact]
    public void Accumulate_StringValues_CollectsAll()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("apple")]);
        accumulator.Accumulate([DataValue.FromString("banana")]);
        accumulator.Accumulate([DataValue.FromString("cherry")]);

        DataValue result = accumulator.Result;
        Assert.Equal(DataKind.Array, result.Kind);
        Assert.Equal(DataKind.String, result.ArrayElementKind);

        DataValue[] elements = result.AsArray();
        Assert.Equal(3, elements.Length);
        Assert.Equal("apple", elements[0].AsString());
        Assert.Equal("banana", elements[1].AsString());
        Assert.Equal("cherry", elements[2].AsString());
    }

    // ─────────────── DATE VALUES ───────────────

    [Fact]
    public void Accumulate_DateValues_CollectsAll()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DateOnly date1 = new(2024, 1, 15);
        DateOnly date2 = new(2024, 6, 30);

        accumulator.Accumulate([DataValue.FromDate(date1)]);
        accumulator.Accumulate([DataValue.FromDate(date2)]);

        DataValue result = accumulator.Result;
        Assert.Equal(DataKind.Date, result.ArrayElementKind);

        DataValue[] elements = result.AsArray();
        Assert.Equal(2, elements.Length);
        Assert.Equal(date1, elements[0].AsDate());
        Assert.Equal(date2, elements[1].AsDate());
    }

    // ─────────────── NULL HANDLING ───────────────

    [Fact]
    public void Accumulate_SkipsNulls()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromFloat32(1f)]);
        accumulator.Accumulate([DataValue.Null(DataKind.Float32)]);
        accumulator.Accumulate([DataValue.FromFloat32(3f)]);

        DataValue result = accumulator.Result;
        DataValue[] elements = result.AsArray();
        Assert.Equal(2, elements.Length);
        Assert.Equal(1f, elements[0].AsFloat32());
        Assert.Equal(3f, elements[1].AsFloat32());
    }

    [Fact]
    public void Accumulate_AllNulls_ReturnsNull()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.Null(DataKind.Float32)]);
        accumulator.Accumulate([DataValue.Null(DataKind.Float32)]);

        DataValue result = accumulator.Result;
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Array, result.Kind);
    }

    [Fact]
    public void Accumulate_Empty_ReturnsNull()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue result = accumulator.Result;
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Array, result.Kind);
    }

    // ─────────────── SINGLE ELEMENT ───────────────

    [Fact]
    public void Accumulate_SingleValue_ReturnsSingleElementArray()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromFloat32(42f)]);

        DataValue result = accumulator.Result;
        DataValue[] elements = result.AsArray();
        Assert.Single(elements);
        Assert.Equal(42f, elements[0].AsFloat32());
    }

    // ─────────────── PRESERVES INSERTION ORDER ───────────────

    [Fact]
    public void Accumulate_PreservesInsertionOrder()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.FromString("c")]);
        accumulator.Accumulate([DataValue.FromString("a")]);
        accumulator.Accumulate([DataValue.FromString("b")]);

        DataValue[] elements = accumulator.Result.AsArray();
        Assert.Equal("c", elements[0].AsString());
        Assert.Equal("a", elements[1].AsString());
        Assert.Equal("b", elements[2].AsString());
    }
}

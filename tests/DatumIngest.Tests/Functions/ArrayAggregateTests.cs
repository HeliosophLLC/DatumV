using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for the <see cref="ArrayAggregateFunction"/> aggregate function.
/// </summary>
public class ArrayAggregateTests
{
    // ─────────────── VALIDATION ───────────────

    [Fact]
    public void ValidateArguments_SingleArgument_ReturnsArray()
    {
        ArrayAggregateFunction function = new();

        DataKind result = function.ValidateArguments([DataKind.Scalar]);

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
            function.ValidateArguments([DataKind.Scalar, DataKind.String]));
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

        accumulator.Accumulate([DataValue.FromScalar(10f)]);
        accumulator.Accumulate([DataValue.FromScalar(20f)]);
        accumulator.Accumulate([DataValue.FromScalar(30f)]);

        DataValue result = accumulator.Result;
        Assert.Equal(DataKind.Array, result.Kind);
        Assert.Equal(DataKind.Scalar, result.ArrayElementKind);

        DataValue[] elements = result.AsArray();
        Assert.Equal(3, elements.Length);
        Assert.Equal(10f, elements[0].AsScalar());
        Assert.Equal(20f, elements[1].AsScalar());
        Assert.Equal(30f, elements[2].AsScalar());
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

        accumulator.Accumulate([DataValue.FromScalar(1f)]);
        accumulator.Accumulate([DataValue.Null(DataKind.Scalar)]);
        accumulator.Accumulate([DataValue.FromScalar(3f)]);

        DataValue result = accumulator.Result;
        DataValue[] elements = result.AsArray();
        Assert.Equal(2, elements.Length);
        Assert.Equal(1f, elements[0].AsScalar());
        Assert.Equal(3f, elements[1].AsScalar());
    }

    [Fact]
    public void Accumulate_AllNulls_ReturnsNull()
    {
        ArrayAggregateFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate([DataValue.Null(DataKind.Scalar)]);
        accumulator.Accumulate([DataValue.Null(DataKind.Scalar)]);

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

        accumulator.Accumulate([DataValue.FromScalar(42f)]);

        DataValue result = accumulator.Result;
        DataValue[] elements = result.AsArray();
        Assert.Single(elements);
        Assert.Equal(42f, elements[0].AsScalar());
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

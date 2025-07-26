using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for the built-in aggregate function implementations
/// (COUNT, SUM, AVG, MIN, MAX).
/// </summary>
public class AggregateFunctionTests
{
    // ─────────────── COUNT ───────────────

    [Fact]
    public void Count_Star_CountsAllRows()
    {
        CountFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        accumulator.Accumulate(ReadOnlySpan<DataValue>.Empty);
        accumulator.Accumulate(ReadOnlySpan<DataValue>.Empty);
        accumulator.Accumulate(ReadOnlySpan<DataValue>.Empty);

        Assert.Equal(3f, accumulator.Result.AsScalar());
    }

    [Fact]
    public void Count_Expression_SkipsNulls()
    {
        CountFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] nonNull = [DataValue.FromScalar(10f)];
        DataValue[] nullValue = [DataValue.Null(DataKind.Scalar)];

        accumulator.Accumulate(nonNull);
        accumulator.Accumulate(nullValue);
        accumulator.Accumulate(nonNull);

        Assert.Equal(2f, accumulator.Result.AsScalar());
    }

    [Fact]
    public void Count_Empty_ReturnsZero()
    {
        CountFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        Assert.Equal(0f, accumulator.Result.AsScalar());
    }

    // ─────────────── SUM ───────────────

    [Fact]
    public void Sum_OfScalarValues()
    {
        SumFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromScalar(10f)];
        DataValue[] val2 = [DataValue.FromScalar(20f)];
        DataValue[] val3 = [DataValue.FromScalar(30f)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        Assert.Equal(60f, accumulator.Result.AsScalar());
    }

    [Fact]
    public void Sum_SkipsNulls()
    {
        SumFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromScalar(5f)];
        DataValue[] nullVal = [DataValue.Null(DataKind.Scalar)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(nullVal);
        accumulator.Accumulate(val1);

        Assert.Equal(10f, accumulator.Result.AsScalar());
    }

    [Fact]
    public void Sum_AllNulls_ReturnsNull()
    {
        SumFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] nullVal = [DataValue.Null(DataKind.Scalar)];
        accumulator.Accumulate(nullVal);
        accumulator.Accumulate(nullVal);

        Assert.True(accumulator.Result.IsNull);
    }

    // ─────────────── AVG ───────────────

    [Fact]
    public void Avg_OfScalarValues()
    {
        AvgFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromScalar(10f)];
        DataValue[] val2 = [DataValue.FromScalar(20f)];
        DataValue[] val3 = [DataValue.FromScalar(30f)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        Assert.Equal(20f, accumulator.Result.AsScalar());
    }

    [Fact]
    public void Avg_SkipsNullsInDenominator()
    {
        AvgFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromScalar(10f)];
        DataValue[] nullVal = [DataValue.Null(DataKind.Scalar)];
        DataValue[] val2 = [DataValue.FromScalar(30f)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(nullVal);
        accumulator.Accumulate(val2);

        // AVG of 10 and 30, ignoring NULL = 20
        Assert.Equal(20f, accumulator.Result.AsScalar());
    }

    [Fact]
    public void Avg_AllNulls_ReturnsNull()
    {
        AvgFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] nullVal = [DataValue.Null(DataKind.Scalar)];
        accumulator.Accumulate(nullVal);

        Assert.True(accumulator.Result.IsNull);
    }

    // ─────────────── MIN ───────────────

    [Fact]
    public void Min_OfScalarValues()
    {
        MinFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromScalar(30f)];
        DataValue[] val2 = [DataValue.FromScalar(10f)];
        DataValue[] val3 = [DataValue.FromScalar(20f)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        Assert.Equal(10f, accumulator.Result.AsScalar());
    }

    [Fact]
    public void Min_OfStringValues()
    {
        MinFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromString("cherry")];
        DataValue[] val2 = [DataValue.FromString("apple")];
        DataValue[] val3 = [DataValue.FromString("banana")];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        Assert.Equal("apple", accumulator.Result.AsString());
    }

    [Fact]
    public void Min_SkipsNulls()
    {
        MinFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] nullVal = [DataValue.Null(DataKind.Scalar)];
        DataValue[] val1 = [DataValue.FromScalar(5f)];

        accumulator.Accumulate(nullVal);
        accumulator.Accumulate(val1);

        Assert.Equal(5f, accumulator.Result.AsScalar());
    }

    [Fact]
    public void Min_AllNulls_ReturnsNull()
    {
        MinFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] nullVal = [DataValue.Null(DataKind.Scalar)];
        accumulator.Accumulate(nullVal);

        Assert.True(accumulator.Result.IsNull);
    }

    // ─────────────── MAX ───────────────

    [Fact]
    public void Max_OfScalarValues()
    {
        MaxFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromScalar(10f)];
        DataValue[] val2 = [DataValue.FromScalar(30f)];
        DataValue[] val3 = [DataValue.FromScalar(20f)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        Assert.Equal(30f, accumulator.Result.AsScalar());
    }

    [Fact]
    public void Max_OfStringValues()
    {
        MaxFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromString("apple")];
        DataValue[] val2 = [DataValue.FromString("cherry")];
        DataValue[] val3 = [DataValue.FromString("banana")];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        Assert.Equal("cherry", accumulator.Result.AsString());
    }

    [Fact]
    public void Max_SkipsNulls()
    {
        MaxFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] nullVal = [DataValue.Null(DataKind.Scalar)];
        DataValue[] val1 = [DataValue.FromScalar(5f)];

        accumulator.Accumulate(nullVal);
        accumulator.Accumulate(val1);

        Assert.Equal(5f, accumulator.Result.AsScalar());
    }

    // ─────────────── FunctionRegistry ───────────────

    [Fact]
    public void Registry_ContainsAllAggregateFunctions()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        Assert.NotNull(registry.TryGetAggregate("COUNT"));
        Assert.NotNull(registry.TryGetAggregate("SUM"));
        Assert.NotNull(registry.TryGetAggregate("AVG"));
        Assert.NotNull(registry.TryGetAggregate("MIN"));
        Assert.NotNull(registry.TryGetAggregate("MAX"));
    }

    [Fact]
    public void Registry_AggregateLookupIsCaseInsensitive()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        Assert.NotNull(registry.TryGetAggregate("count"));
        Assert.NotNull(registry.TryGetAggregate("Sum"));
        Assert.NotNull(registry.TryGetAggregate("AVG"));
    }

    [Fact]
    public void Registry_AggregateFunctionNames_ListsAll()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        List<string> names = registry.AggregateFunctionNames.ToList();

        Assert.Contains("COUNT", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("SUM", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("AVG", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("MIN", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("MAX", names, StringComparer.OrdinalIgnoreCase);
    }
}

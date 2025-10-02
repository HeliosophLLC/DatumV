using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for the built-in aggregate function implementations
/// (COUNT, SUM, AVG, MIN, MAX, MEDIAN, VARIANCE, STDDEV, CORR, COVAR).
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

        Assert.Equal(3L, accumulator.Result.AsInt64());
    }

    [Fact]
    public void Count_Expression_SkipsNulls()
    {
        CountFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] nonNull = [DataValue.FromFloat32(10f)];
        DataValue[] nullValue = [DataValue.Null(DataKind.Float32)];

        accumulator.Accumulate(nonNull);
        accumulator.Accumulate(nullValue);
        accumulator.Accumulate(nonNull);

        Assert.Equal(2L, accumulator.Result.AsInt64());
    }

    [Fact]
    public void Count_Empty_ReturnsZero()
    {
        CountFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        Assert.Equal(0L, accumulator.Result.AsInt64());
    }

    // ─────────────── SUM ───────────────

    [Fact]
    public void Sum_OfScalarValues()
    {
        SumFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromFloat32(10f)];
        DataValue[] val2 = [DataValue.FromFloat32(20f)];
        DataValue[] val3 = [DataValue.FromFloat32(30f)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        Assert.Equal(60f, accumulator.Result.AsFloat32());
    }

    [Fact]
    public void Sum_SkipsNulls()
    {
        SumFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromFloat32(5f)];
        DataValue[] nullVal = [DataValue.Null(DataKind.Float32)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(nullVal);
        accumulator.Accumulate(val1);

        Assert.Equal(10f, accumulator.Result.AsFloat32());
    }

    [Fact]
    public void Sum_AllNulls_ReturnsNull()
    {
        SumFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] nullVal = [DataValue.Null(DataKind.Float32)];
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

        DataValue[] val1 = [DataValue.FromFloat32(10f)];
        DataValue[] val2 = [DataValue.FromFloat32(20f)];
        DataValue[] val3 = [DataValue.FromFloat32(30f)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        Assert.Equal(20.0, accumulator.Result.AsFloat64());
    }

    [Fact]
    public void Avg_SkipsNullsInDenominator()
    {
        AvgFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromFloat32(10f)];
        DataValue[] nullVal = [DataValue.Null(DataKind.Float32)];
        DataValue[] val2 = [DataValue.FromFloat32(30f)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(nullVal);
        accumulator.Accumulate(val2);

        // AVG of 10 and 30, ignoring NULL = 20
        Assert.Equal(20.0, accumulator.Result.AsFloat64());
    }

    [Fact]
    public void Avg_AllNulls_ReturnsNull()
    {
        AvgFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] nullVal = [DataValue.Null(DataKind.Float32)];
        accumulator.Accumulate(nullVal);

        Assert.True(accumulator.Result.IsNull);
        Assert.Equal(DataKind.Float64, accumulator.Result.Kind);
    }

    // ─────────────── MIN ───────────────

    [Fact]
    public void Min_OfScalarValues()
    {
        MinFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromFloat32(30f)];
        DataValue[] val2 = [DataValue.FromFloat32(10f)];
        DataValue[] val3 = [DataValue.FromFloat32(20f)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        Assert.Equal(10f, accumulator.Result.AsFloat32());
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

        DataValue[] nullVal = [DataValue.Null(DataKind.Float32)];
        DataValue[] val1 = [DataValue.FromFloat32(5f)];

        accumulator.Accumulate(nullVal);
        accumulator.Accumulate(val1);

        Assert.Equal(5f, accumulator.Result.AsFloat32());
    }

    [Fact]
    public void Min_AllNulls_ReturnsNull()
    {
        MinFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] nullVal = [DataValue.Null(DataKind.Float32)];
        accumulator.Accumulate(nullVal);

        Assert.True(accumulator.Result.IsNull);
    }

    // ─────────────── MAX ───────────────

    [Fact]
    public void Max_OfScalarValues()
    {
        MaxFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromFloat32(10f)];
        DataValue[] val2 = [DataValue.FromFloat32(30f)];
        DataValue[] val3 = [DataValue.FromFloat32(20f)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        Assert.Equal(30f, accumulator.Result.AsFloat32());
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

        DataValue[] nullVal = [DataValue.Null(DataKind.Float32)];
        DataValue[] val1 = [DataValue.FromFloat32(5f)];

        accumulator.Accumulate(nullVal);
        accumulator.Accumulate(val1);

        Assert.Equal(5f, accumulator.Result.AsFloat32());
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

        Assert.NotNull(registry.TryGetAggregate("sum"));
        Assert.NotNull(registry.TryGetAggregate("avg"));
        Assert.NotNull(registry.TryGetAggregate("count"));
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

    // ─────────────── Extended numeric kinds ───────────────

    [Fact]
    public void Sum_Int32Input_ReturnsInt64()
    {
        SumFunction function = new();
        DataKind outputKind = function.ValidateArguments([DataKind.Int32]);
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromInt32(100)];
        DataValue[] val2 = [DataValue.FromInt32(200)];
        DataValue[] val3 = [DataValue.FromInt32(300)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        Assert.Equal(DataKind.Int64, outputKind);
        Assert.Equal(600L, accumulator.Result.AsInt64());
    }

    [Fact]
    public void Sum_Int64Input_ReturnsInt64()
    {
        SumFunction function = new();
        DataKind outputKind = function.ValidateArguments([DataKind.Int64]);
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromInt64(1_000_000_000L)];
        DataValue[] val2 = [DataValue.FromInt64(2_000_000_000L)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);

        Assert.Equal(DataKind.Int64, outputKind);
        Assert.Equal(3_000_000_000L, accumulator.Result.AsInt64());
    }

    [Fact]
    public void Sum_Float64Input_ReturnsFloat64()
    {
        SumFunction function = new();
        DataKind outputKind = function.ValidateArguments([DataKind.Float64]);
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromFloat64(1.5)];
        DataValue[] val2 = [DataValue.FromFloat64(2.5)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);

        Assert.Equal(DataKind.Float64, outputKind);
        Assert.Equal(4.0, accumulator.Result.AsFloat64());
    }

    [Fact]
    public void Sum_IntegerAllNulls_ReturnsNull()
    {
        SumFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] nullVal = [DataValue.Null(DataKind.Int32)];
        accumulator.Accumulate(nullVal);

        Assert.True(accumulator.Result.IsNull);
    }

    [Fact]
    public void Avg_Int32Input_ReturnsFloat64()
    {
        AvgFunction function = new();
        DataKind outputKind = function.ValidateArguments([DataKind.Int32]);
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromInt32(10)];
        DataValue[] val2 = [DataValue.FromInt32(20)];
        DataValue[] val3 = [DataValue.FromInt32(30)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        Assert.Equal(DataKind.Float64, outputKind);
        Assert.Equal(20.0, accumulator.Result.AsFloat64());
    }

    [Fact]
    public void Avg_Float64Input_ReturnsFloat64()
    {
        AvgFunction function = new();
        DataKind outputKind = function.ValidateArguments([DataKind.Float64]);
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromFloat64(1.0)];
        DataValue[] val2 = [DataValue.FromFloat64(3.0)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);

        Assert.Equal(DataKind.Float64, outputKind);
        Assert.Equal(2.0, accumulator.Result.AsFloat64());
    }

    [Fact]
    public void Min_Int32Input_PreservesKind()
    {
        MinFunction function = new();
        DataKind outputKind = function.ValidateArguments([DataKind.Int32]);
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromInt32(30)];
        DataValue[] val2 = [DataValue.FromInt32(10)];
        DataValue[] val3 = [DataValue.FromInt32(20)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        Assert.Equal(DataKind.Int32, outputKind);
        Assert.Equal(10, accumulator.Result.AsInt32());
    }

    [Fact]
    public void Max_Int64Input_PreservesKind()
    {
        MaxFunction function = new();
        DataKind outputKind = function.ValidateArguments([DataKind.Int64]);
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromInt64(100L)];
        DataValue[] val2 = [DataValue.FromInt64(300L)];
        DataValue[] val3 = [DataValue.FromInt64(200L)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        Assert.Equal(DataKind.Int64, outputKind);
        Assert.Equal(300L, accumulator.Result.AsInt64());
    }

    [Fact]
    public void Max_AllNulls_NullKindMatchesInput()
    {
        MaxFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] nullVal = [DataValue.Null(DataKind.Int32)];
        accumulator.Accumulate(nullVal);

        Assert.True(accumulator.Result.IsNull);
    }

    [Fact]
    public void Median_Int32Input_ReturnsFloat64()
    {
        MedianFunction function = new();
        DataKind outputKind = function.ValidateArguments([DataKind.Int32]);
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromInt32(1)];
        DataValue[] val2 = [DataValue.FromInt32(2)];
        DataValue[] val3 = [DataValue.FromInt32(3)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        Assert.Equal(DataKind.Float64, outputKind);
        Assert.Equal(2.0, accumulator.Result.AsFloat64());
    }

    [Fact]
    public void Median_EvenCount_AveragesMiddlePair()
    {
        MedianFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromInt32(1)];
        DataValue[] val2 = [DataValue.FromInt32(2)];
        DataValue[] val3 = [DataValue.FromInt32(3)];
        DataValue[] val4 = [DataValue.FromInt32(4)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);
        accumulator.Accumulate(val4);

        Assert.Equal(2.5, accumulator.Result.AsFloat64());
    }

    [Fact]
    public void Stddev_Int32Input_ReturnsFloat64()
    {
        StandardDeviationFunction function = new(usePopulation: true, name: "STDDEV_POP");
        DataKind outputKind = function.ValidateArguments([DataKind.Int32]);
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        // Population stddev of {2, 4, 4, 4, 5, 5, 7, 9} = 2.0
        foreach (int v in new[] { 2, 4, 4, 4, 5, 5, 7, 9 })
        {
            accumulator.Accumulate([DataValue.FromInt32(v)]);
        }

        Assert.Equal(DataKind.Float64, outputKind);
        Assert.Equal(2.0, accumulator.Result.AsFloat64(), precision: 10);
    }

    [Fact]
    public void Variance_Float64Input_ReturnsFloat64()
    {
        VarianceFunction function = new(usePopulation: false, name: "VAR_SAMP");
        DataKind outputKind = function.ValidateArguments([DataKind.Float64]);
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        DataValue[] val1 = [DataValue.FromFloat64(2.0)];
        DataValue[] val2 = [DataValue.FromFloat64(4.0)];
        DataValue[] val3 = [DataValue.FromFloat64(6.0)];

        accumulator.Accumulate(val1);
        accumulator.Accumulate(val2);
        accumulator.Accumulate(val3);

        // Sample variance of {2, 4, 6} = 4.0
        Assert.Equal(DataKind.Float64, outputKind);
        Assert.Equal(4.0, accumulator.Result.AsFloat64(), precision: 10);
    }

    [Fact]
    public void Corr_Int32Inputs_ReturnsFloat64()
    {
        CorrelationFunction function = new();
        DataKind outputKind = function.ValidateArguments([DataKind.Int32, DataKind.Int32]);
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        // Perfect positive correlation: y = x
        foreach (int v in new[] { 1, 2, 3, 4, 5 })
        {
            accumulator.Accumulate([DataValue.FromInt32(v), DataValue.FromInt32(v)]);
        }

        Assert.Equal(DataKind.Float64, outputKind);
        Assert.Equal(1.0, accumulator.Result.AsFloat64(), precision: 10);
    }

    [Fact]
    public void Covar_Int32Inputs_ReturnsFloat64()
    {
        CovarianceFunction function = new(usePopulation: false, name: "COVAR_SAMP");
        DataKind outputKind = function.ValidateArguments([DataKind.Int32, DataKind.Int32]);
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        // COVAR_SAMP({1,2,3}, {1,2,3}) = 1.0
        foreach (int v in new[] { 1, 2, 3 })
        {
            accumulator.Accumulate([DataValue.FromInt32(v), DataValue.FromInt32(v)]);
        }

        Assert.Equal(DataKind.Float64, outputKind);
        Assert.Equal(1.0, accumulator.Result.AsFloat64(), precision: 10);
    }
}

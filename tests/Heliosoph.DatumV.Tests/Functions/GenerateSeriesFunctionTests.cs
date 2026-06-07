using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.TableValued;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Functions;

/// <summary>
/// Unit tests for the <see cref="GenerateSeriesFunction"/> table-valued
/// function — PostgreSQL-style inclusive <c>[start, stop]</c> semantics,
/// output column named <c>generate_series</c>, numeric overloads
/// (Int32 / Int64 / Float64). End-to-end timestamp coverage lives in
/// <c>IntervalSqlTests</c>.
/// </summary>
public class GenerateSeriesFunctionTests : ServiceTestBase
{
    private readonly GenerateSeriesFunction _function = new();

    [Fact]
    public void Name_IsGenerateSeries()
    {
        Assert.Equal("generate_series", GenerateSeriesFunction.Name);
    }

    [Fact]
    public async Task GenerateSeries_BasicInclusive_IncludesUpperBound()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromInt32(0),
            ValueRef.FromInt32(5)]);

        // [0, 5] → 0, 1, 2, 3, 4, 5
        Assert.Equal(6, rows.Count);
        Assert.Equal(DataKind.Int32, rows[0]["value"].Kind);
        for (int i = 0; i <= 5; i++)
        {
            Assert.Equal(i, rows[i]["value"].AsInt32());
        }
    }

    [Fact]
    public async Task GenerateSeries_Int64_WithStep_Inclusive()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromInt64(0L),
            ValueRef.FromInt64(4L),
            ValueRef.FromInt64(2L)]);

        // [0, 4] step 2 → 0, 2, 4
        Assert.Equal(3, rows.Count);
        Assert.Equal(DataKind.Int64, rows[0]["value"].Kind);
        Assert.Equal(0L, rows[0]["value"].AsInt64());
        Assert.Equal(2L, rows[1]["value"].AsInt64());
        Assert.Equal(4L, rows[2]["value"].AsInt64());
    }

    [Fact]
    public async Task GenerateSeries_Float_FractionalStep_Inclusive()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromFloat64(0.0),
            ValueRef.FromFloat64(1.0),
            ValueRef.FromFloat64(0.25)]);

        // [0.0, 1.0] step 0.25 → 0.0, 0.25, 0.5, 0.75, 1.0
        Assert.Equal(5, rows.Count);
        Assert.Equal(0.0, rows[0]["value"].AsFloat64(), 0.001);
        Assert.Equal(1.0, rows[4]["value"].AsFloat64(), 0.001);
    }

    [Fact]
    public async Task GenerateSeries_NegativeStep_Descending()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromInt32(5),
            ValueRef.FromInt32(0),
            ValueRef.FromInt32(-1)]);

        // [5, 0] step -1 → 5, 4, 3, 2, 1, 0
        Assert.Equal(6, rows.Count);
        Assert.Equal(5, rows[0]["value"].AsInt32());
        Assert.Equal(0, rows[5]["value"].AsInt32());
    }

    [Fact]
    public async Task GenerateSeries_StartEqualsStop_IsSingleRow()
    {
        // PG: generate_series(5, 5) emits exactly one row.
        List<Row> rows = await CollectRows([
            ValueRef.FromInt32(5),
            ValueRef.FromInt32(5)]);

        Assert.Single(rows);
        Assert.Equal(5, rows[0]["value"].AsInt32());
    }

    [Fact]
    public async Task GenerateSeries_PositiveStepWithDescendingBounds_IsEmpty()
    {
        // PG semantics: wrong-direction step yields zero rows, not an error.
        List<Row> rows = await CollectRows([
            ValueRef.FromInt32(10),
            ValueRef.FromInt32(0),
            ValueRef.FromInt32(1)]);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GenerateSeries_NegativeStepWithAscendingBounds_IsEmpty()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromInt32(0),
            ValueRef.FromInt32(10),
            ValueRef.FromInt32(-1)]);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GenerateSeries_ZeroStep_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([
                ValueRef.FromInt32(0),
                ValueRef.FromInt32(10),
                ValueRef.FromInt32(0)]));
    }

    [Fact]
    public async Task GenerateSeries_MixedIntAndFloat_PromotesToFloat64()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromInt32(0),
            ValueRef.FromFloat64(2.0)]);

        // [0.0, 2.0] step 1 → 0.0, 1.0, 2.0
        Assert.Equal(3, rows.Count);
        Assert.Equal(DataKind.Float64, rows[0]["value"].Kind);
    }

    [Fact]
    public async Task GenerateSeries_NullStartArgument_IsEmpty()
    {
        // PG semantics: any NULL argument yields zero rows.
        List<Row> rows = await CollectRows([
            ValueRef.Null(DataKind.Int32),
            ValueRef.FromInt32(5)]);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GenerateSeries_NullStopArgument_IsEmpty()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromInt32(0),
            ValueRef.Null(DataKind.Int32)]);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GenerateSeries_NullStepArgument_IsEmpty()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromInt32(0),
            ValueRef.FromInt32(10),
            ValueRef.Null(DataKind.Int32)]);

        Assert.Empty(rows);
    }

    private async Task<List<Row>> CollectRows(ValueRef[] arguments)
    {
        ExecutionContext context = CreateExecutionContext();
        List<Row> rows = [];
        await foreach (RowBatch batch in _function.ExecuteAsync(arguments, context))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rows.Add(batch[i]);
            }
        }
        return rows;
    }
}

using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.TableValued;
using DatumIngest.Model;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Unit tests for the <see cref="RangeFunction"/> table-valued function.
/// </summary>
public class RangeFunctionTests : ServiceTestBase
{
    private readonly RangeFunction _function = new();

    [Fact]
    public void Name_IsRange()
    {
        Assert.Equal("range", RangeFunction.Name);
    }

    [Fact]
    public async Task Range_BasicInclusive_ProducesCorrectRows()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromFloat64(0),
            ValueRef.FromFloat64(5)]);

        Assert.Equal(6, rows.Count);
        for (int i = 0; i <= 5; i++)
        {
            Assert.Equal((double)i, rows[i]["Value"].AsFloat64());
        }
    }

    [Fact]
    public async Task Range_WithStep_ProducesCorrectRows()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromFloat64(0),
            ValueRef.FromFloat64(10),
            ValueRef.FromFloat64(2)]);

        Assert.Equal(6, rows.Count);
        Assert.Equal(0.0, rows[0]["Value"].AsFloat64());
        Assert.Equal(2.0, rows[1]["Value"].AsFloat64());
        Assert.Equal(4.0, rows[2]["Value"].AsFloat64());
        Assert.Equal(6.0, rows[3]["Value"].AsFloat64());
        Assert.Equal(8.0, rows[4]["Value"].AsFloat64());
        Assert.Equal(10.0, rows[5]["Value"].AsFloat64());
    }

    [Fact]
    public async Task Range_FractionalStep_ProducesCorrectRows()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromFloat64(0.0),
            ValueRef.FromFloat64(1.0),
            ValueRef.FromFloat64(0.25)]);

        Assert.Equal(5, rows.Count);
        Assert.Equal(0.0, rows[0]["Value"].AsFloat64(), 0.001);
        Assert.Equal(0.25, rows[1]["Value"].AsFloat64(), 0.001);
        Assert.Equal(0.5, rows[2]["Value"].AsFloat64(), 0.001);
        Assert.Equal(0.75, rows[3]["Value"].AsFloat64(), 0.001);
        Assert.Equal(1.0, rows[4]["Value"].AsFloat64(), 0.001);
    }

    [Fact]
    public async Task Range_SingleValue_ProducesSingleRow()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromFloat64(5),
            ValueRef.FromFloat64(5)]);

        Assert.Single(rows);
        Assert.Equal(5.0, rows[0]["Value"].AsFloat64());
    }

    [Fact]
    public async Task Range_NegativeStep_Descending()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromFloat64(10),
            ValueRef.FromFloat64(0),
            ValueRef.FromFloat64(-2)]);

        Assert.Equal(6, rows.Count);
        Assert.Equal(10.0, rows[0]["Value"].AsFloat64());
        Assert.Equal(8.0, rows[1]["Value"].AsFloat64());
        Assert.Equal(6.0, rows[2]["Value"].AsFloat64());
        Assert.Equal(4.0, rows[3]["Value"].AsFloat64());
        Assert.Equal(2.0, rows[4]["Value"].AsFloat64());
        Assert.Equal(0.0, rows[5]["Value"].AsFloat64());
    }

    [Fact]
    public async Task Range_LargeRange_ProducesCorrectCount()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromFloat64(0),
            ValueRef.FromFloat64(360)]);

        Assert.Equal(361, rows.Count);
        Assert.Equal(0.0, rows[0]["Value"].AsFloat64());
        Assert.Equal(360.0, rows[360]["Value"].AsFloat64());
    }

    [Fact]
    public async Task Range_Int32Args_ProducesInt32Output()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromInt32(0),
            ValueRef.FromInt32(5)]);

        Assert.Equal(6, rows.Count);
        Assert.Equal(DataKind.Int32, rows[0]["Value"].Kind);
        for (int i = 0; i <= 5; i++)
        {
            Assert.Equal(i, rows[i]["Value"].AsInt32());
        }
    }

    [Fact]
    public async Task Range_Int64Args_ProducesInt64Output()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromInt64(0L),
            ValueRef.FromInt64(4L),
            ValueRef.FromInt64(2L)]);

        Assert.Equal(3, rows.Count);
        Assert.Equal(DataKind.Int64, rows[0]["Value"].Kind);
        Assert.Equal(0L, rows[0]["Value"].AsInt64());
        Assert.Equal(2L, rows[1]["Value"].AsInt64());
        Assert.Equal(4L, rows[2]["Value"].AsInt64());
    }

    [Fact]
    public async Task Range_MixedIntAndFloat_PromotesToFloat64()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromInt32(0),
            ValueRef.FromFloat64(2.0)]);

        Assert.Equal(3, rows.Count);
        Assert.Equal(DataKind.Float64, rows[0]["Value"].Kind);
    }

    [Fact]
    public async Task Range_ZeroStep_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([
                ValueRef.FromFloat64(0),
                ValueRef.FromFloat64(10),
                ValueRef.FromFloat64(0)]));
    }

    [Fact]
    public async Task Range_WrongArgCount_TooFew_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([ValueRef.FromFloat64(0)]));
    }

    [Fact]
    public async Task Range_WrongArgCount_TooMany_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([
                ValueRef.FromFloat64(0),
                ValueRef.FromFloat64(10),
                ValueRef.FromFloat64(1),
                ValueRef.FromFloat64(99)]));
    }

    [Fact]
    public async Task Range_PositiveStepWithDescendingBounds_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([
                ValueRef.FromFloat64(10),
                ValueRef.FromFloat64(0),
                ValueRef.FromFloat64(1)]));
    }

    [Fact]
    public async Task Range_NegativeStepWithAscendingBounds_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([
                ValueRef.FromFloat64(0),
                ValueRef.FromFloat64(10),
                ValueRef.FromFloat64(-1)]));
    }

    [Fact]
    public async Task Range_Cancellation_Stops()
    {
        CancellationTokenSource cts = new();
        ExecutionContext context = CreateContextWithToken(cts.Token);
        List<Row> rows = [];

        // Range must span multiple batches (DefaultBatchSize = 1024) so that
        // cancellation is checked on the next MoveNextAsync after the first batch.
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (RowBatch batch in _function.ExecuteAsync(
                [ValueRef.FromFloat64(0), ValueRef.FromFloat64(2048)],
                context))
            {
                for (int index = 0; index < batch.Count; index++)
                {
                    rows.Add(batch[index]);
                    if (rows.Count == 5)
                    {
                        cts.Cancel();
                        break;
                    }
                }
            }
        });

        Assert.Equal(5, rows.Count);
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

    private ExecutionContext CreateContextWithToken(CancellationToken token)
    {
        Pool pool = GetService<Pool>();
        return new(token, FunctionRegistry.CreateDefault(), new TableCatalog(pool), new LocalBufferPool(), pool);
    }
}

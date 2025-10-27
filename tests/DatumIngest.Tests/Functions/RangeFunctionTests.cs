using DatumIngest.Functions.TableValued;
using DatumIngest.Model;

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
        Assert.Equal("range", _function.Name);
    }

    [Fact]
    public async Task Range_BasicInclusive_ProducesCorrectRows()
    {
        List<Row> rows = await CollectRows([
            DataValue.FromFloat64(0),
            DataValue.FromFloat64(5)]);

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
            DataValue.FromFloat64(0),
            DataValue.FromFloat64(10),
            DataValue.FromFloat64(2)]);

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
            DataValue.FromFloat64(0.0),
            DataValue.FromFloat64(1.0),
            DataValue.FromFloat64(0.25)]);

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
            DataValue.FromFloat64(5),
            DataValue.FromFloat64(5)]);

        Assert.Single(rows);
        Assert.Equal(5.0, rows[0]["Value"].AsFloat64());
    }

    [Fact]
    public async Task Range_NegativeStep_Descending()
    {
        List<Row> rows = await CollectRows([
            DataValue.FromFloat64(10),
            DataValue.FromFloat64(0),
            DataValue.FromFloat64(-2)]);

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
            DataValue.FromFloat64(0),
            DataValue.FromFloat64(360)]);

        Assert.Equal(361, rows.Count);
        Assert.Equal(0.0, rows[0]["Value"].AsFloat64());
        Assert.Equal(360.0, rows[360]["Value"].AsFloat64());
    }

    [Fact]
    public async Task Range_ZeroStep_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([
                DataValue.FromFloat64(0),
                DataValue.FromFloat64(10),
                DataValue.FromFloat64(0)]));
    }

    [Fact]
    public async Task Range_WrongArgCount_TooFew_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([DataValue.FromFloat64(0)]));
    }

    [Fact]
    public async Task Range_WrongArgCount_TooMany_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([
                DataValue.FromFloat64(0),
                DataValue.FromFloat64(10),
                DataValue.FromFloat64(1),
                DataValue.FromFloat64(99)]));
    }

    [Fact]
    public async Task Range_PositiveStepWithDescendingBounds_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([
                DataValue.FromFloat64(10),
                DataValue.FromFloat64(0),
                DataValue.FromFloat64(1)]));
    }

    [Fact]
    public async Task Range_NegativeStepWithAscendingBounds_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([
                DataValue.FromFloat64(0),
                DataValue.FromFloat64(10),
                DataValue.FromFloat64(-1)]));
    }

    [Fact]
    public async Task Range_Cancellation_Stops()
    {
        CancellationTokenSource cancellationTokenSource = new();
        List<Row> rows = [];

        // Range must span multiple batches (DefaultBatchSize = 1024) so that
        // cancellation is checked on the next MoveNextAsync after the first batch.
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (RowBatch batch in _function.ExecuteAsync(
                [DataValue.FromFloat64(0), DataValue.FromFloat64(2048)],
                cancellationTokenSource.Token))
            {
                for (int index = 0; index < batch.Count; index++)
                {
                    rows.Add(batch[index]);
                    if (rows.Count == 5)
                    {
                        cancellationTokenSource.Cancel();
                        break;
                    }
                }
            }
        });

        Assert.Equal(5, rows.Count);
    }

    private async Task<List<Row>> CollectRows(DataValue[] arguments)
    {
        List<Row> rows = await _function.ExecuteAsync(arguments, CancellationToken.None).CollectRowsAsync();
        return rows;
    }
}

using DatumIngest.Functions.TableValued;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Unit tests for the <see cref="RangeFunction"/> table-valued function.
/// </summary>
public class RangeFunctionTests
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
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(5)]);

        Assert.Equal(6, rows.Count);
        for (int i = 0; i <= 5; i++)
        {
            Assert.Equal((float)i, rows[i]["Value"].AsFloat32());
        }
    }

    [Fact]
    public async Task Range_WithStep_ProducesCorrectRows()
    {
        List<Row> rows = await CollectRows([
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(10),
            DataValue.FromFloat32(2)]);

        Assert.Equal(6, rows.Count);
        Assert.Equal(0f, rows[0]["Value"].AsFloat32());
        Assert.Equal(2f, rows[1]["Value"].AsFloat32());
        Assert.Equal(4f, rows[2]["Value"].AsFloat32());
        Assert.Equal(6f, rows[3]["Value"].AsFloat32());
        Assert.Equal(8f, rows[4]["Value"].AsFloat32());
        Assert.Equal(10f, rows[5]["Value"].AsFloat32());
    }

    [Fact]
    public async Task Range_FractionalStep_ProducesCorrectRows()
    {
        List<Row> rows = await CollectRows([
            DataValue.FromFloat32(0.0f),
            DataValue.FromFloat32(1.0f),
            DataValue.FromFloat32(0.25f)]);

        Assert.Equal(5, rows.Count);
        Assert.Equal(0.0f, rows[0]["Value"].AsFloat32(), 0.001f);
        Assert.Equal(0.25f, rows[1]["Value"].AsFloat32(), 0.001f);
        Assert.Equal(0.5f, rows[2]["Value"].AsFloat32(), 0.001f);
        Assert.Equal(0.75f, rows[3]["Value"].AsFloat32(), 0.001f);
        Assert.Equal(1.0f, rows[4]["Value"].AsFloat32(), 0.001f);
    }

    [Fact]
    public async Task Range_SingleValue_ProducesSingleRow()
    {
        List<Row> rows = await CollectRows([
            DataValue.FromFloat32(5),
            DataValue.FromFloat32(5)]);

        Assert.Single(rows);
        Assert.Equal(5f, rows[0]["Value"].AsFloat32());
    }

    [Fact]
    public async Task Range_NegativeStep_Descending()
    {
        List<Row> rows = await CollectRows([
            DataValue.FromFloat32(10),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(-2)]);

        Assert.Equal(6, rows.Count);
        Assert.Equal(10f, rows[0]["Value"].AsFloat32());
        Assert.Equal(8f, rows[1]["Value"].AsFloat32());
        Assert.Equal(6f, rows[2]["Value"].AsFloat32());
        Assert.Equal(4f, rows[3]["Value"].AsFloat32());
        Assert.Equal(2f, rows[4]["Value"].AsFloat32());
        Assert.Equal(0f, rows[5]["Value"].AsFloat32());
    }

    [Fact]
    public async Task Range_LargeRange_ProducesCorrectCount()
    {
        List<Row> rows = await CollectRows([
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(360)]);

        Assert.Equal(361, rows.Count);
        Assert.Equal(0f, rows[0]["Value"].AsFloat32());
        Assert.Equal(360f, rows[360]["Value"].AsFloat32());
    }

    [Fact]
    public async Task Range_ZeroStep_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([
                DataValue.FromFloat32(0),
                DataValue.FromFloat32(10),
                DataValue.FromFloat32(0)]));
    }

    [Fact]
    public async Task Range_WrongArgCount_TooFew_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([DataValue.FromFloat32(0)]));
    }

    [Fact]
    public async Task Range_WrongArgCount_TooMany_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([
                DataValue.FromFloat32(0),
                DataValue.FromFloat32(10),
                DataValue.FromFloat32(1),
                DataValue.FromFloat32(99)]));
    }

    [Fact]
    public async Task Range_PositiveStepWithDescendingBounds_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([
                DataValue.FromFloat32(10),
                DataValue.FromFloat32(0),
                DataValue.FromFloat32(1)]));
    }

    [Fact]
    public async Task Range_NegativeStepWithAscendingBounds_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectRows([
                DataValue.FromFloat32(0),
                DataValue.FromFloat32(10),
                DataValue.FromFloat32(-1)]));
    }

    [Fact]
    public async Task Range_Cancellation_Stops()
    {
        CancellationTokenSource cancellationTokenSource = new();
        List<Row> rows = [];

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (Row row in _function.ExecuteAsync(
                [DataValue.FromFloat32(0), DataValue.FromFloat32(1000)],
                cancellationTokenSource.Token))
            {
                rows.Add(row);
                if (rows.Count == 5)
                {
                    cancellationTokenSource.Cancel();
                }
            }
        });

        Assert.Equal(5, rows.Count);
    }

    private async Task<List<Row>> CollectRows(DataValue[] arguments)
    {
        List<Row> rows = [];
        await foreach (Row row in _function.ExecuteAsync(arguments, CancellationToken.None))
        {
            rows.Add(row);
        }
        return rows;
    }
}

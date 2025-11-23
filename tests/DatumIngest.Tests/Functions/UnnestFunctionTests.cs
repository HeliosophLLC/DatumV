using DatumIngest.Functions.TableValued;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

public class UnnestFunctionTests : ServiceTestBase
{
    private readonly UnnestFunction _function = new();

    [Fact]
    public void Name_IsUnnest()
    {
        Assert.Equal("unnest", _function.Name);
    }

    [Fact]
    public async Task Unnest_Vector_ExpandsToScalarRows()
    {
        DataValue vector = DataValue.FromVector([10, 20, 30]);
        List<Row> rows = await CollectRows([vector]);

        Assert.Equal(3, rows.Count);
        Assert.Equal(10f, rows[0]["value"].AsFloat32());
        Assert.Equal(20f, rows[1]["value"].AsFloat32());
        Assert.Equal(30f, rows[2]["value"].AsFloat32());
    }

    [Fact]
    public async Task Unnest_ByteArray_RequiresStoreAwareDispatch()
    {
        Arena arena = new();
        DataValue bytes = DataValue.FromByteArray([1, 2, 3], arena);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await CollectRows([bytes]));
    }

    [Fact]
    public async Task Unnest_EmptyVector_YieldsNoRows()
    {
        DataValue vector = DataValue.FromVector([]);
        List<Row> rows = await CollectRows([vector]);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Unnest_NullInput_YieldsNoRows()
    {
        DataValue nullValue = DataValue.Null(DataKind.Vector);
        List<Row> rows = await CollectRows([nullValue]);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Unnest_WrongArgCount_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await CollectRows([]));
    }

    [Fact]
    public async Task Unnest_UnsupportedKind_Throws()
    {
        DataValue scalar = DataValue.FromFloat32(42);
        await Assert.ThrowsAsync<ArgumentException>(async () => await CollectRows([scalar]));
    }

    [Fact]
    public async Task Unnest_Cancellation_Stops()
    {
        // Vector must span multiple batches (DefaultBatchSize = 1024) so that
        // cancellation is checked on the next MoveNextAsync after the first batch.
        DataValue vector = DataValue.FromVector(new float[2048]);
        CancellationTokenSource cancellationTokenSource = new();
        List<Row> rows = [];

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (RowBatch batch in _function.ExecuteAsync([vector], cancellationTokenSource.Token))
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

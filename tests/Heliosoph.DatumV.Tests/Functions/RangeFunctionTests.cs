using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.TableValued;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Functions;

/// <summary>
/// Unit tests for the <see cref="RangeFunction"/> table-valued function —
/// DuckDB-style half-open <c>[start, stop)</c> semantics, output column
/// named <c>range</c>, numeric (Int32 / Int64 / Float64) and temporal
/// (Timestamp / TimestampTz with Interval stride) overloads.
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
    public async Task Range_BasicHalfOpen_ExcludesUpperBound()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromFloat64(0),
            ValueRef.FromFloat64(5)]);

        // [0, 5) → 0, 1, 2, 3, 4
        Assert.Equal(5, rows.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal((double)i, rows[i]["value"].AsFloat64());
        }
    }

    [Fact]
    public async Task Range_WithStep_HalfOpen()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromFloat64(0),
            ValueRef.FromFloat64(10),
            ValueRef.FromFloat64(2)]);

        // [0, 10) step 2 → 0, 2, 4, 6, 8
        Assert.Equal(5, rows.Count);
        Assert.Equal(0.0, rows[0]["value"].AsFloat64());
        Assert.Equal(2.0, rows[1]["value"].AsFloat64());
        Assert.Equal(4.0, rows[2]["value"].AsFloat64());
        Assert.Equal(6.0, rows[3]["value"].AsFloat64());
        Assert.Equal(8.0, rows[4]["value"].AsFloat64());
    }

    [Fact]
    public async Task Range_FractionalStep_HalfOpen()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromFloat64(0.0),
            ValueRef.FromFloat64(1.0),
            ValueRef.FromFloat64(0.25)]);

        // [0.0, 1.0) step 0.25 → 0.0, 0.25, 0.5, 0.75
        Assert.Equal(4, rows.Count);
        Assert.Equal(0.0, rows[0]["value"].AsFloat64(), 0.001);
        Assert.Equal(0.25, rows[1]["value"].AsFloat64(), 0.001);
        Assert.Equal(0.5, rows[2]["value"].AsFloat64(), 0.001);
        Assert.Equal(0.75, rows[3]["value"].AsFloat64(), 0.001);
    }

    [Fact]
    public async Task Range_StartEqualsStop_IsEmpty()
    {
        // [5, 5) is empty under half-open semantics.
        List<Row> rows = await CollectRows([
            ValueRef.FromFloat64(5),
            ValueRef.FromFloat64(5)]);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Range_NegativeStep_Descending_HalfOpen()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromFloat64(10),
            ValueRef.FromFloat64(0),
            ValueRef.FromFloat64(-2)]);

        // (10, 0] reflected: walks 10, 8, 6, 4, 2 — excludes 0.
        Assert.Equal(5, rows.Count);
        Assert.Equal(10.0, rows[0]["value"].AsFloat64());
        Assert.Equal(8.0, rows[1]["value"].AsFloat64());
        Assert.Equal(6.0, rows[2]["value"].AsFloat64());
        Assert.Equal(4.0, rows[3]["value"].AsFloat64());
        Assert.Equal(2.0, rows[4]["value"].AsFloat64());
    }

    [Fact]
    public async Task Range_LargeRange_HalfOpen()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromFloat64(0),
            ValueRef.FromFloat64(360)]);

        // [0, 360) → 360 rows, last is 359.
        Assert.Equal(360, rows.Count);
        Assert.Equal(0.0, rows[0]["value"].AsFloat64());
        Assert.Equal(359.0, rows[359]["value"].AsFloat64());
    }

    [Fact]
    public async Task Range_Int32Args_ProducesInt32Output()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromInt32(0),
            ValueRef.FromInt32(5)]);

        Assert.Equal(5, rows.Count);
        Assert.Equal(DataKind.Int32, rows[0]["value"].Kind);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(i, rows[i]["value"].AsInt32());
        }
    }

    [Fact]
    public async Task Range_Int64Args_ProducesInt64Output()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromInt64(0L),
            ValueRef.FromInt64(4L),
            ValueRef.FromInt64(2L)]);

        // [0, 4) step 2 → 0, 2
        Assert.Equal(2, rows.Count);
        Assert.Equal(DataKind.Int64, rows[0]["value"].Kind);
        Assert.Equal(0L, rows[0]["value"].AsInt64());
        Assert.Equal(2L, rows[1]["value"].AsInt64());
    }

    [Fact]
    public async Task Range_MixedIntAndFloat_PromotesToFloat64()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromInt32(0),
            ValueRef.FromFloat64(2.0)]);

        // [0.0, 2.0) → 0.0, 1.0
        Assert.Equal(2, rows.Count);
        Assert.Equal(DataKind.Float64, rows[0]["value"].Kind);
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
    public async Task Range_PositiveStepWithDescendingBounds_IsEmpty()
    {
        // PG / DuckDB: wrong-direction step yields zero rows, not an error.
        List<Row> rows = await CollectRows([
            ValueRef.FromFloat64(10),
            ValueRef.FromFloat64(0),
            ValueRef.FromFloat64(1)]);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Range_NegativeStepWithAscendingBounds_IsEmpty()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromFloat64(0),
            ValueRef.FromFloat64(10),
            ValueRef.FromFloat64(-1)]);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Range_Timestamp_HourlyStride_HalfOpen()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromTimestamp(new DateTime(2026, 6, 11, 0, 0, 0)),
            ValueRef.FromTimestamp(new DateTime(2026, 6, 11, 3, 0, 0)),
            ValueRef.FromInterval(new Interval(0, 0, Interval.MicrosPerHour))]);

        // [00:00, 03:00) hourly → 00:00, 01:00, 02:00
        Assert.Equal(3, rows.Count);
        Assert.Equal(DataKind.Timestamp, rows[0]["value"].Kind);
        Assert.Equal(new DateTime(2026, 6, 11, 0, 0, 0), rows[0]["value"].AsTimestamp());
        Assert.Equal(new DateTime(2026, 6, 11, 2, 0, 0), rows[2]["value"].AsTimestamp());
    }

    [Fact]
    public async Task Range_TimestampTz_HalfOpen_PreservesKind()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromTimestampTz(new DateTimeOffset(2026, 6, 11, 0, 0, 0, TimeSpan.Zero)),
            ValueRef.FromTimestampTz(new DateTimeOffset(2026, 6, 11, 1, 0, 0, TimeSpan.Zero)),
            ValueRef.FromInterval(new Interval(0, 0, 30 * Interval.MicrosPerMinute))]);

        // [00:00, 01:00) every 30 min → 00:00, 00:30
        Assert.Equal(2, rows.Count);
        Assert.Equal(DataKind.TimestampTz, rows[0]["value"].Kind);
    }

    [Fact]
    public async Task Range_NullArgument_IsEmpty()
    {
        // PG semantics: any NULL argument yields zero rows.
        List<Row> rows = await CollectRows([
            ValueRef.FromInt32(0),
            ValueRef.Null(DataKind.Int32)]);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Range_NullStepArgument_IsEmpty()
    {
        List<Row> rows = await CollectRows([
            ValueRef.FromInt32(0),
            ValueRef.FromInt32(10),
            ValueRef.Null(DataKind.Int32)]);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Range_Cancellation_Stops()
    {
        CancellationTokenSource cts = new();
        ExecutionContext context = CreateContextWithToken(cts.Token);
        List<Row> rows = [];

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
        Pool pool = CreatePool();
        return new(CreateCatalog(pool), cancellationToken: token);
    }
}

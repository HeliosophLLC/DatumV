using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// Round-trips every typed-array element kind through
/// <see cref="InMemoryTableProvider"/>'s append path
/// (<c>ConvertDataValueToCell</c>, DataValue → CLR cell) and scan path
/// (<c>MaterializeCell</c>, CLR cell → DataValue). Historically only
/// <see cref="DataKind.UInt8"/> arrays survived the append path; every other
/// element kind fell through to its scalar accessor. These tests pin the
/// symmetric behaviour for the full element-kind surface.
/// </summary>
public sealed class InMemoryTableProviderArrayRoundTripTests : ServiceTestBase
{
    // Each test builds a source provider whose cells are CLR arrays of the
    // element kind, scans it into batches carrying array DataValues (reverse
    // path), appends those batches into a fresh provider (forward path), then
    // scans the destination and reads element bytes against the live batch
    // arena before comparing managed copies. See RoundTripTemporalAsync.

    [Fact]
    public async Task Int128Array_RoundTrips()
    {
        Int128[] a = [Int128.MinValue, 0, Int128.MaxValue];
        Int128[] b = [(Int128)(-1), (Int128)123456789012345, (Int128)42];
        List<Int128[]> got = await RoundTripBlittableAsync<Int128>(
            DataKind.Int128, [[a], [b]]);
        Assert.Equal(a, got[0]);
        Assert.Equal(b, got[1]);
    }

    [Fact]
    public async Task UInt128Array_RoundTrips()
    {
        UInt128[] a = [UInt128.MinValue, (UInt128)7, UInt128.MaxValue];
        List<UInt128[]> got = await RoundTripBlittableAsync<UInt128>(
            DataKind.UInt128, [[a]]);
        Assert.Equal(a, got[0]);
    }

    [Fact]
    public async Task DecimalArray_RoundTrips()
    {
        decimal[] a = [0m, -12.3456m, 9999999.5m, decimal.MinValue, decimal.MaxValue];
        List<decimal[]> got = await RoundTripBlittableAsync<decimal>(
            DataKind.Decimal, [[a]]);
        Assert.Equal(a, got[0]);
    }

    [Fact]
    public async Task UuidArray_RoundTrips()
    {
        Guid[] a =
        [
            new Guid("00000000-0000-0000-0000-000000000000"),
            new Guid("11112222-3333-4444-5555-666677778888"),
            new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff"),
        ];
        List<Guid[]> got = await RoundTripBlittableAsync<Guid>(
            DataKind.Uuid, [[a]]);
        Assert.Equal(a, got[0]);
    }

    [Fact]
    public async Task DateArray_RoundTrips()
    {
        DateOnly[] a = [new(1, 1, 1), new(2020, 2, 29), DateOnly.MaxValue];
        List<DateOnly[]> got = await RoundTripTemporalAsync(
            DataKind.Date, [[a]],
            (DataValue v, Arena arena) =>
            {
                ReadOnlySpan<int> days = v.AsArraySpan<int>(arena);
                DateOnly[] result = new DateOnly[days.Length];
                for (int i = 0; i < days.Length; i++) result[i] = DateOnly.FromDayNumber(days[i]);
                return result;
            });
        Assert.Equal(a, got[0]);
    }

    [Fact]
    public async Task TimeArray_RoundTrips()
    {
        TimeOnly[] a = [new(0, 0, 0), new(12, 30, 45), new(23, 59, 59)];
        List<TimeOnly[]> got = await RoundTripTemporalAsync(
            DataKind.Time, [[a]],
            (DataValue v, Arena arena) =>
            {
                ReadOnlySpan<long> ticks = v.AsArraySpan<long>(arena);
                TimeOnly[] result = new TimeOnly[ticks.Length];
                for (int i = 0; i < ticks.Length; i++) result[i] = new TimeOnly(ticks[i]);
                return result;
            });
        Assert.Equal(a, got[0]);
    }

    [Fact]
    public async Task TimestampArray_RoundTrips()
    {
        DateTime[] a =
        [
            new(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            new(1999, 12, 31, 23, 59, 59, DateTimeKind.Unspecified),
        ];
        List<DateTime[]> got = await RoundTripTemporalAsync(
            DataKind.Timestamp, [[a]],
            (DataValue v, Arena arena) =>
            {
                ReadOnlySpan<long> ticks = v.AsArraySpan<long>(arena);
                DateTime[] result = new DateTime[ticks.Length];
                for (int i = 0; i < ticks.Length; i++) result[i] = new DateTime(ticks[i], DateTimeKind.Unspecified);
                return result;
            });
        Assert.Equal(a, got[0]);
    }

    [Fact]
    public async Task TimestampTzArray_RoundTrips()
    {
        DateTimeOffset[] a =
        [
            new(2020, 1, 1, 12, 0, 0, TimeSpan.Zero),
            new(2020, 6, 15, 8, 30, 0, TimeSpan.Zero),
        ];
        List<DateTimeOffset[]> got = await RoundTripTemporalAsync(
            DataKind.TimestampTz, [[a]],
            (DataValue v, Arena arena) =>
            {
                ReadOnlySpan<long> ticks = v.AsArraySpan<long>(arena);
                DateTimeOffset[] result = new DateTimeOffset[ticks.Length];
                for (int i = 0; i < ticks.Length; i++) result[i] = new DateTimeOffset(ticks[i], TimeSpan.Zero);
                return result;
            });
        Assert.Equal(a, got[0]);
    }

    [Fact]
    public async Task DurationArray_RoundTrips()
    {
        TimeSpan[] a = [TimeSpan.Zero, TimeSpan.FromMinutes(90), TimeSpan.FromTicks(-12345)];
        List<TimeSpan[]> got = await RoundTripTemporalAsync(
            DataKind.Duration, [[a]],
            (DataValue v, Arena arena) =>
            {
                ReadOnlySpan<long> ticks = v.AsArraySpan<long>(arena);
                TimeSpan[] result = new TimeSpan[ticks.Length];
                for (int i = 0; i < ticks.Length; i++) result[i] = new TimeSpan(ticks[i]);
                return result;
            });
        Assert.Equal(a, got[0]);
    }

    // ─────────────────────────── Scan drivers ────────────────────────────

    private async Task<List<T[]>> RoundTripBlittableAsync<T>(DataKind kind, object?[][] sourceRows)
        where T : unmanaged
    {
        return await RoundTripTemporalAsync(kind, sourceRows,
            (DataValue v, Arena arena) => v.AsArraySpan<T>(arena).ToArray());
    }

    private async Task<List<TOut[]>> RoundTripTemporalAsync<TOut>(
        DataKind kind, object?[][] sourceRows, Func<DataValue, Arena, TOut[]> read)
    {
        Pool pool = CreatePool();
        string[] columns = ["xs"];

        InMemoryTableProvider source = new(pool, "src", columns, [kind], sourceRows);
        Schema destSchema = new([new ColumnInfo("xs", kind, nullable: true) { IsArray = true }]);
        InMemoryTableProvider dest = new(pool, "dst", destSchema);

        IAppendSession session = dest.BeginAppend();
        await foreach (RowBatch batch in source.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            await session.WriteAsync(batch);
            batch.Dispose();
        }
        await session.CommitAsync();

        List<TOut[]> result = new();
        await foreach (RowBatch batch in dest.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                result.Add(read(batch[r][0], batch.Arena));
            }
            batch.Dispose();
        }
        return result;
    }
}

using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Temporal;

/// <summary>
/// SQL-surface coverage for the foundational temporal helpers: shorthand
/// component extractors (<c>year</c>, <c>month</c>, <c>day</c>, <c>hour</c>,
/// <c>minute</c>, <c>second</c>, <c>quarter</c>, <c>dayofweek</c>,
/// <c>dayofyear</c>), the make-from-components constructors
/// (<c>make_date</c>, <c>make_timestamp</c>, <c>make_time</c>), and the
/// T-SQL-style arithmetic helpers (<c>date_add</c>, <c>date_diff</c>).
/// </summary>
public sealed class TemporalFoundationalFunctionTests : ServiceTestBase
{
    private TableCatalog SingleRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = new string[columns.Length];
        object?[] cells = new object?[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            names[i] = columns[i].Name;
            cells[i] = columns[i].Value;
        }
        return CreateCatalog("data", names, cells);
    }

    // ─── Shorthand extractors ──────────────────────────────────────────

    [Fact]
    public async Task Year_FromDate()
    {
        TableCatalog catalog = SingleRow(("d", DataValue.FromDate(new DateOnly(2026, 6, 11))));
        List<Row> rows = await ExecuteQueryAsync("SELECT year(d) AS y FROM data", catalog);
        Assert.Equal(DataKind.Float32, rows[0]["y"].Kind);
        Assert.Equal(2026f, rows[0]["y"].AsFloat32());
    }

    [Fact]
    public async Task Month_FromTimestamp()
    {
        TableCatalog catalog = SingleRow(("ts", DataValue.FromTimestamp(new DateTime(2026, 3, 15, 14, 30, 0))));
        List<Row> rows = await ExecuteQueryAsync("SELECT month(ts) AS m FROM data", catalog);
        Assert.Equal(3f, rows[0]["m"].AsFloat32());
    }

    [Fact]
    public async Task Day_FromTimestampTz()
    {
        TableCatalog catalog = SingleRow(("ts", DataValue.FromTimestampTz(new DateTimeOffset(2026, 6, 11, 0, 0, 0, TimeSpan.Zero))));
        List<Row> rows = await ExecuteQueryAsync("SELECT day(ts) AS d FROM data", catalog);
        Assert.Equal(11f, rows[0]["d"].AsFloat32());
    }

    [Fact]
    public async Task Hour_FromTimestamp_AndTime()
    {
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestamp(new DateTime(2026, 6, 11, 14, 30, 45))),
            ("t",  DataValue.FromTime(new TimeOnly(9, 15, 0))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT hour(ts) AS h_ts, hour(t) AS h_t FROM data", catalog);
        Assert.Equal(14f, rows[0]["h_ts"].AsFloat32());
        Assert.Equal(9f,  rows[0]["h_t"].AsFloat32());
    }

    [Fact]
    public async Task Hour_FromDate_IsZero()
    {
        TableCatalog catalog = SingleRow(("d", DataValue.FromDate(new DateOnly(2026, 6, 11))));
        List<Row> rows = await ExecuteQueryAsync("SELECT hour(d) AS h FROM data", catalog);
        Assert.Equal(0f, rows[0]["h"].AsFloat32());
    }

    [Fact]
    public async Task MinuteAndSecond_FromTimestamp()
    {
        TableCatalog catalog = SingleRow(("ts", DataValue.FromTimestamp(new DateTime(2026, 6, 11, 14, 30, 45))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT minute(ts) AS m, second(ts) AS s FROM data", catalog);
        Assert.Equal(30f, rows[0]["m"].AsFloat32());
        Assert.Equal(45f, rows[0]["s"].AsFloat32());
    }

    [Fact]
    public async Task Second_DropsFractionalPart()
    {
        // The shorthand returns whole seconds, unlike date_part('second',...)
        // which returns fractional seconds.
        TableCatalog catalog = SingleRow(("ts", DataValue.FromTimestamp(
            new DateTime(2026, 6, 11, 14, 30, 45, 500))));
        List<Row> rows = await ExecuteQueryAsync("SELECT second(ts) AS s FROM data", catalog);
        Assert.Equal(45f, rows[0]["s"].AsFloat32());
    }

    [Fact]
    public async Task Quarter_FromDate()
    {
        TableCatalog catalog = SingleRow(
            ("q1", DataValue.FromDate(new DateOnly(2026, 2, 15))),
            ("q2", DataValue.FromDate(new DateOnly(2026, 4, 15))),
            ("q3", DataValue.FromDate(new DateOnly(2026, 8, 15))),
            ("q4", DataValue.FromDate(new DateOnly(2026, 11, 15))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT quarter(q1) AS a, quarter(q2) AS b, quarter(q3) AS c, quarter(q4) AS d FROM data",
            catalog);
        Assert.Equal(1f, rows[0]["a"].AsFloat32());
        Assert.Equal(2f, rows[0]["b"].AsFloat32());
        Assert.Equal(3f, rows[0]["c"].AsFloat32());
        Assert.Equal(4f, rows[0]["d"].AsFloat32());
    }

    [Fact]
    public async Task DayOfWeek_IsoConvention_MondayIs1_SundayIs7()
    {
        TableCatalog catalog = SingleRow(
            ("mon", DataValue.FromDate(new DateOnly(2026, 6, 8))),   // Mon
            ("sun", DataValue.FromDate(new DateOnly(2026, 6, 14)))); // Sun
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT dayofweek(mon) AS m, dayofweek(sun) AS s FROM data", catalog);
        Assert.Equal(1f, rows[0]["m"].AsFloat32());
        Assert.Equal(7f, rows[0]["s"].AsFloat32());
    }

    [Fact]
    public async Task DayOfYear_FromDate()
    {
        TableCatalog catalog = SingleRow(
            ("d1", DataValue.FromDate(new DateOnly(2026, 1, 1))),
            ("d2", DataValue.FromDate(new DateOnly(2026, 12, 31))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT dayofyear(d1) AS a, dayofyear(d2) AS b FROM data", catalog);
        Assert.Equal(1f,   rows[0]["a"].AsFloat32());
        Assert.Equal(365f, rows[0]["b"].AsFloat32());
    }

    [Fact]
    public async Task Extractor_NullInput_ReturnsNullFloat32()
    {
        TableCatalog catalog = SingleRow(("d", DataValue.Null(DataKind.Date)));
        List<Row> rows = await ExecuteQueryAsync("SELECT year(d) AS y FROM data", catalog);
        Assert.True(rows[0]["y"].IsNull);
        Assert.Equal(DataKind.Float32, rows[0]["y"].Kind);
    }

    // ─── Constructors ──────────────────────────────────────────────────

    [Fact]
    public async Task MakeDate_BuildsDate()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT make_date(2026, 6, 11) AS d FROM data", catalog);
        Assert.Equal(DataKind.Date, rows[0]["d"].Kind);
        Assert.Equal(new DateOnly(2026, 6, 11), rows[0]["d"].AsDate());
    }

    [Fact]
    public async Task MakeDate_InvalidComponents_Throws()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        ExpressionEvaluationException ex = await Assert.ThrowsAsync<ExpressionEvaluationException>(() =>
            ExecuteQueryAsync("SELECT make_date(2026, 13, 1) AS d FROM data", catalog));
        Assert.Contains("invalid date", ex.Message);
    }

    [Fact]
    public async Task MakeTimestamp_ReturnsTimestamp_NotTimestampTz()
    {
        // PG compatibility: make_timestamp returns timestamp without tz.
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT make_timestamp(2026, 6, 11, 14, 30, 45) AS ts FROM data", catalog);
        Assert.Equal(DataKind.Timestamp, rows[0]["ts"].Kind);
        Assert.Equal(new DateTime(2026, 6, 11, 14, 30, 45), rows[0]["ts"].AsTimestamp());
    }

    [Fact]
    public async Task MakeTimestamp_FractionalSeconds_ApplyAsTicks()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT make_timestamp(2026, 6, 11, 14, 30, 45.5) AS ts FROM data", catalog);
        DateTime expected = new DateTime(2026, 6, 11, 14, 30, 45)
            .AddTicks(TimeSpan.TicksPerSecond / 2);
        Assert.Equal(expected, rows[0]["ts"].AsTimestamp());
    }

    [Fact]
    public async Task MakeTime_BuildsTime()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT make_time(14, 30, 0) AS t FROM data", catalog);
        Assert.Equal(DataKind.Time, rows[0]["t"].Kind);
        Assert.Equal(new TimeOnly(14, 30, 0), rows[0]["t"].AsTime());
    }

    [Fact]
    public async Task MakeTime_FractionalSeconds()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT make_time(9, 15, 30.25) AS t FROM data", catalog);
        TimeOnly expected = new TimeOnly(9, 15, 30).Add(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 4));
        Assert.Equal(expected, rows[0]["t"].AsTime());
    }

    // ─── date_add ──────────────────────────────────────────────────────

    [Fact]
    public async Task DateAdd_Month_ToDate_PreservesDateKind()
    {
        TableCatalog catalog = SingleRow(("d", DataValue.FromDate(new DateOnly(2026, 1, 31))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_add('month', 1, d) AS r FROM data", catalog);
        Assert.Equal(DataKind.Date, rows[0]["r"].Kind);
        // 2026-01-31 + 1 month = 2026-02-28 (Gregorian end-of-month clamp).
        Assert.Equal(new DateOnly(2026, 2, 28), rows[0]["r"].AsDate());
    }

    [Fact]
    public async Task DateAdd_Day_ToTimestamp()
    {
        TableCatalog catalog = SingleRow(("ts", DataValue.FromTimestamp(new DateTime(2026, 6, 11, 14, 0, 0))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_add('day', 3, ts) AS r FROM data", catalog);
        Assert.Equal(new DateTime(2026, 6, 14, 14, 0, 0), rows[0]["r"].AsTimestamp());
    }

    [Fact]
    public async Task DateAdd_Hour_ToDate_PromotesToTimestamp()
    {
        TableCatalog catalog = SingleRow(("d", DataValue.FromDate(new DateOnly(2026, 6, 11))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_add('hour', 5, d) AS r FROM data", catalog);
        Assert.Equal(DataKind.Timestamp, rows[0]["r"].Kind);
        Assert.Equal(new DateTime(2026, 6, 11, 5, 0, 0), rows[0]["r"].AsTimestamp());
    }

    [Fact]
    public async Task DateAdd_NegativeAmount_GoesBackwards()
    {
        TableCatalog catalog = SingleRow(("ts", DataValue.FromTimestamp(new DateTime(2026, 6, 11, 12, 0, 0))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_add('hour', -3, ts) AS r FROM data", catalog);
        Assert.Equal(new DateTime(2026, 6, 11, 9, 0, 0), rows[0]["r"].AsTimestamp());
    }

    [Fact]
    public async Task DateAdd_AliasPart()
    {
        TableCatalog catalog = SingleRow(("d", DataValue.FromDate(new DateOnly(2026, 1, 1))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_add('y', 1, d) AS r FROM data", catalog);
        Assert.Equal(new DateOnly(2027, 1, 1), rows[0]["r"].AsDate());
    }

    [Fact]
    public async Task DateAdd_TimestampTz_PreservesKind()
    {
        TableCatalog catalog = SingleRow(("tz", DataValue.FromTimestampTz(
            new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_add('hour', 1, tz) AS r FROM data", catalog);
        Assert.Equal(DataKind.TimestampTz, rows[0]["r"].Kind);
        Assert.Equal(new DateTimeOffset(2026, 6, 11, 13, 0, 0, TimeSpan.Zero),
            rows[0]["r"].AsTimestampTz());
    }

    // ─── date_diff ─────────────────────────────────────────────────────

    [Fact]
    public async Task DateDiff_Day_CountsCalendarBoundaries()
    {
        // 23:00 to 01:00 next day → one day boundary crossed, not 2 hours.
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromTimestamp(new DateTime(2026, 6, 11, 23, 0, 0))),
            ("b", DataValue.FromTimestamp(new DateTime(2026, 6, 12, 1, 0, 0))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_diff('day', a, b) AS r FROM data", catalog);
        Assert.Equal(DataKind.Float32, rows[0]["r"].Kind);
        Assert.Equal(1f, rows[0]["r"].AsFloat32());
    }

    [Fact]
    public async Task DateDiff_Month_CountsMonthBoundaries()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromDate(new DateOnly(2026, 6, 30))),
            ("b", DataValue.FromDate(new DateOnly(2026, 7, 1))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_diff('month', a, b) AS r FROM data", catalog);
        Assert.Equal(1f, rows[0]["r"].AsFloat32());
    }

    [Fact]
    public async Task DateDiff_Year_CountsYearBoundaries()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromDate(new DateOnly(2025, 12, 31))),
            ("b", DataValue.FromDate(new DateOnly(2026, 1, 1))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_diff('year', a, b) AS r FROM data", catalog);
        Assert.Equal(1f, rows[0]["r"].AsFloat32());
    }

    [Fact]
    public async Task DateDiff_Hour_ReturnsElapsed()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromTimestamp(new DateTime(2026, 6, 11, 8, 0, 0))),
            ("b", DataValue.FromTimestamp(new DateTime(2026, 6, 11, 13, 30, 0))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_diff('hour', a, b) AS r FROM data", catalog);
        Assert.Equal(5.5f, rows[0]["r"].AsFloat32());
    }

    [Fact]
    public async Task DateDiff_NegativeWhenEndBeforeStart()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromDate(new DateOnly(2026, 6, 11))),
            ("b", DataValue.FromDate(new DateOnly(2026, 6, 1))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_diff('day', a, b) AS r FROM data", catalog);
        Assert.Equal(-10f, rows[0]["r"].AsFloat32());
    }

    [Fact]
    public async Task DateDiff_NullInput_ReturnsNull()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromDate(new DateOnly(2026, 6, 11))),
            ("b", DataValue.Null(DataKind.Date)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_diff('day', a, b) AS r FROM data", catalog);
        Assert.True(rows[0]["r"].IsNull);
        Assert.Equal(DataKind.Float32, rows[0]["r"].Kind);
    }
}

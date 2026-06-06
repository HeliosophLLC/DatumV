using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// End-to-end SQL coverage for the Postgres-style <c>Interval</c> kind:
/// the typed literal, scalar functions, EXTRACT, and the temporal
/// arithmetic operators (Timestamp/Date ± Interval, Interval ± Interval,
/// Interval × Double). The value-struct mechanics live in
/// <c>IntervalTests</c>; here we pin the SQL surface.
/// </summary>
public sealed class IntervalSqlTests : ServiceTestBase
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

    // ─── Typed literal: INTERVAL '...' ─────────────────────────────────

    [Fact]
    public async Task IntervalLiteral_Parses_OneDay()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT INTERVAL '1 day' AS iv FROM data", catalog);

        Assert.Equal(DataKind.Interval, rows[0]["iv"].Kind);
        Interval iv = rows[0]["iv"].AsInterval();
        Assert.Equal(0, iv.Months);
        Assert.Equal(1, iv.Days);
        Assert.Equal(0L, iv.Microseconds);
    }

    [Fact]
    public async Task IntervalLiteral_Parses_FullVerboseForm()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT INTERVAL '1 year 2 months 3 days 04:05:06' AS iv FROM data", catalog);

        Interval iv = rows[0]["iv"].AsInterval();
        Assert.Equal(14, iv.Months);
        Assert.Equal(3, iv.Days);
        Assert.Equal(14_706_000_000L, iv.Microseconds);
    }

    [Fact]
    public async Task CastStringToInterval_Works()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT CAST('5 days' AS Interval) AS iv FROM data", catalog);
        Assert.Equal(new Interval(0, 5, 0), rows[0]["iv"].AsInterval());
    }

    [Fact]
    public async Task CastIntervalToString_UsesPostgresCanonical()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        Arena scratch = catalog.Pool.Backing.RentArena();
        try
        {
            List<Row> rows = await ExecuteQueryAsync(
                "SELECT CAST(INTERVAL '1 year 2 months 3 days 04:05:06' AS String) AS s FROM data",
                catalog, store: scratch);
            // String spills past the 27-byte inline cap; resolve via the
            // per-query arena.
            Assert.Equal("1 year 2 mons 3 days 04:05:06",
                rows[0]["s"].AsString(scratch));
        }
        finally
        {
            catalog.Pool.Backing.TryReturn(scratch);
        }
    }

    [Fact]
    public async Task CastDurationToInterval_LossLess()
    {
        TableCatalog catalog = SingleRow(
            ("d", DataValue.FromDuration(TimeSpan.FromHours(49))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT CAST(d AS Interval) AS iv FROM data", catalog);

        Interval iv = rows[0]["iv"].AsInterval();
        Assert.Equal(0, iv.Months);
        Assert.Equal(2, iv.Days);
        Assert.Equal(Interval.MicrosPerHour, iv.Microseconds);
    }

    // ─── make_interval ─────────────────────────────────────────────────

    [Fact]
    public async Task MakeInterval_BuildsFromComponents()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT make_interval(1, 2, 0, 3, 4, 5, 6) AS iv FROM data", catalog);
        Interval iv = rows[0]["iv"].AsInterval();
        Assert.Equal(14, iv.Months);
        Assert.Equal(3, iv.Days);
        Assert.Equal(14_706_000_000L, iv.Microseconds);
    }

    [Fact]
    public async Task MakeInterval_FractionalSeconds_RoundsToMicros()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT make_interval(0, 0, 0, 0, 0, 0, 1.5) AS iv FROM data", catalog);
        Assert.Equal(1_500_000L, rows[0]["iv"].AsInterval().Microseconds);
    }

    // ─── justify_* ─────────────────────────────────────────────────────

    [Fact]
    public async Task JustifyHours_PushesExcessIntoDays()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT justify_hours(INTERVAL '36 hours') AS iv FROM data", catalog);
        Interval iv = rows[0]["iv"].AsInterval();
        Assert.Equal(1, iv.Days);
        Assert.Equal(12 * Interval.MicrosPerHour, iv.Microseconds);
    }

    [Fact]
    public async Task JustifyDays_PushesExcessIntoMonths()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT justify_days(INTERVAL '35 days') AS iv FROM data", catalog);
        Interval iv = rows[0]["iv"].AsInterval();
        Assert.Equal(1, iv.Months);
        Assert.Equal(5, iv.Days);
    }

    [Fact]
    public async Task JustifyInterval_FullNormalisation()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT justify_interval(INTERVAL '35 days 25 hours') AS iv FROM data", catalog);
        Interval iv = rows[0]["iv"].AsInterval();
        Assert.Equal(1, iv.Months);
        Assert.Equal(6, iv.Days);
        Assert.Equal(Interval.MicrosPerHour, iv.Microseconds);
    }

    // ─── EXTRACT(field FROM interval) → date_part ─────────────────────

    [Fact]
    public async Task ExtractYear_FromInterval()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT EXTRACT(year FROM INTERVAL '14 months') AS y FROM data", catalog);
        Assert.Equal(1.0, rows[0]["y"].AsFloat64());
    }

    [Fact]
    public async Task ExtractMonth_FromInterval_IsRemainder()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT EXTRACT(month FROM INTERVAL '14 months') AS m FROM data", catalog);
        Assert.Equal(2.0, rows[0]["m"].AsFloat64());
    }

    [Fact]
    public async Task ExtractEpoch_FromInterval_TotalSeconds()
    {
        // 1 hour interval → 3600 seconds.
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT EXTRACT(epoch FROM INTERVAL '1 hour') AS e FROM data", catalog);
        Assert.Equal(3600.0, rows[0]["e"].AsFloat64());
    }

    [Fact]
    public async Task ExtractHour_FromInterval_WrapsAt24()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT EXTRACT(hour FROM INTERVAL '1 day 5 hours') AS h FROM data", catalog);
        Assert.Equal(5.0, rows[0]["h"].AsFloat64());
    }

    // ─── Arithmetic ────────────────────────────────────────────────────

    [Fact]
    public async Task TimestampPlusInterval_MonthAware()
    {
        // 2024-01-31 + 1 month = 2024-02-29 (leap clamp).
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestamp(new DateTime(2024, 1, 31))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT ts + INTERVAL '1 month' AS r FROM data", catalog);

        Assert.Equal(DataKind.Timestamp, rows[0]["r"].Kind);
        Assert.Equal(new DateTime(2024, 2, 29), rows[0]["r"].AsTimestamp());
    }

    [Fact]
    public async Task TimestampMinusInterval_ShiftsBackwards()
    {
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestamp(new DateTime(2026, 6, 11, 12, 0, 0))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT ts - INTERVAL '1 day' AS r FROM data", catalog);
        Assert.Equal(new DateTime(2026, 6, 10, 12, 0, 0), rows[0]["r"].AsTimestamp());
    }

    [Fact]
    public async Task TimestampTzPlusInterval_PreservesKind()
    {
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestampTz(new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT ts + INTERVAL '2 hours' AS r FROM data", catalog);

        Assert.Equal(DataKind.TimestampTz, rows[0]["r"].Kind);
        Assert.Equal(
            new DateTimeOffset(2026, 6, 11, 14, 0, 0, TimeSpan.Zero),
            rows[0]["r"].AsTimestampTz());
    }

    [Fact]
    public async Task DatePlusInterval_ReturnsTimestamp()
    {
        TableCatalog catalog = SingleRow(("d", DataValue.FromDate(new DateOnly(2026, 6, 11))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT d + INTERVAL '12 hours' AS r FROM data", catalog);
        Assert.Equal(DataKind.Timestamp, rows[0]["r"].Kind);
        Assert.Equal(new DateTime(2026, 6, 11, 12, 0, 0), rows[0]["r"].AsTimestamp());
    }

    [Fact]
    public async Task IntervalPlusInterval_FieldWise()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT INTERVAL '1 month' + INTERVAL '5 days' AS r FROM data", catalog);

        Interval iv = rows[0]["r"].AsInterval();
        Assert.Equal(1, iv.Months);
        Assert.Equal(5, iv.Days);
    }

    [Fact]
    public async Task IntervalMinusInterval_FieldWise()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT INTERVAL '5 days' - INTERVAL '2 days' AS r FROM data", catalog);
        Assert.Equal(new Interval(0, 3, 0), rows[0]["r"].AsInterval());
    }

    [Fact]
    public async Task IntervalTimesNumber_ScalesEveryComponent()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT INTERVAL '1 hour' * 2 AS r FROM data", catalog);
        Assert.Equal(DataKind.Interval, rows[0]["r"].Kind);
        Assert.Equal(2 * Interval.MicrosPerHour, rows[0]["r"].AsInterval().Microseconds);
    }

    [Fact]
    public async Task TimestampMinusTimestamp_ReturnsDuration_NotInterval()
    {
        // Existing PG behavior preserved: ts - ts returns Duration (not Interval).
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromTimestamp(new DateTime(2026, 6, 12))),
            ("b", DataValue.FromTimestamp(new DateTime(2026, 6, 11))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT a - b AS r FROM data", catalog);
        Assert.Equal(DataKind.Duration, rows[0]["r"].Kind);
        Assert.Equal(TimeSpan.FromDays(1), rows[0]["r"].AsDuration());
    }

    [Fact]
    public async Task NullInterval_PropagatesThroughArithmetic()
    {
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestamp(new DateTime(2026, 6, 11))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT ts + CAST(NULL AS Interval) AS r FROM data", catalog);
        Assert.True(rows[0]["r"].IsNull);
        Assert.Equal(DataKind.Timestamp, rows[0]["r"].Kind);
    }

    // ─── Slice 2: ISO 8601 + SQL-standard literal forms ───────────────

    [Fact]
    public async Task IntervalLiteral_Iso8601()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT INTERVAL 'P1Y2M3DT4H5M6S' AS iv FROM data", catalog);
        Interval iv = rows[0]["iv"].AsInterval();
        Assert.Equal(14, iv.Months);
        Assert.Equal(3, iv.Days);
        Assert.Equal(14_706_000_000L, iv.Microseconds);
    }

    [Fact]
    public async Task IntervalLiteral_SqlStandard_YearMonth()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT INTERVAL '1-2' AS iv FROM data", catalog);
        Interval iv = rows[0]["iv"].AsInterval();
        Assert.Equal(14, iv.Months);
        Assert.Equal(0, iv.Days);
        Assert.Equal(0L, iv.Microseconds);
    }

    [Fact]
    public async Task IntervalLiteral_SqlStandard_DayTime()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT INTERVAL '3 04:05:06' AS iv FROM data", catalog);
        Interval iv = rows[0]["iv"].AsInterval();
        Assert.Equal(0, iv.Months);
        Assert.Equal(3, iv.Days);
        Assert.Equal(14_706_000_000L, iv.Microseconds);
    }

    // ─── Slice 2: qualifier ───────────────────────────────────────────

    [Fact]
    public async Task IntervalLiteral_QualifierDisambiguates_BareNumber()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT INTERVAL '90' MINUTE AS iv FROM data", catalog);
        Assert.Equal(5_400_000_000L, rows[0]["iv"].AsInterval().Microseconds);
    }

    [Fact]
    public async Task IntervalLiteral_QualifierYearToMonth()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT INTERVAL '1-2' YEAR TO MONTH AS iv FROM data", catalog);
        Assert.Equal(14, rows[0]["iv"].AsInterval().Months);
    }

    [Fact]
    public async Task IntervalLiteral_QualifierYear_TruncatesSubYearFields()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT INTERVAL '1 year 2 months 3 days' YEAR AS iv FROM data", catalog);
        Interval iv = rows[0]["iv"].AsInterval();
        Assert.Equal(12, iv.Months);
        Assert.Equal(0, iv.Days);
    }

    // ─── Slice 2: date_trunc ──────────────────────────────────────────

    [Fact]
    public async Task DateTrunc_Hour_OnTimestamp()
    {
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestamp(new DateTime(2026, 6, 11, 12, 34, 56))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_trunc('hour', ts) AS r FROM data", catalog);
        Assert.Equal(new DateTime(2026, 6, 11, 12, 0, 0), rows[0]["r"].AsTimestamp());
    }

    [Fact]
    public async Task DateTrunc_Month_OnTimestamp()
    {
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestamp(new DateTime(2026, 6, 11, 12, 34, 56))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_trunc('month', ts) AS r FROM data", catalog);
        Assert.Equal(new DateTime(2026, 6, 1), rows[0]["r"].AsTimestamp());
    }

    [Fact]
    public async Task DateTrunc_Week_SnapsToMonday()
    {
        // 2026-06-11 is a Thursday → ISO week starts Monday 2026-06-08.
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestamp(new DateTime(2026, 6, 11, 12, 34, 56))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_trunc('week', ts) AS r FROM data", catalog);
        Assert.Equal(new DateTime(2026, 6, 8), rows[0]["r"].AsTimestamp());
    }

    [Fact]
    public async Task DateTrunc_Quarter_OnTimestamp()
    {
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestamp(new DateTime(2026, 8, 11))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_trunc('quarter', ts) AS r FROM data", catalog);
        Assert.Equal(new DateTime(2026, 7, 1), rows[0]["r"].AsTimestamp());
    }

    [Fact]
    public async Task DateTrunc_TimestampTz_PreservesKind()
    {
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestampTz(new DateTimeOffset(2026, 6, 11, 12, 34, 56, TimeSpan.Zero))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_trunc('day', ts) AS r FROM data", catalog);
        Assert.Equal(DataKind.TimestampTz, rows[0]["r"].Kind);
        Assert.Equal(new DateTimeOffset(2026, 6, 11, 0, 0, 0, TimeSpan.Zero),
            rows[0]["r"].AsTimestampTz());
    }

    // ─── Slice 2: date_bin ────────────────────────────────────────────

    [Fact]
    public async Task DateBin_FiveMinuteBuckets()
    {
        // 12:34:56 with 5-minute stride from origin 2000-01-01 → 12:30:00.
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestamp(new DateTime(2026, 6, 11, 12, 34, 56))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_bin(INTERVAL '5 minutes', ts, TIMESTAMP '2000-01-01') AS r FROM data",
            catalog);
        Assert.Equal(new DateTime(2026, 6, 11, 12, 30, 0), rows[0]["r"].AsTimestamp());
    }

    [Fact]
    public async Task DateBin_FifteenSecondBuckets()
    {
        // 12:34:53 with 15-second stride → 12:34:45.
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestamp(new DateTime(2026, 6, 11, 12, 34, 53))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_bin(INTERVAL '15 seconds', ts, TIMESTAMP '2000-01-01') AS r FROM data",
            catalog);
        Assert.Equal(new DateTime(2026, 6, 11, 12, 34, 45), rows[0]["r"].AsTimestamp());
    }

    // ─── Slice 3: make_interval optional args ─────────────────────────

    [Fact]
    public async Task MakeInterval_NoArgs_ReturnsZero()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT make_interval() AS iv FROM data", catalog);
        Assert.Equal(Interval.Zero, rows[0]["iv"].AsInterval());
    }

    [Fact]
    public async Task MakeInterval_PartialPrefix_DefaultsRemainingToZero()
    {
        // make_interval(1) → 1 year only.
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT make_interval(1) AS iv FROM data", catalog);
        Interval iv = rows[0]["iv"].AsInterval();
        Assert.Equal(12, iv.Months);
        Assert.Equal(0, iv.Days);
        Assert.Equal(0L, iv.Microseconds);
    }

    [Fact]
    public async Task MakeInterval_ThreeArgs_YearsMonthsWeeks()
    {
        // make_interval(1, 2, 3) → 1 year, 2 months, 21 days (3 weeks).
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT make_interval(1, 2, 3) AS iv FROM data", catalog);
        Interval iv = rows[0]["iv"].AsInterval();
        Assert.Equal(14, iv.Months);
        Assert.Equal(21, iv.Days);
        Assert.Equal(0L, iv.Microseconds);
    }

    // ─── Slice 3: age() ───────────────────────────────────────────────

    [Fact]
    public async Task Age_Timestamps_ReturnsCalendarAwareInterval()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromTimestamp(new DateTime(2027, 6, 11))),
            ("b", DataValue.FromTimestamp(new DateTime(2026, 6, 11))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT age(a, b) AS r FROM data", catalog);

        Assert.Equal(DataKind.Interval, rows[0]["r"].Kind);
        Interval iv = rows[0]["r"].AsInterval();
        Assert.Equal(12, iv.Months);
        Assert.Equal(0, iv.Days);
        Assert.Equal(0L, iv.Microseconds);
    }

    [Fact]
    public async Task Age_DistinguishesFromTimestampSubtraction()
    {
        // ts - ts → Duration (1 day); age(ts, ts) → Interval (1 day).
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromTimestamp(new DateTime(2026, 6, 12))),
            ("b", DataValue.FromTimestamp(new DateTime(2026, 6, 11))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT a - b AS diff, age(a, b) AS aged FROM data", catalog);
        Assert.Equal(DataKind.Duration, rows[0]["diff"].Kind);
        Assert.Equal(DataKind.Interval, rows[0]["aged"].Kind);
        Assert.Equal(new Interval(0, 1, 0), rows[0]["aged"].AsInterval());
    }

    // ─── Slice 3: uuidv7(shift interval) ──────────────────────────────

    [Fact]
    public async Task UuidV7_NoArgs_StillWorks()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT uuidv7() AS u FROM data", catalog);
        Assert.Equal(DataKind.Uuid, rows[0]["u"].Kind);
    }

    [Fact]
    public async Task UuidV7_WithShift_ProducesPastTimestamp()
    {
        // A shift of -1 year places the embedded timestamp well before now.
        // Version-7 UUIDs encode milliseconds-since-epoch in the first 48 bits;
        // we extract those and compare to "now" - 1 year ± slack.
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT uuidv7(INTERVAL '-1 year') AS u FROM data", catalog);
        Guid g = rows[0]["u"].AsUuid();
        byte[] bytes = g.ToByteArray(bigEndian: true);
        long ms = ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24)
                | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
        DateTimeOffset embedded = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        DateTimeOffset expected = DateTimeOffset.UtcNow.AddYears(-1);
        // 5-minute slack covers any clock skew between INTERVAL evaluation
        // and the embedded timestamp build.
        Assert.True((embedded - expected).Duration() < TimeSpan.FromMinutes(5),
            $"embedded {embedded:O} not within 5min of expected {expected:O}.");
    }

    // ─── Slice 3: date_part extra fields ──────────────────────────────

    [Fact]
    public async Task DatePart_Julian_OnDate()
    {
        // Julian Day for 2000-01-01 = 2451545 (fixed reference epoch).
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestamp(new DateTime(2000, 1, 1))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_part('julian', ts) AS j FROM data", catalog);
        Assert.Equal(2451545.0, rows[0]["j"].AsFloat64());
    }

    [Fact]
    public async Task DatePart_IsoYear_RolloverWeek()
    {
        // 2025-12-29 falls in ISO week 1 of 2026; isoyear differs from year.
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestamp(new DateTime(2025, 12, 29))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT date_part('year', ts) AS y, date_part('isoyear', ts) AS iy FROM data",
            catalog);
        Assert.Equal(2025.0, rows[0]["y"].AsFloat64());
        Assert.Equal(2026.0, rows[0]["iy"].AsFloat64());
    }

    // ─── Slice 3: generate_series for timestamps ──────────────────────

    [Fact]
    public async Task GenerateSeries_Timestamp_HourlyStride()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT Value FROM generate_series(" +
            "  TIMESTAMP '2026-06-11 00:00:00'," +
            "  TIMESTAMP '2026-06-11 03:00:00'," +
            "  INTERVAL '1 hour')",
            catalog);
        Assert.Equal(4, rows.Count);
        Assert.Equal(new DateTime(2026, 6, 11, 0, 0, 0), rows[0]["Value"].AsTimestamp());
        Assert.Equal(new DateTime(2026, 6, 11, 3, 0, 0), rows[3]["Value"].AsTimestamp());
    }

    [Fact]
    public async Task GenerateSeries_Timestamp_MonthStrideIsCalendarAware()
    {
        // Monthly stride from Jan 31: clamp-then-stay semantics carry the
        // 28th forward (AddMonths on a clamped day doesn't re-anchor to the
        // original 31). PG behaves identically — the stride walks the actual
        // emitted date, not a hidden "intended day".
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT Value FROM generate_series(" +
            "  TIMESTAMP '2026-01-31'," +
            "  TIMESTAMP '2026-04-30'," +
            "  INTERVAL '1 month')",
            catalog);
        Assert.Equal(4, rows.Count);
        Assert.Equal(new DateTime(2026, 1, 31), rows[0]["Value"].AsTimestamp());
        Assert.Equal(new DateTime(2026, 2, 28), rows[1]["Value"].AsTimestamp());
        Assert.Equal(new DateTime(2026, 3, 28), rows[2]["Value"].AsTimestamp());
        Assert.Equal(new DateTime(2026, 4, 28), rows[3]["Value"].AsTimestamp());
    }

    [Fact]
    public async Task GenerateSeries_NegativeStride_WalksBackwards()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT Value FROM generate_series(" +
            "  TIMESTAMP '2026-06-11 03:00:00'," +
            "  TIMESTAMP '2026-06-11 00:00:00'," +
            "  INTERVAL '-1 hour')",
            catalog);
        Assert.Equal(4, rows.Count);
        Assert.Equal(new DateTime(2026, 6, 11, 3, 0, 0), rows[0]["Value"].AsTimestamp());
        Assert.Equal(new DateTime(2026, 6, 11, 0, 0, 0), rows[3]["Value"].AsTimestamp());
    }

    [Fact]
    public async Task GenerateSeries_TimestampTz_PreservesKind()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT Value FROM generate_series(" +
            "  TIMESTAMPTZ '2026-06-11 00:00:00+00'," +
            "  TIMESTAMPTZ '2026-06-11 01:00:00+00'," +
            "  INTERVAL '30 minutes')",
            catalog);
        Assert.Equal(3, rows.Count);
        Assert.Equal(DataKind.TimestampTz, rows[0]["Value"].Kind);
    }

    [Fact]
    public async Task GenerateSeries_ZeroStride_Rejects()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        await Assert.ThrowsAnyAsync<Exception>(
            () => ExecuteQueryAsync(
                "SELECT Value FROM generate_series(" +
                "  TIMESTAMP '2026-06-11'," +
                "  TIMESTAMP '2026-06-12'," +
                "  INTERVAL '0 seconds')",
                catalog));
    }

    [Fact]
    public async Task DateBin_StrideWithMonths_Rejects()
    {
        // PG rejects month-stride because months aren't a fixed duration.
        // The function-level ExecutionException is wrapped in an
        // ExpressionEvaluationException by the evaluator's span-tagging.
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestamp(new DateTime(2026, 6, 11))));
        Exception ex = await Assert.ThrowsAnyAsync<Exception>(
            () => ExecuteQueryAsync(
                "SELECT date_bin(INTERVAL '1 month', ts, TIMESTAMP '2000-01-01') AS r FROM data",
                catalog));
        Assert.Contains("month component", ex.Message);
    }
}

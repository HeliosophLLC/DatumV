using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Data;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Semantics tests for the session time zone: bare-literal interpretation,
/// Timestamp↔TimestampTz and Date↔TimestampTz casts, INSERT literal
/// coercion, <c>date_part</c> zone-aware fields, <c>date_trunc</c>
/// session-zone boundaries, and machine-zone independence of temporal
/// string parsing. America/New_York is the non-UTC fixture zone:
/// UTC-5 in January (EST), UTC-4 in July (EDT) — both DST cases covered.
/// </summary>
public sealed class SessionTimeZoneSemanticsTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public SessionTimeZoneSemanticsTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-sessiontz-sem-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _catalogPath = Path.Combine(_scratchDir, CatalogStore.DefaultFileName);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private async Task<DataValue> ScalarAsync(TableCatalog catalog, string sql)
    {
        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(sql);
        DataValue? value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);
        return value!.Value;
    }

    // ───────────────────── String → TimestampTz ─────────────────────

    [Fact]
    public async Task BareTimestampTzLiteral_DefaultSession_IsUtc()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        DataValue v = await ScalarAsync(catalog, "SELECT TIMESTAMPTZ '2026-01-15 12:00:00'");

        Assert.Equal(new DateTime(2026, 1, 15, 12, 0, 0), v.AsTimestampTz().UtcDateTime);
    }

    [Fact]
    public async Task BareTimestampTzLiteral_SessionZone_InterpretsWallClockThere()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("SET TIME ZONE 'America/New_York'");

        // Noon in New York in January (EST, UTC-5) is 17:00 UTC.
        DataValue v = await ScalarAsync(catalog, "SELECT TIMESTAMPTZ '2026-01-15 12:00:00'");

        Assert.Equal(new DateTime(2026, 1, 15, 17, 0, 0), v.AsTimestampTz().UtcDateTime);
    }

    [Fact]
    public async Task BareTimestampTzLiteral_SessionZone_DstAware()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("SET TIME ZONE 'America/New_York'");

        // Noon in New York in July (EDT, UTC-4) is 16:00 UTC.
        DataValue v = await ScalarAsync(catalog, "SELECT TIMESTAMPTZ '2026-07-15 12:00:00'");

        Assert.Equal(new DateTime(2026, 7, 15, 16, 0, 0), v.AsTimestampTz().UtcDateTime);
    }

    [Fact]
    public async Task ExplicitOffsetLiteral_SessionZone_OffsetWins()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("SET TIME ZONE 'America/New_York'");

        DataValue v = await ScalarAsync(catalog, "SELECT TIMESTAMPTZ '2026-01-15 12:00:00+02'");

        Assert.Equal(new DateTime(2026, 1, 15, 10, 0, 0), v.AsTimestampTz().UtcDateTime);
    }

    [Fact]
    public async Task SetThenCast_InOneBatch_UsesNewZone()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "SET TIME ZONE 'America/New_York'; SELECT TIMESTAMPTZ '2026-01-15 12:00:00'");

        await using InProcessDatumDbReader reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());

        Assert.Equal(
            new DateTime(2026, 1, 15, 17, 0, 0),
            reader.GetValue(0).AsTimestampTz().UtcDateTime);
    }

    // ───────────────────── String → Timestamp (naive) ─────────────────────

    [Fact]
    public async Task TimestampLiteral_WithExplicitOffset_KeepsWallClockAsWritten()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        // PG discards the offset for timestamp-without-time-zone; the wall
        // clock is stored verbatim. Deterministic regardless of machine zone.
        DataValue v = await ScalarAsync(catalog, "SELECT TIMESTAMP '2026-01-15 12:00:00+05'");

        Assert.Equal(new DateTime(2026, 1, 15, 12, 0, 0), v.AsTimestamp());
    }

    // ───────────────────── Timestamp ↔ TimestampTz casts ─────────────────────

    [Fact]
    public async Task TimestampToTimestampTz_AnchorsInSessionZone()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("SET TIME ZONE 'America/New_York'");

        DataValue v = await ScalarAsync(
            catalog, "SELECT CAST(TIMESTAMP '2026-01-15 12:00:00' AS TimestampTz)");

        Assert.Equal(new DateTime(2026, 1, 15, 17, 0, 0), v.AsTimestampTz().UtcDateTime);
    }

    [Fact]
    public async Task TimestampTzToTimestamp_DropsToSessionWallClock()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("SET TIME ZONE 'America/New_York'");

        DataValue v = await ScalarAsync(
            catalog, "SELECT CAST(TIMESTAMPTZ '2026-01-15 17:00:00+00' AS Timestamp)");

        Assert.Equal(new DateTime(2026, 1, 15, 12, 0, 0), v.AsTimestamp());
    }

    // ───────────────────── Date ↔ TimestampTz casts ─────────────────────

    [Fact]
    public async Task DateToTimestampTz_IsSessionZoneMidnight()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("SET TIME ZONE 'America/New_York'");

        DataValue v = await ScalarAsync(
            catalog, "SELECT CAST(DATE '2026-01-15' AS TimestampTz)");

        // Midnight in New York (EST) is 05:00 UTC.
        Assert.Equal(new DateTime(2026, 1, 15, 5, 0, 0), v.AsTimestampTz().UtcDateTime);
    }

    [Fact]
    public async Task TimestampTzToDate_IsSessionZoneDate()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("SET TIME ZONE 'America/New_York'");

        // 03:00 UTC on Jan 15 is still Jan 14 in New York (22:00 EST).
        DataValue v = await ScalarAsync(
            catalog, "SELECT CAST(TIMESTAMPTZ '2026-01-15 03:00:00+00' AS Date)");

        Assert.Equal(new DateOnly(2026, 1, 14), v.AsDate());
    }

    // ───────────────────── INSERT literal coercion ─────────────────────

    [Fact]
    public async Task InsertBareStringIntoTimestampTzColumn_UsesSessionZone()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (ts TIMESTAMPTZ)");
        catalog.Plan("SET TIME ZONE 'America/New_York'");
        catalog.Plan("INSERT INTO t VALUES ('2026-01-15 12:00:00')");

        DataValue v = await ScalarAsync(catalog, "SELECT ts FROM t");

        Assert.Equal(new DateTime(2026, 1, 15, 17, 0, 0), v.AsTimestampTz().UtcDateTime);
    }

    // ───────────────────── date_part / EXTRACT ─────────────────────

    [Fact]
    public async Task DatePart_TimezoneFields_ReportSessionOffset()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("SET TIME ZONE 'America/New_York'");

        DataValue seconds = await ScalarAsync(
            catalog, "SELECT date_part('timezone', TIMESTAMPTZ '2026-01-15 12:00:00+00')");
        DataValue hour = await ScalarAsync(
            catalog, "SELECT date_part('timezone_hour', TIMESTAMPTZ '2026-01-15 12:00:00+00')");
        DataValue summer = await ScalarAsync(
            catalog, "SELECT date_part('timezone_hour', TIMESTAMPTZ '2026-07-15 12:00:00+00')");

        Assert.Equal(-18000.0, seconds.AsFloat64());   // EST = UTC-5
        Assert.Equal(-5.0, hour.AsFloat64());
        Assert.Equal(-4.0, summer.AsFloat64());        // EDT = UTC-4
    }

    [Fact]
    public async Task DatePart_WallClockField_ReadsSessionZoneClock()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("SET TIME ZONE 'America/New_York'");

        // 17:00 UTC is 12:00 in New York; the extracted hour is the local one.
        DataValue hour = await ScalarAsync(
            catalog, "SELECT date_part('hour', TIMESTAMPTZ '2026-01-15 17:00:00+00')");
        // 03:00 UTC on Jan 15 is Jan 14 in New York.
        DataValue day = await ScalarAsync(
            catalog, "SELECT date_part('day', TIMESTAMPTZ '2026-01-15 03:00:00+00')");

        Assert.Equal(12.0, hour.AsFloat64());
        Assert.Equal(14.0, day.AsFloat64());
    }

    [Fact]
    public async Task DatePart_Epoch_IsZoneIndependent()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        DataValue utcEpoch = await ScalarAsync(
            catalog, "SELECT date_part('epoch', TIMESTAMPTZ '2026-01-15 12:00:00+00')");

        catalog.Plan("SET TIME ZONE 'America/New_York'");
        DataValue nyEpoch = await ScalarAsync(
            catalog, "SELECT date_part('epoch', TIMESTAMPTZ '2026-01-15 12:00:00+00')");

        Assert.Equal(utcEpoch.AsFloat64(), nyEpoch.AsFloat64());
    }

    [Fact]
    public async Task DatePart_DefaultUtcSession_TimezoneFieldsAreZero()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        DataValue v = await ScalarAsync(
            catalog, "SELECT date_part('timezone', TIMESTAMPTZ '2026-01-15 12:00:00+00')");

        Assert.Equal(0.0, v.AsFloat64());
    }

    // ───────────────────── date_trunc ─────────────────────

    [Fact]
    public async Task DateTrunc_Day_SnapsToSessionZoneMidnight()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("SET TIME ZONE 'America/New_York'");

        // 03:00 UTC Jan 15 = 22:00 EST Jan 14 → truncates to Jan 14 00:00 EST
        // = Jan 14 05:00 UTC.
        DataValue v = await ScalarAsync(
            catalog, "SELECT date_trunc('day', TIMESTAMPTZ '2026-01-15 03:00:00+00')");

        Assert.Equal(new DateTime(2026, 1, 14, 5, 0, 0), v.AsTimestampTz().UtcDateTime);
    }

    [Fact]
    public async Task DateTrunc_DefaultUtcSession_Unchanged()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        DataValue v = await ScalarAsync(
            catalog, "SELECT date_trunc('day', TIMESTAMPTZ '2026-01-15 03:00:00+00')");

        Assert.Equal(new DateTime(2026, 1, 15, 0, 0, 0), v.AsTimestampTz().UtcDateTime);
    }

    // ───────────────────── Temporal constants ─────────────────────

    [Fact]
    public async Task CurrentDate_ReadsSessionZoneClockFace()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("SET TIME ZONE 'America/New_York'");

        DataValue v = await ScalarAsync(catalog, "SELECT CURRENT_DATE");

        DateOnly expected = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(
                DateTimeOffset.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime);
        // Allow the race where the wall clock crosses midnight between the
        // query and this assertion.
        Assert.True(
            v.AsDate() == expected || Math.Abs(v.AsDate().DayNumber - expected.DayNumber) <= 1,
            $"CURRENT_DATE {v.AsDate()} not within a day of expected {expected}.");
    }
}

using System.Text;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Display/export tests for the session time zone: <c>ToDisplayString</c>
/// projection, CSV / JSON export rendering in the session zone's wall
/// clock (instant-preserving — the offset suffix parses back to the same
/// UTC ticks), and Arrow's deliberate session-independence (binary formats
/// stay UTC-normalized regardless of <c>SET TIME ZONE</c>).
/// </summary>
public sealed class SessionTimeZoneDisplayTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public SessionTimeZoneDisplayTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-sessiontz-disp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _catalogPath = Path.Combine(_scratchDir, CatalogStore.DefaultFileName);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private static string EscapeSql(string path) => path.Replace("'", "''");

    private async Task ExportAsync(TableCatalog catalog, string sql)
    {
        StatementPlan plan = await catalog.PlanAsync(sql);
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }
    }

    private TableCatalog CreateSeededCatalog()
    {
        TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (ts TIMESTAMPTZ)");
        catalog.Plan("INSERT INTO t VALUES ('2026-01-15 17:00:00+00')");
        return catalog;
    }

    // ───────────────────── ToDisplayString ─────────────────────

    [Fact]
    public void ToDisplayString_NoZone_RendersUtc()
    {
        DataValue v = DataValue.FromTimestampTz(
            new DateTimeOffset(2026, 1, 15, 17, 0, 0, TimeSpan.Zero));

        Assert.Equal("2026-01-15T17:00:00.0000000+00:00", v.ToDisplayString());
    }

    [Fact]
    public void ToDisplayString_SessionZone_RendersZoneWallClockWithOffset()
    {
        DataValue v = DataValue.FromTimestampTz(
            new DateTimeOffset(2026, 1, 15, 17, 0, 0, TimeSpan.Zero));
        TimeZoneInfo newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        Assert.Equal(
            "2026-01-15T12:00:00.0000000-05:00",
            v.ToDisplayString(timeZone: newYork));
    }

    // ───────────────────── CSV export ─────────────────────

    [Fact]
    public async Task CsvExport_DefaultSession_RendersUtcOffset()
    {
        using TableCatalog catalog = CreateSeededCatalog();
        string outPath = Path.Combine(_scratchDir, "utc.csv");

        await ExportAsync(catalog, $"COPY (SELECT ts FROM t) TO '{EscapeSql(outPath)}'");

        string text = File.ReadAllText(outPath, Encoding.UTF8);
        Assert.Contains("2026-01-15T17:00:00.0000000+00:00", text);
    }

    [Fact]
    public async Task CsvExport_SessionZone_RendersZoneWallClock_InstantPreserved()
    {
        using TableCatalog catalog = CreateSeededCatalog();
        catalog.Plan("SET TIME ZONE 'America/New_York'");
        string outPath = Path.Combine(_scratchDir, "ny.csv");

        await ExportAsync(catalog, $"COPY (SELECT ts FROM t) TO '{EscapeSql(outPath)}'");

        string text = File.ReadAllText(outPath, Encoding.UTF8);
        Assert.Contains("2026-01-15T12:00:00.0000000-05:00", text);

        // The rendered form carries its offset, so parsing recovers the
        // exact instant that was stored.
        DateTimeOffset parsed = DateTimeOffset.Parse("2026-01-15T12:00:00.0000000-05:00");
        Assert.Equal(new DateTime(2026, 1, 15, 17, 0, 0), parsed.UtcDateTime);
    }

    // ───────────────────── JSON export ─────────────────────

    [Fact]
    public async Task JsonExport_SessionZone_RendersZoneWallClock()
    {
        using TableCatalog catalog = CreateSeededCatalog();
        catalog.Plan("SET TIME ZONE 'America/New_York'");
        string outPath = Path.Combine(_scratchDir, "ny.json");

        await ExportAsync(catalog, $"COPY (SELECT ts FROM t) TO '{EscapeSql(outPath)}'");

        string text = File.ReadAllText(outPath, Encoding.UTF8);
        Assert.Contains("2026-01-15T12:00:00.0000000-05:00", text);
    }

    // ───────────────────── Arrow session-independence ─────────────────────

    [Fact]
    public async Task ArrowExport_IsSessionIndependent_ByteIdentical()
    {
        using TableCatalog catalog = CreateSeededCatalog();

        string utcPath = Path.Combine(_scratchDir, "utc.arrow");
        await ExportAsync(catalog, $"COPY (SELECT ts FROM t) TO '{EscapeSql(utcPath)}'");

        catalog.Plan("SET TIME ZONE 'America/New_York'");
        string nyPath = Path.Combine(_scratchDir, "ny.arrow");
        await ExportAsync(catalog, $"COPY (SELECT ts FROM t) TO '{EscapeSql(nyPath)}'");

        // Binary formats are UTC-normalized instants with UTC schema
        // metadata by design: SET TIME ZONE must not perturb schema
        // fingerprints or round-trips.
        Assert.Equal(File.ReadAllBytes(utcPath), File.ReadAllBytes(nyPath));
    }
}

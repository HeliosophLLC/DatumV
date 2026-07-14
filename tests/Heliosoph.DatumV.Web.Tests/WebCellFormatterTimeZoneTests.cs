using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Web.Execution;

namespace Heliosoph.DatumV.Web.Tests;

/// <summary>
/// Covers <see cref="WebCellFormatter"/>'s session-time-zone rendering of
/// <c>timestamptz</c> cells: the UTC instant is projected into the session
/// zone's wall clock; the default (no zone) renders the UTC form unchanged.
/// </summary>
public sealed class WebCellFormatterTimeZoneTests
{
    private static readonly DataValue Instant =
        DataValue.FromTimestampTz(new DateTimeOffset(2026, 1, 15, 17, 0, 0, TimeSpan.Zero));

    [Fact]
    public void TimestampTz_NoSessionZone_RendersUtcWallClock()
    {
        using Arena arena = new();
        SidecarRegistry registry = new();

        JsonCell cell = WebCellFormatter.Format(Instant, arena, registry);

        Assert.Equal("text", cell.Kind);
        Assert.Equal("2026-01-15 17:00:00", cell.Text);
    }

    [Fact]
    public void TimestampTz_SessionZone_RendersZoneWallClock()
    {
        using Arena arena = new();
        SidecarRegistry registry = new();
        TimeZoneInfo newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        // 17:00 UTC in January is 12:00 in New York (EST).
        JsonCell cell = WebCellFormatter.Format(
            Instant, arena, registry, sessionTimeZone: newYork);

        Assert.Equal("text", cell.Kind);
        Assert.Equal("2026-01-15 12:00:00", cell.Text);
    }
}

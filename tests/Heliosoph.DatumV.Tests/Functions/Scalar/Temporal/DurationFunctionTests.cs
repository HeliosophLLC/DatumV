using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Temporal;

/// <summary>
/// SQL-surface coverage for the Duration helpers: <c>make_duration</c>,
/// <c>time_diff</c>, and the <c>duration_seconds/minutes/hours/days</c>
/// readouts.
/// </summary>
public sealed class DurationFunctionTests : ServiceTestBase
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

    // ─── make_duration ─────────────────────────────────────────────────

    [Fact]
    public async Task MakeDuration_BuildsFromComponents()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT make_duration(1, 2, 30, 45) AS d FROM data", catalog);
        Assert.Equal(DataKind.Duration, rows[0]["d"].Kind);
        TimeSpan expected = TimeSpan.FromDays(1) + TimeSpan.FromHours(2)
            + TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(45);
        Assert.Equal(expected, rows[0]["d"].AsDuration());
    }

    [Fact]
    public async Task MakeDuration_FractionalSeconds_PreserveSubSecondTicks()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT make_duration(0, 0, 0, 1.5) AS d FROM data", catalog);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), rows[0]["d"].AsDuration());
    }

    [Fact]
    public async Task MakeDuration_NullArg_PropagatesNull()
    {
        TableCatalog catalog = SingleRow(("seed", DataValue.FromInt32(0)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT make_duration(1, 0, 0, CAST(NULL AS Float32)) AS d FROM data", catalog);
        Assert.True(rows[0]["d"].IsNull);
        Assert.Equal(DataKind.Duration, rows[0]["d"].Kind);
    }

    // ─── time_diff ─────────────────────────────────────────────────────

    [Fact]
    public async Task TimeDiff_ForwardWithinDay()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromTime(new TimeOnly(9, 0))),
            ("b", DataValue.FromTime(new TimeOnly(17, 30))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT time_diff(a, b) AS r FROM data", catalog);
        Assert.Equal(DataKind.Duration, rows[0]["r"].Kind);
        Assert.Equal(TimeSpan.FromHours(8.5), rows[0]["r"].AsDuration());
    }

    [Fact]
    public async Task TimeDiff_WrapsForwardThroughMidnight()
    {
        // Overnight shift 23:00 → 02:00 = 3 hours, not -21 hours.
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromTime(new TimeOnly(23, 0))),
            ("b", DataValue.FromTime(new TimeOnly(2, 0))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT time_diff(a, b) AS r FROM data", catalog);
        Assert.Equal(TimeSpan.FromHours(3), rows[0]["r"].AsDuration());
    }

    [Fact]
    public async Task TimeDiff_EqualTimes_IsZero()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromTime(new TimeOnly(12, 0))),
            ("b", DataValue.FromTime(new TimeOnly(12, 0))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT time_diff(a, b) AS r FROM data", catalog);
        Assert.Equal(TimeSpan.Zero, rows[0]["r"].AsDuration());
    }

    // ─── duration_* extractors ────────────────────────────────────────

    [Fact]
    public async Task DurationSeconds_TotalsFractional()
    {
        TableCatalog catalog = SingleRow(
            ("d", DataValue.FromDuration(TimeSpan.FromSeconds(125.5))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT duration_seconds(d) AS r FROM data", catalog);
        Assert.Equal(DataKind.Float32, rows[0]["r"].Kind);
        Assert.Equal(125.5f, rows[0]["r"].AsFloat32());
    }

    [Fact]
    public async Task DurationMinutes_Fractional()
    {
        TableCatalog catalog = SingleRow(
            ("d", DataValue.FromDuration(TimeSpan.FromSeconds(90))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT duration_minutes(d) AS r FROM data", catalog);
        Assert.Equal(1.5f, rows[0]["r"].AsFloat32());
    }

    [Fact]
    public async Task DurationHours_Fractional()
    {
        TableCatalog catalog = SingleRow(
            ("d", DataValue.FromDuration(TimeSpan.FromMinutes(90))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT duration_hours(d) AS r FROM data", catalog);
        Assert.Equal(1.5f, rows[0]["r"].AsFloat32());
    }

    [Fact]
    public async Task DurationDays_Fractional()
    {
        TableCatalog catalog = SingleRow(
            ("d", DataValue.FromDuration(TimeSpan.FromHours(36))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT duration_days(d) AS r FROM data", catalog);
        Assert.Equal(1.5f, rows[0]["r"].AsFloat32());
    }

    [Fact]
    public async Task DurationExtractor_NullInput_ReturnsNull()
    {
        TableCatalog catalog = SingleRow(("d", DataValue.Null(DataKind.Duration)));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT duration_seconds(d) AS r FROM data", catalog);
        Assert.True(rows[0]["r"].IsNull);
    }

    // ─── End-to-end: subtraction → Duration → readout ─────────────────

    [Fact]
    public async Task TimestampSubtraction_FeedsDurationExtractor()
    {
        // Replaces the docs-deprecated date_span(start, end): the - operator
        // returns Duration directly, and duration_days reads it as Float32.
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromTimestamp(new DateTime(2026, 1, 1))),
            ("b", DataValue.FromTimestamp(new DateTime(2026, 1, 11))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT duration_days(b - a) AS days FROM data", catalog);
        Assert.Equal(10f, rows[0]["days"].AsFloat32());
    }

    [Fact]
    public async Task TimestampPlusMakeDuration_AddsViaOperator()
    {
        // Replaces the docs-deprecated date_offset(date, duration).
        TableCatalog catalog = SingleRow(
            ("ts", DataValue.FromTimestamp(new DateTime(2026, 6, 11, 9, 0, 0))));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT ts + make_duration(0, 8, 0, 0) AS shift_end FROM data", catalog);
        Assert.Equal(DataKind.Timestamp, rows[0]["shift_end"].Kind);
        Assert.Equal(new DateTime(2026, 6, 11, 17, 0, 0), rows[0]["shift_end"].AsTimestamp());
    }
}

using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Data;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// End-to-end tests for the session time zone: the UTC default,
/// <c>SET TIME ZONE</c> mutation through the planner, <c>SHOW</c> /
/// <c>current_setting()</c> readback (including SET-then-SHOW inside one
/// batch), and rejection of unknown zones and parameters.
/// </summary>
public sealed class SessionTimeZoneTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public SessionTimeZoneTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-sessiontz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _catalogPath = Path.Combine(_scratchDir, CatalogStore.DefaultFileName);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void DefaultSessionTimeZone_IsUtc()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        Assert.Equal("UTC", catalog.SessionTimeZone.Id);
    }

    [Fact]
    public void SetTimeZone_IanaName_UpdatesSessionState()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        catalog.Plan("SET TIME ZONE 'America/New_York'");

        Assert.Equal("America/New_York", catalog.SessionTimeZone.Id);
    }

    [Fact]
    public void SetTimeZone_ParameterForm_UpdatesSessionState()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        catalog.Plan("SET timezone TO 'America/Chicago'");

        Assert.Equal("America/Chicago", catalog.SessionTimeZone.Id);
    }

    [Fact]
    public void SetTimeZone_Default_ResetsToUtc()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("SET TIME ZONE 'America/New_York'");

        catalog.Plan("SET TIME ZONE DEFAULT");

        Assert.Equal("UTC", catalog.SessionTimeZone.Id);
    }

    [Fact]
    public void SetTimeZone_UnknownZone_ThrowsSessionSettingException()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        SessionSettingException ex = Assert.Throws<SessionSettingException>(
            () => catalog.Plan("SET TIME ZONE 'Nowhere/Imaginary'"));

        Assert.Contains("Nowhere/Imaginary", ex.Message);
        // The failed SET must not have clobbered the session state.
        Assert.Equal("UTC", catalog.SessionTimeZone.Id);
    }

    [Fact]
    public void SetTimeZone_InsideBlock_Applies()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        catalog.Plan("BEGIN SET TIME ZONE 'America/Chicago'; END");

        Assert.Equal("America/Chicago", catalog.SessionTimeZone.Id);
    }

    [Fact]
    public async Task ShowTimeZone_ReturnsSingleRowWithGucLabel()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand("SHOW timezone");

        await using InProcessDatumDbReader reader = await command.ExecuteReaderAsync();

        Assert.Equal("TimeZone", reader.GetName(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("UTC", reader.GetString(0));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task SetThenShow_InOneBatch_ReportsPostSetValue()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "SET TIME ZONE 'America/New_York'; SHOW timezone");

        await using InProcessDatumDbReader reader = await command.ExecuteReaderAsync();

        // First result set is the SET — zero rows.
        Assert.False(await reader.ReadAsync());
        Assert.True(await reader.NextResultAsync());

        Assert.True(await reader.ReadAsync());
        Assert.Equal("America/New_York", reader.GetString(0));
    }

    [Fact]
    public async Task ShowSearchPath_ReturnsCommaSeparatedList()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand("SHOW search_path");

        await using InProcessDatumDbReader reader = await command.ExecuteReaderAsync();

        Assert.Equal("search_path", reader.GetName(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("public, system", reader.GetString(0));
    }

    [Fact]
    public async Task ShowUnknownParameter_ThrowsAtPlanTime()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand("SHOW bogus_setting");

        QueryPlanException ex = await Assert.ThrowsAsync<QueryPlanException>(
            () => command.ExecuteReaderAsync());

        Assert.Contains("bogus_setting", ex.Message);
    }

    [Fact]
    public async Task CurrentSetting_TimeZone_ReadsLiveValue()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("SET TIME ZONE 'America/New_York'");

        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "SELECT current_setting('timezone')");

        DataValue? value = await command.ExecuteScalarAsync();

        Assert.NotNull(value);
        Assert.Equal("America/New_York", value!.Value.AsString());
    }

    [Fact]
    public async Task CurrentSetting_SearchPath_ReadsLiveValue()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "SELECT current_setting('search_path')");

        DataValue? value = await command.ExecuteScalarAsync();

        Assert.NotNull(value);
        Assert.Equal("public, system", value!.Value.AsString());
    }

    [Fact]
    public async Task CurrentSetting_UnknownParameter_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "SELECT current_setting('bogus_setting')");

        // The evaluator wraps in-function failures with position context;
        // the SessionSettingException travels as the inner exception.
        ExpressionEvaluationException ex = await Assert.ThrowsAsync<ExpressionEvaluationException>(
            () => command.ExecuteScalarAsync());

        Assert.Contains("bogus_setting", ex.Message);
        Assert.IsType<SessionSettingException>(ex.InnerException);
    }
}

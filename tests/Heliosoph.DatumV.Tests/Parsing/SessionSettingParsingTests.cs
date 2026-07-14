using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Parsing;

/// <summary>
/// Parser tests for session-setting statements:
/// <list type="bullet">
///   <item><description><c>SET TIME ZONE value</c> and the
///     configuration-parameter spellings <c>SET timezone = value</c> /
///     <c>SET timezone TO value</c> / <c>SET time_zone …</c>.</description></item>
///   <item><description><c>SHOW name</c> / <c>SHOW TIME ZONE</c> with
///     name canonicalization.</description></item>
///   <item><description>Non-interference with the procedural
///     <c>SET var = expr</c> and <c>SET search_path</c> forms.</description></item>
/// </list>
/// </summary>
public class SessionSettingParsingTests : ServiceTestBase
{
    // ───────────────────── SET TIME ZONE keyword form ─────────────────────

    [Fact]
    public void SetTimeZone_KeywordForm_StringLiteral()
    {
        Statement statement = SqlParser.ParseStatement(
            "SET TIME ZONE 'America/New_York'");

        SetTimeZoneStatement set = Assert.IsType<SetTimeZoneStatement>(statement);
        Assert.Equal("America/New_York", set.TimeZoneName);
    }

    [Fact]
    public void SetTimeZone_KeywordForm_BareIdentifier()
    {
        Statement statement = SqlParser.ParseStatement("SET TIME ZONE UTC");

        SetTimeZoneStatement set = Assert.IsType<SetTimeZoneStatement>(statement);
        Assert.Equal("UTC", set.TimeZoneName);
    }

    [Fact]
    public void SetTimeZone_Default_YieldsNullName()
    {
        Statement statement = SqlParser.ParseStatement("SET TIME ZONE DEFAULT");

        SetTimeZoneStatement set = Assert.IsType<SetTimeZoneStatement>(statement);
        Assert.Null(set.TimeZoneName);
    }

    [Fact]
    public void SetTimeZone_Local_YieldsNullName()
    {
        Statement statement = SqlParser.ParseStatement("SET TIME ZONE LOCAL");

        SetTimeZoneStatement set = Assert.IsType<SetTimeZoneStatement>(statement);
        Assert.Null(set.TimeZoneName);
    }

    // ───────────────────── SET timezone = / TO forms ─────────────────────

    [Theory]
    [InlineData("SET timezone = 'UTC'")]
    [InlineData("SET timezone TO 'UTC'")]
    [InlineData("SET time_zone = 'UTC'")]
    [InlineData("SET time_zone TO 'UTC'")]
    [InlineData("SET TimeZone TO 'UTC'")]
    public void SetTimeZone_ParameterForm_AllSpellings(string sql)
    {
        Statement statement = SqlParser.ParseStatement(sql);

        SetTimeZoneStatement set = Assert.IsType<SetTimeZoneStatement>(statement);
        Assert.Equal("UTC", set.TimeZoneName);
    }

    // ───────────────────── SHOW ─────────────────────

    [Theory]
    [InlineData("SHOW timezone")]
    [InlineData("SHOW TIME ZONE")]
    [InlineData("SHOW time_zone")]
    [InlineData("SHOW TimeZone")]
    public void Show_TimeZone_AllSpellings_Canonicalize(string sql)
    {
        Statement statement = SqlParser.ParseStatement(sql);

        ShowStatement show = Assert.IsType<ShowStatement>(statement);
        Assert.Equal("timezone", show.SettingName);
    }

    [Fact]
    public void Show_SearchPath()
    {
        Statement statement = SqlParser.ParseStatement("SHOW search_path");

        ShowStatement show = Assert.IsType<ShowStatement>(statement);
        Assert.Equal("search_path", show.SettingName);
    }

    [Fact]
    public void Show_UnknownName_ParsesForPlanTimeRejection()
    {
        // The parser accepts any identifier; unknown names are rejected at
        // plan time so the error can name the parameter.
        Statement statement = SqlParser.ParseStatement("SHOW bogus_setting");

        ShowStatement show = Assert.IsType<ShowStatement>(statement);
        Assert.Equal("bogus_setting", show.SettingName);
    }

    // ───────────────────── Non-interference ─────────────────────

    [Fact]
    public void ProceduralSet_StillParsesAsVariableAssignment()
    {
        Statement statement = SqlParser.ParseStatement("SET counter = 5");

        SetStatement set = Assert.IsType<SetStatement>(statement);
        Assert.Equal("counter", set.VariableName);
    }

    [Fact]
    public void SetSearchPath_StillParses()
    {
        Statement statement = SqlParser.ParseStatement("SET search_path = myapp, public");

        SetSearchPathStatement set = Assert.IsType<SetSearchPathStatement>(statement);
        Assert.Equal(new[] { "myapp", "public" }, set.Schemas);
    }
}

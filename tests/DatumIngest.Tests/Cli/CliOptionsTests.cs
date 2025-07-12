namespace DatumQuery.Tests.Cli;

using DatumQuery.Cli;

public sealed class CliOptionsTests
{
    [Fact]
    public void Parse_QueryWithSource_ParsesCorrectly()
    {
        string[] args = ["query", "SELECT * FROM data", "--source", "csv:data=/path/to/data.csv"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal("query", options.Command);
        Assert.Equal("SELECT * FROM data", options.Sql);
        Assert.Single(options.Sources);
        Assert.Equal("csv:data=/path/to/data.csv", options.Sources[0]);
    }

    [Fact]
    public void Parse_ExploreWithLimit_ParsesCorrectly()
    {
        string[] args = ["explore", "SELECT * FROM data", "--source", "csv:data=data.csv", "--limit", "20"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal("explore", options.Command);
        Assert.Equal(20, options.Limit);
    }

    [Fact]
    public void Parse_WithCatalog_ParsesCorrectly()
    {
        string[] args = ["stats", "SELECT * FROM data", "--catalog", "catalog.json"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal("stats", options.Command);
        Assert.Equal("catalog.json", options.CatalogPath);
    }

    [Fact]
    public void Parse_MultipleSources_CollectsAll()
    {
        string[] args = [
            "query", "SELECT * FROM a JOIN b",
            "--source", "csv:a=a.csv",
            "--source", "json:b=b.json"
        ];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal(2, options.Sources.Count);
        Assert.Equal("csv:a=a.csv", options.Sources[0]);
        Assert.Equal("json:b=b.json", options.Sources[1]);
    }

    [Fact]
    public void Parse_CatalogAndSource_BothParsed()
    {
        string[] args = [
            "query", "SELECT * FROM data",
            "--catalog", "catalog.json",
            "--source", "csv:extra=extra.csv"
        ];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal("catalog.json", options.CatalogPath);
        Assert.Single(options.Sources);
    }

    [Fact]
    public void Parse_TooFewArgs_Throws()
    {
        string[] args = ["query"];

        Assert.Throws<ArgumentException>(() => CliOptions.Parse(args));
    }

    [Fact]
    public void Parse_NoCatalogOrSource_Throws()
    {
        string[] args = ["query", "SELECT * FROM data"];

        Assert.Throws<ArgumentException>(() => CliOptions.Parse(args));
    }

    [Fact]
    public void Parse_UnknownArgument_Throws()
    {
        string[] args = ["query", "SELECT * FROM data", "--source", "csv:a=a.csv", "--unknown"];

        Assert.Throws<ArgumentException>(() => CliOptions.Parse(args));
    }

    [Fact]
    public void Parse_CatalogWithoutPath_Throws()
    {
        string[] args = ["query", "SELECT * FROM data", "--catalog"];

        Assert.Throws<ArgumentException>(() => CliOptions.Parse(args));
    }

    [Fact]
    public void Parse_SourceWithoutDefinition_Throws()
    {
        string[] args = ["query", "SELECT * FROM data", "--source"];

        Assert.Throws<ArgumentException>(() => CliOptions.Parse(args));
    }

    [Fact]
    public void Parse_LimitWithoutNumber_Throws()
    {
        string[] args = ["query", "SELECT * FROM data", "--source", "csv:a=a.csv", "--limit"];

        Assert.Throws<ArgumentException>(() => CliOptions.Parse(args));
    }

    [Fact]
    public void Parse_DefaultLimit_IsTen()
    {
        string[] args = ["explore", "SELECT * FROM data", "--source", "csv:a=a.csv"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal(10, options.Limit);
    }

    [Fact]
    public void Parse_CaseInsensitiveCommand()
    {
        string[] args = ["QUERY", "SELECT * FROM data", "--source", "csv:a=a.csv"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal("query", options.Command);
    }

    [Fact]
    public void Parse_SourceWithOptions_ParsedAsRawString()
    {
        string[] args = ["query", "SELECT * FROM data", "--source", "csv:data=/path/file.csv;delimiter=;;header=true"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal("csv:data=/path/file.csv;delimiter=;;header=true", options.Sources[0]);
    }
}

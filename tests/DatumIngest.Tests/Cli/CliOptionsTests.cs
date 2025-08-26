namespace DatumIngest.Tests.Cli;

using DatumIngest.Cli;

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

    [Fact]
    public void Parse_IndexColumns_ParsesCommaSeparated()
    {
        string[] args = ["index", "--source", "csv:data=data.csv", "--index-columns", "id,name,value"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal(3, options.IndexColumns.Count);
        Assert.Contains("id", options.IndexColumns);
        Assert.Contains("name", options.IndexColumns);
        Assert.Contains("value", options.IndexColumns);
    }

    [Fact]
    public void Parse_IndexColumns_TrimsWhitespace()
    {
        string[] args = ["index", "--source", "csv:data=data.csv", "--index-columns", " id , name "];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal(2, options.IndexColumns.Count);
        Assert.Contains("id", options.IndexColumns);
        Assert.Contains("name", options.IndexColumns);
    }

    [Fact]
    public void Parse_IndexColumns_DefaultEmpty()
    {
        string[] args = ["index", "--source", "csv:data=data.csv"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Empty(options.IndexColumns);
    }

    [Fact]
    public void Parse_BothBloomAndIndexColumns_ParsedIndependently()
    {
        string[] args = ["index", "--source", "csv:data=data.csv",
            "--bloom-columns", "category",
            "--index-columns", "id"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Single(options.BloomColumns);
        Assert.Contains("category", options.BloomColumns);
        Assert.Single(options.IndexColumns);
        Assert.Contains("id", options.IndexColumns);
    }

    [Fact]
    public void Parse_IndexWithCatalogOnly_ParsesCorrectly()
    {
        string[] args = ["index", "--catalog", "catalog.json"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal("index", options.Command);
        Assert.Equal("catalog.json", options.CatalogPath);
        Assert.Empty(options.Sources);
    }

    [Fact]
    public void Parse_IndexWithCatalogAndOptions_ParsesCorrectly()
    {
        string[] args = ["index", "--catalog", "catalog.json",
            "--chunk-size", "5000",
            "--bloom-columns", "label",
            "--index-columns", "id"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal("index", options.Command);
        Assert.Equal("catalog.json", options.CatalogPath);
        Assert.Equal(5000, options.ChunkSize);
        Assert.Contains("label", options.BloomColumns);
        Assert.Contains("id", options.IndexColumns);
    }

    [Fact]
    public void Parse_IndexManifest_NoSqlRequired()
    {
        string[] args = ["index-manifest", "--source", "csv:data=data.csv"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal("index-manifest", options.Command);
        Assert.Equal("", options.Sql);
        Assert.Single(options.Sources);
    }

    [Fact]
    public void Parse_IndexManifest_WithInteractions()
    {
        string[] args = ["index-manifest", "--source", "csv:data=data.csv", "--with-interactions"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal("index-manifest", options.Command);
        Assert.True(options.WithInteractions);
    }

    [Fact]
    public void Parse_IndexManifest_WithoutInteractions_DefaultsFalse()
    {
        string[] args = ["index-manifest", "--source", "csv:data=data.csv"];

        CliOptions options = CliOptions.Parse(args);

        Assert.False(options.WithInteractions);
    }

    [Fact]
    public void Parse_IndexManifest_WithOutput()
    {
        string[] args = ["index-manifest", "--source", "csv:data=data.csv", "--output", "custom.json"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal("custom.json", options.OutputPath);
    }

    [Fact]
    public void Parse_IndexManifest_AcceptsAllIndexFlags()
    {
        string[] args = [
            "index-manifest", "--source", "csv:data=data.csv",
            "--chunk-size", "2000",
            "--bloom-columns", "category",
            "--index-columns", "id",
            "--with-interactions",
            "--output", "manifest.json"
        ];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal("index-manifest", options.Command);
        Assert.Equal(2000, options.ChunkSize);
        Assert.Contains("category", options.BloomColumns);
        Assert.Contains("id", options.IndexColumns);
        Assert.True(options.WithInteractions);
        Assert.Equal("manifest.json", options.OutputPath);
    }

    [Fact]
    public void Parse_BloomAll_ParsedCorrectly()
    {
        string[] args = ["index", "--source", "csv:data=data.csv", "--bloom-all"];

        CliOptions options = CliOptions.Parse(args);

        Assert.True(options.BloomAllColumns);
        Assert.Empty(options.BloomColumns);
    }

    [Fact]
    public void Parse_IndexAll_ParsedCorrectly()
    {
        string[] args = ["index", "--source", "csv:data=data.csv", "--index-all"];

        CliOptions options = CliOptions.Parse(args);

        Assert.True(options.IndexAllColumns);
        Assert.Empty(options.IndexColumns);
    }

    [Fact]
    public void Parse_BloomAll_DefaultsFalse()
    {
        string[] args = ["index", "--source", "csv:data=data.csv"];

        CliOptions options = CliOptions.Parse(args);

        Assert.False(options.BloomAllColumns);
        Assert.False(options.IndexAllColumns);
    }

    [Fact]
    public void Parse_BloomAllAndIndexAll_BothParsed()
    {
        string[] args = ["index", "--source", "csv:data=data.csv", "--bloom-all", "--index-all"];

        CliOptions options = CliOptions.Parse(args);

        Assert.True(options.BloomAllColumns);
        Assert.True(options.IndexAllColumns);
    }

    [Fact]
    public void Parse_MemoryBudgetMegabytes_ParsesCorrectly()
    {
        string[] args = ["query", "SELECT 1", "--source", "csv:a=a.csv", "--memory-budget", "512MB"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal(512L * 1024 * 1024, options.MemoryBudgetBytes);
    }

    [Fact]
    public void Parse_MemoryBudgetGigabytes_ParsesCorrectly()
    {
        string[] args = ["query", "SELECT 1", "--source", "csv:a=a.csv", "--memory-budget", "4GB"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal(4L * 1024 * 1024 * 1024, options.MemoryBudgetBytes);
    }

    [Fact]
    public void Parse_MemoryBudgetRawBytes_ParsesCorrectly()
    {
        string[] args = ["query", "SELECT 1", "--source", "csv:a=a.csv", "--memory-budget", "1073741824"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Equal(1073741824L, options.MemoryBudgetBytes);
    }

    [Fact]
    public void Parse_MemoryBudgetZero_DisablesBudget()
    {
        string[] args = ["query", "SELECT 1", "--source", "csv:a=a.csv", "--memory-budget", "0"];

        CliOptions options = CliOptions.Parse(args);

        Assert.Null(options.MemoryBudgetBytes);
    }

    [Fact]
    public void Parse_MemoryBudgetMissingValue_Throws()
    {
        string[] args = ["query", "SELECT 1", "--source", "csv:a=a.csv", "--memory-budget"];

        Assert.Throws<ArgumentException>(() => CliOptions.Parse(args));
    }
}

using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Export;

/// <summary>
/// Parser-level tests for the <c>COPY (query) TO 'path' (...)</c> statement.
/// Covers the option-block shape (identifier / string / number values), the
/// optional WITH keyword, and the parenthesised source query — distinct from
/// the end-to-end plan + execute round-trip in
/// <see cref="ParquetExportSinkTests"/>.
/// </summary>
public sealed class CopyStatementParserTests
{
    [Fact]
    public void Parses_BareCopyToParquet_NoOptionBlock()
    {
        // The trailing option block is optional — bare form mirrors DuckDB
        // and Postgres's `COPY (q) TO 'p'` shape and lets format inference
        // from the path extension carry the load.
        Statement stmt = SqlParser.ParseStatement(
            "COPY (SELECT 1 AS x) TO 'out.parquet'");

        CopyStatement copy = Assert.IsType<CopyStatement>(stmt);
        Assert.Equal("out.parquet", copy.TargetPath);
        Assert.Empty(copy.Options);
        Assert.IsType<SelectQueryExpression>(copy.Source);
    }

    [Fact]
    public void Throws_OnEmptyOptionBlock()
    {
        // An empty (...) block is rejected — one canonical form for "no
        // options" (omit the block entirely). Anything between the parens
        // must be a real option.
        Assert.Throws<ParseException>(() => SqlParser.ParseStatement(
            "COPY (SELECT 1 AS x) TO 'out.parquet' ()"));
    }

    [Fact]
    public void Parses_FormatOptionAsIdentifier()
    {
        Statement stmt = SqlParser.ParseStatement(
            "COPY (SELECT 1 AS x) TO 'out.parquet' (FORMAT parquet)");

        CopyStatement copy = Assert.IsType<CopyStatement>(stmt);
        CopyOption format = Assert.Single(copy.Options);
        Assert.Equal("FORMAT", format.Key, ignoreCase: true);
        LiteralExpression value = Assert.IsType<LiteralExpression>(format.Value);
        Assert.Equal("parquet", value.Value);
    }

    [Fact]
    public void Parses_OptionalWithKeyword()
    {
        Statement stmt = SqlParser.ParseStatement(
            "COPY (SELECT 1) TO 'out.parquet' WITH (FORMAT parquet)");

        CopyStatement copy = Assert.IsType<CopyStatement>(stmt);
        Assert.Single(copy.Options);
    }

    [Fact]
    public void Parses_NumericAndStringOptions()
    {
        Statement stmt = SqlParser.ParseStatement(
            "COPY (SELECT 1) TO 'out.parquet' " +
            "(FORMAT parquet, ROW_GROUP_SIZE 10000, COMPRESSION 'zstd')");

        CopyStatement copy = Assert.IsType<CopyStatement>(stmt);
        Assert.Equal(3, copy.Options.Count);

        LiteralExpression rowGroup = Assert.IsType<LiteralExpression>(
            copy.Options.Single(o => string.Equals(o.Key, "ROW_GROUP_SIZE", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.Equal(10000L, rowGroup.Value);

        LiteralExpression compression = Assert.IsType<LiteralExpression>(
            copy.Options.Single(o => string.Equals(o.Key, "COMPRESSION", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.Equal("zstd", compression.Value);
    }

    [Fact]
    public void Throws_OnMissingTargetPath()
    {
        // Path is required even when the option block is omitted.
        Assert.Throws<ParseException>(() => SqlParser.ParseStatement(
            "COPY (SELECT 1) TO"));
    }
}

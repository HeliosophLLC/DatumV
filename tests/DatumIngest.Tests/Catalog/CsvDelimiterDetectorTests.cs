using DatumIngest.Catalog.Providers;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <see cref="CsvDelimiterDetector"/> content-based delimiter sniffing.
/// </summary>
public sealed class CsvDelimiterDetectorTests
{
    // ───────────────────── Comma ─────────────────────

    [Fact]
    public void DetectFromLines_CommaDelimited_ReturnsComma()
    {
        string[] lines =
        [
            "name,age,score",
            "Alice,30,95.5",
            "Bob,25,87.3",
        ];

        char result = CsvDelimiterDetector.DetectFromLines(lines);

        Assert.Equal(',', result);
    }

    // ───────────────────── Semicolon ─────────────────────

    [Fact]
    public void DetectFromLines_SemicolonDelimited_ReturnsSemicolon()
    {
        string[] lines =
        [
            "id;value;label",
            "1;100.0;cat",
            "2;200.5;dog",
        ];

        char result = CsvDelimiterDetector.DetectFromLines(lines);

        Assert.Equal(';', result);
    }

    // ───────────────────── Tab ─────────────────────

    [Fact]
    public void DetectFromLines_TabDelimited_ReturnsTab()
    {
        string[] lines =
        [
            "col_a\tcol_b\tcol_c",
            "1\t2\t3",
            "4\t5\t6",
        ];

        char result = CsvDelimiterDetector.DetectFromLines(lines);

        Assert.Equal('\t', result);
    }

    // ───────────────────── Pipe ─────────────────────

    [Fact]
    public void DetectFromLines_PipeDelimited_ReturnsPipe()
    {
        string[] lines =
        [
            "a|b|c",
            "1|2|3",
            "4|5|6",
        ];

        char result = CsvDelimiterDetector.DetectFromLines(lines);

        Assert.Equal('|', result);
    }

    // ───────────────────── Edge cases ─────────────────────

    [Fact]
    public void DetectFromLines_EmptyInput_FallsBackToComma()
    {
        char result = CsvDelimiterDetector.DetectFromLines([]);

        Assert.Equal(',', result);
    }

    [Fact]
    public void DetectFromLines_SingleColumnFile_FallsBackToComma()
    {
        string[] lines =
        [
            "only_column",
            "value1",
            "value2",
        ];

        char result = CsvDelimiterDetector.DetectFromLines(lines);

        Assert.Equal(',', result);
    }

    [Fact]
    public void DetectFromLines_InconsistentDelimiter_SkipsCandidate()
    {
        // Semicolons are inconsistent (3 fields then 2 fields),
        // but commas are consistent (2 fields each).
        string[] lines =
        [
            "a;b;c,d",
            "1;2,3",
            "4;5,6",
        ];

        char result = CsvDelimiterDetector.DetectFromLines(lines);

        Assert.Equal(',', result);
    }

    [Fact]
    public void DetectFromLines_QuotedFieldsContainingDelimiter_NotDoubleCounted()
    {
        // The comma inside the quoted field should not inflate the count.
        string[] lines =
        [
            "name;description;value",
            "a;\"has, commas\";10",
            "b;\"more, commas\";20",
        ];

        char result = CsvDelimiterDetector.DetectFromLines(lines);

        Assert.Equal(';', result);
    }

    [Fact]
    public void DetectFromLines_HeaderOnly_DetectsFromHeader()
    {
        string[] lines =
        [
            "a;b;c",
        ];

        char result = CsvDelimiterDetector.DetectFromLines(lines);

        Assert.Equal(';', result);
    }

    [Fact]
    public void DetectFromLines_CommaPriorityOverSemicolon_WhenTied()
    {
        // Both comma and semicolon produce exactly 2 fields — comma wins by priority.
        string[] lines =
        [
            "a,b;c",
            "1,2;3",
        ];

        char result = CsvDelimiterDetector.DetectFromLines(lines);

        // Comma: 2 fields. Semicolon: 2 fields. Comma comes first in priority.
        Assert.Equal(',', result);
    }
}

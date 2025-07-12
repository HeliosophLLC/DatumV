namespace DatumIngest.Tests.LanguageServer;

using DatumIngest.LanguageServer;

/// <summary>
/// Tests for <see cref="CompletionContext"/> — cursor position classification
/// within SQL fragments for determining completion zones.
/// </summary>
public sealed class CompletionContextTests
{
    // ───────────────────── Empty / null input ─────────────────────

    [Fact]
    public void Classify_EmptyString_ReturnsStatementStart()
    {
        CompletionZone zone = CompletionContext.Classify("", 0);

        Assert.Equal(CompletionZoneKind.StatementStart, zone.Kind);
        Assert.Null(zone.Prefix);
    }

    [Fact]
    public void Classify_NullString_ReturnsStatementStart()
    {
        CompletionZone zone = CompletionContext.Classify(null!, 0);

        Assert.Equal(CompletionZoneKind.StatementStart, zone.Kind);
    }

    [Fact]
    public void Classify_ZeroCursorOffset_ReturnsStatementStart()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT x FROM t", 0);

        Assert.Equal(CompletionZoneKind.StatementStart, zone.Kind);
    }

    // ───────────────────── SELECT zone ─────────────────────

    [Fact]
    public void Classify_AfterSelect_ReturnsAfterSelect()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT ", 7);

        Assert.Equal(CompletionZoneKind.AfterSelect, zone.Kind);
        Assert.Null(zone.Prefix);
    }

    [Fact]
    public void Classify_AfterSelectWithPartialIdentifier_HasPrefix()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT na", 9);

        Assert.Equal(CompletionZoneKind.AfterSelect, zone.Kind);
        Assert.Equal("na", zone.Prefix);
    }

    [Fact]
    public void Classify_AfterSelectComma_ReturnsAfterSelect()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT a, ", 10);

        Assert.Equal(CompletionZoneKind.AfterSelect, zone.Kind);
        Assert.Null(zone.Prefix);
    }

    // ───────────────────── FROM zone ─────────────────────

    [Fact]
    public void Classify_AfterFrom_ReturnsAfterFrom()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM ", 14);

        Assert.Equal(CompletionZoneKind.AfterFrom, zone.Kind);
        Assert.Null(zone.Prefix);
    }

    [Fact]
    public void Classify_AfterFromWithPrefix_HasPrefix()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM us", 16);

        Assert.Equal(CompletionZoneKind.AfterFrom, zone.Kind);
        Assert.Equal("us", zone.Prefix);
    }

    // ───────────────────── WHERE zone ─────────────────────

    [Fact]
    public void Classify_AfterWhere_ReturnsAfterWhere()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM t WHERE ", 22);

        Assert.Equal(CompletionZoneKind.AfterWhere, zone.Kind);
        Assert.Null(zone.Prefix);
    }

    [Fact]
    public void Classify_AfterWhereOperator_ReturnsAfterWhere()
    {
        // After "AND", the classifier keeps walking back to find the governing WHERE.
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM t WHERE x = 1 AND ", 32);

        Assert.Equal(CompletionZoneKind.AfterWhere, zone.Kind);
    }

    // ───────────────────── JOIN zone ─────────────────────

    [Fact]
    public void Classify_AfterJoin_ReturnsAfterJoin()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM a JOIN ", 21);

        Assert.Equal(CompletionZoneKind.AfterJoin, zone.Kind);
        Assert.Null(zone.Prefix);
    }

    // ───────────────────── ON zone ─────────────────────

    [Fact]
    public void Classify_AfterOn_ReturnsAfterOn()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM a JOIN b ON ", 26);

        Assert.Equal(CompletionZoneKind.AfterOn, zone.Kind);
        Assert.Null(zone.Prefix);
    }

    // ───────────────────── ORDER BY zone ─────────────────────

    [Fact]
    public void Classify_AfterOrderBy_ReturnsAfterOrderBy()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM t ORDER BY ", 25);

        Assert.Equal(CompletionZoneKind.AfterOrderBy, zone.Kind);
        Assert.Null(zone.Prefix);
    }

    // ───────────────────── INTO zone ─────────────────────

    [Fact]
    public void Classify_AfterInto_ReturnsAfterInto()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM t INTO ", 21);

        Assert.Equal(CompletionZoneKind.AfterInto, zone.Kind);
        Assert.Null(zone.Prefix);
    }

    // ───────────────────── AS zone ─────────────────────

    [Fact]
    public void Classify_AfterAs_ReturnsAfterAs()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT x AS ", 12);

        Assert.Equal(CompletionZoneKind.AfterAs, zone.Kind);
        Assert.Null(zone.Prefix);
    }

    // ───────────────────── Dot-qualified columns ─────────────────────

    [Fact]
    public void Classify_AfterDot_ReturnsAfterDotWithQualifier()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT t.", 9);

        Assert.Equal(CompletionZoneKind.AfterDot, zone.Kind);
        Assert.Equal("t", zone.TableQualifier);
        Assert.Null(zone.Prefix);
    }

    [Fact]
    public void Classify_AfterDotWithPrefix_ReturnsAfterDotWithQualifierAndPrefix()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT t.na", 11);

        Assert.Equal(CompletionZoneKind.AfterDot, zone.Kind);
        Assert.Equal("t", zone.TableQualifier);
        Assert.Equal("na", zone.Prefix);
    }

    // ───────────────────── Function arguments ─────────────────────

    [Fact]
    public void Classify_InsideFunctionCall_ReturnsInFunctionArguments()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT abs(", 11);

        Assert.Equal(CompletionZoneKind.InFunctionArguments, zone.Kind);
    }

    // ───────────────────── Prefix extraction ─────────────────────

    [Fact]
    public void Classify_WhitespaceBoundary_NullPrefix()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT ", 7);

        Assert.Null(zone.Prefix);
    }

    [Fact]
    public void Classify_CursorAtEndOfIdentifier_ExtractsPrefix()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT col", 10);

        Assert.Equal("col", zone.Prefix);
    }
}

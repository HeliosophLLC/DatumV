namespace DatumIngest.Tests.LanguageServer;

using DatumIngest.LanguageServer;

/// <summary>
/// Tests for <see cref="CompletionContext"/> — cursor position classification
/// within SQL fragments for determining completion zones.
/// </summary>
public sealed class CompletionContextTests : ServiceTestBase
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

    // ───────────────────── FROM source zone (table already specified) ─────────────────────

    [Fact]
    public void Classify_AfterFromTable_ReturnsAfterFromSource()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM users ", 20);

        Assert.Equal(CompletionZoneKind.AfterFromSource, zone.Kind);
        Assert.Null(zone.Prefix);
    }

    [Fact]
    public void Classify_AfterFromTableAlias_ReturnsAfterFromSource()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM users u ", 22);

        Assert.Equal(CompletionZoneKind.AfterFromSource, zone.Kind);
    }

    [Fact]
    public void Classify_AfterFromTableAsAlias_ReturnsAfterFromSource()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM users AS u ", 25);

        Assert.Equal(CompletionZoneKind.AfterFromSource, zone.Kind);
    }

    [Fact]
    public void Classify_AfterFromTableAs_ReturnsAfterAs()
    {
        // User is still typing the alias name — no completions.
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM users AS ", 23);

        Assert.Equal(CompletionZoneKind.AfterAs, zone.Kind);
    }

    [Fact]
    public void Classify_AfterFromSubquery_ReturnsAfterFromSource()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM (SELECT 1) ", 25);

        Assert.Equal(CompletionZoneKind.AfterFromSource, zone.Kind);
    }

    [Fact]
    public void Classify_AfterFromTableWithPrefix_ReturnsAfterFromSource()
    {
        // "FROM users W" — prefix "W" is being typed, walk skips it, hits "users" → passedContent.
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM users W", 21);

        Assert.Equal(CompletionZoneKind.AfterFromSource, zone.Kind);
        Assert.Equal("W", zone.Prefix);
    }

    // ───────────────────── JOIN source zone ─────────────────────

    [Fact]
    public void Classify_AfterJoinTable_ReturnsAfterJoinSource()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM a JOIN b ", 23);

        Assert.Equal(CompletionZoneKind.AfterJoinSource, zone.Kind);
    }

    [Fact]
    public void Classify_AfterJoinTableAsAlias_ReturnsAfterJoinSource()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM a JOIN b AS x ", 28);

        Assert.Equal(CompletionZoneKind.AfterJoinSource, zone.Kind);
    }

    [Fact]
    public void Classify_AfterLeftJoin_ReturnsAfterJoin()
    {
        // Just typed "LEFT JOIN " — need a table name.
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM a LEFT JOIN ", 26);

        Assert.Equal(CompletionZoneKind.AfterJoin, zone.Kind);
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

    [Fact]
    public void Classify_InsideExtract_ReturnsInsideExtract()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT EXTRACT(", 15);

        Assert.Equal(CompletionZoneKind.InsideExtract, zone.Kind);
    }

    [Fact]
    public void Classify_InsideExtract_WithPrefix_ReturnsInsideExtract()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT EXTRACT(YE", 18);

        Assert.Equal(CompletionZoneKind.InsideExtract, zone.Kind);
        Assert.Equal("YE", zone.Prefix);
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

    // ───────────────────── DDL zones ─────────────────────

    [Fact]
    public void Classify_AfterCreate_ReturnsAfterCreate()
    {
        CompletionZone zone = CompletionContext.Classify("CREATE ", 7);

        Assert.Equal(CompletionZoneKind.AfterCreate, zone.Kind);
    }

    [Fact]
    public void Classify_AfterDrop_ReturnsAfterDrop()
    {
        CompletionZone zone = CompletionContext.Classify("DROP ", 5);

        Assert.Equal(CompletionZoneKind.AfterDrop, zone.Kind);
    }

    [Fact]
    public void Classify_InsideCreateTableColumnList_ReturnsAfterCreateTableColumns()
    {
        CompletionZone zone = CompletionContext.Classify("CREATE TABLE #temp (col1 ", 25);

        Assert.Equal(CompletionZoneKind.AfterCreateTableColumns, zone.Kind);
    }

    [Fact]
    public void Classify_InsideCreateTempTableColumnList_ReturnsAfterCreateTableColumns()
    {
        CompletionZone zone = CompletionContext.Classify("CREATE TEMP TABLE #temp (", 25);

        Assert.Equal(CompletionZoneKind.AfterCreateTableColumns, zone.Kind);
    }

    [Fact]
    public void Classify_AfterAlterTable_ReturnsAfterAlterTable()
    {
        CompletionZone zone = CompletionContext.Classify("ALTER TABLE ", 12);

        Assert.Equal(CompletionZoneKind.AfterAlterTable, zone.Kind);
    }

    // ───────────────────── DML zones ─────────────────────

    [Fact]
    public void Classify_AfterInsertInto_ReturnsAfterInsertInto()
    {
        CompletionZone zone = CompletionContext.Classify("INSERT INTO ", 12);

        Assert.Equal(CompletionZoneKind.AfterInsertInto, zone.Kind);
    }

    [Fact]
    public void Classify_InsideInsertColumnList_ReturnsAfterInsertTable()
    {
        CompletionZone zone = CompletionContext.Classify("INSERT INTO #temp (", 19);

        Assert.Equal(CompletionZoneKind.AfterInsertTable, zone.Kind);
    }

    [Fact]
    public void Classify_AfterUpdate_ReturnsAfterUpdate()
    {
        CompletionZone zone = CompletionContext.Classify("UPDATE ", 7);

        Assert.Equal(CompletionZoneKind.AfterUpdate, zone.Kind);
    }

    [Fact]
    public void Classify_AfterUpdateSet_ReturnsAfterUpdateSet()
    {
        CompletionZone zone = CompletionContext.Classify("UPDATE #temp SET ", 17);

        Assert.Equal(CompletionZoneKind.AfterUpdateSet, zone.Kind);
    }

    [Fact]
    public void Classify_AfterDeleteFrom_ReturnsAfterDeleteFrom()
    {
        CompletionZone zone = CompletionContext.Classify("DELETE FROM ", 12);

        Assert.Equal(CompletionZoneKind.AfterDeleteFrom, zone.Kind);
    }

    // ───────────────────── TABLESAMPLE contextual zones ─────────────────────

    [Fact]
    public void Classify_AfterTablesample_ReturnsAfterTablesample()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM t TABLESAMPLE ", 28);

        Assert.Equal(CompletionZoneKind.AfterTablesample, zone.Kind);
    }

    [Fact]
    public void Classify_AfterTablesample_WithPrefix_ReturnsAfterTablesample()
    {
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM t TABLESAMPLE STR", 31);

        Assert.Equal(CompletionZoneKind.AfterTablesample, zone.Kind);
        Assert.Equal("STR", zone.Prefix);
    }

    [Fact]
    public void Classify_AfterTablesampleMethodArg_ReturnsAfterTablesampleMethodArg()
    {
        CompletionZone zone = CompletionContext.Classify(
            "SELECT * FROM t TABLESAMPLE STRATIFIED(10) ", 44);

        Assert.Equal(CompletionZoneKind.AfterTablesampleMethodArg, zone.Kind);
    }

    [Fact]
    public void Classify_AfterTablesampleBernoulliArg_ReturnsAfterTablesampleMethodArg()
    {
        CompletionZone zone = CompletionContext.Classify(
            "SELECT * FROM t TABLESAMPLE BERNOULLI(50) ", 43);

        Assert.Equal(CompletionZoneKind.AfterTablesampleMethodArg, zone.Kind);
    }

    [Fact]
    public void Classify_InsideTablesampleArgs_ReturnsInsideTablesampleArg()
    {
        CompletionZone zone = CompletionContext.Classify(
            "SELECT * FROM t TABLESAMPLE BERNOULLI(", 38);

        Assert.Equal(CompletionZoneKind.InsideTablesampleArg, zone.Kind);
    }

    [Fact]
    public void Classify_InsideTablesampleStratifiedArgs_ReturnsInsideTablesampleArg()
    {
        CompletionZone zone = CompletionContext.Classify(
            "SELECT * FROM \"customers_csv\" TABLESAMPLE STRATIFIED(", 53);

        Assert.Equal(CompletionZoneKind.InsideTablesampleArg, zone.Kind);
    }

    // ───────────────────── Inside string / comment ─────────────────────

    [Fact]
    public void Classify_InsideUnclosedSingleQuote_ReturnsInsideStringOrComment()
    {
        // Cursor at offset 24, just after "al" inside an unclosed string.
        string sql = "SELECT * FROM t WHERE n = 'al";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.InsideStringOrComment, zone.Kind);
    }

    [Fact]
    public void Classify_AfterClosedSingleQuote_DoesNotReturnInsideStringOrComment()
    {
        // After the closing quote we're back in expression context.
        string sql = "SELECT * FROM t WHERE n = 'al' ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.NotEqual(CompletionZoneKind.InsideStringOrComment, zone.Kind);
    }

    [Fact]
    public void Classify_InsideEscapedSingleQuoteString_ReturnsInsideStringOrComment()
    {
        // 'it''s ho — '' is the SQL escape for a single quote inside a string,
        // so the string is still open at the cursor.
        string sql = "SELECT 'it''s ho";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.InsideStringOrComment, zone.Kind);
    }

    [Fact]
    public void Classify_InsideLineComment_ReturnsInsideStringOrComment()
    {
        string sql = "SELECT * -- pick the b";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.InsideStringOrComment, zone.Kind);
    }

    [Fact]
    public void Classify_AfterLineCommentNewline_DoesNotReturnInsideStringOrComment()
    {
        string sql = "SELECT * -- comment\nFROM ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.NotEqual(CompletionZoneKind.InsideStringOrComment, zone.Kind);
    }

    [Fact]
    public void Classify_InsideUnclosedBlockComment_ReturnsInsideStringOrComment()
    {
        string sql = "SELECT /* note ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.InsideStringOrComment, zone.Kind);
    }

    [Fact]
    public void Classify_AfterClosedBlockComment_DoesNotReturnInsideStringOrComment()
    {
        string sql = "SELECT /* note */ ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.NotEqual(CompletionZoneKind.InsideStringOrComment, zone.Kind);
    }

    // ───────────────────── Template strings (backticks) ─────────────────────

    [Fact]
    public void Classify_InsideTemplateBody_ReturnsInsideStringOrComment()
    {
        // Cursor sits inside the literal portion of a template — no completions.
        string sql = "SELECT `hello wor";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.InsideStringOrComment, zone.Kind);
    }

    [Fact]
    public void Classify_AfterClosedTemplate_DoesNotReturnInsideStringOrComment()
    {
        // After the closing backtick we're back in expression context.
        string sql = "SELECT `hello` ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.NotEqual(CompletionZoneKind.InsideStringOrComment, zone.Kind);
    }

    [Fact]
    public void Classify_InsideOpenSplice_OffersExpressionCompletions()
    {
        // Cursor sits inside ${…} — splice contents are expressions, so we
        // expect an expression-flavoured zone (AfterSelect surfaces columns
        // and scalar functions).
        string sql = "SELECT `hello ${";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.AfterSelect, zone.Kind);
        Assert.Null(zone.Prefix);
    }

    [Fact]
    public void Classify_InsideSpliceWithPartialIdentifier_HasPrefix()
    {
        string sql = "SELECT `hello ${na";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.AfterSelect, zone.Kind);
        Assert.Equal("na", zone.Prefix);
    }

    [Fact]
    public void Classify_AfterClosedSplice_BackInTemplateBody()
    {
        // Cursor sits in the literal text after a fully-closed splice.
        string sql = "SELECT `hello ${name} wor";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.InsideStringOrComment, zone.Kind);
    }

    [Fact]
    public void Classify_InsideSpliceWithNestedBraces_StillInSpliceContext()
    {
        // Splice contains a struct literal — cursor inside the struct should
        // still offer expression completions (we're nested inside the splice).
        string sql = "SELECT `x = ${ {a: 1, b: ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        // Expression-flavoured (column / function) completions, not InsideStringOrComment.
        Assert.NotEqual(CompletionZoneKind.InsideStringOrComment, zone.Kind);
    }

    [Fact]
    public void Classify_EscapedDollarInTemplate_DoesNotEnterSplice()
    {
        // \${ is an escape for a literal dollar — the cursor sits in the
        // template body, not in a splice.
        string sql = @"SELECT `literal \${name} wor";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.InsideStringOrComment, zone.Kind);
    }

    // ───────────────────── Procedural control flow ─────────────────────

    [Fact]
    public void Classify_AfterIfKeyword_ReturnsExpression()
    {
        // `IF |` — predicate position. Want columns / variables / functions.
        string sql = "IF ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.Expression, zone.Kind);
    }

    [Fact]
    public void Classify_AfterIfPredicateOperator_ReturnsExpression()
    {
        // `IF @x > |` — predicate continues; the trailing `>` says we need
        // another operand, not a body.
        string sql = "IF @x > ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.Expression, zone.Kind);
    }

    [Fact]
    public void Classify_AfterIfPredicateComplete_ReturnsStatementStart()
    {
        // `IF @x > 1 |` — predicate looks done (last token is value-like);
        // the body is what's expected next. StatementStart includes BEGIN
        // and the procedural statement keywords.
        string sql = "IF @x > 1 ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.StatementStart, zone.Kind);
    }

    [Fact]
    public void Classify_AfterWhilePredicateComplete_ReturnsStatementStart()
    {
        string sql = "WHILE @i < 10 ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.StatementStart, zone.Kind);
    }

    [Fact]
    public void Classify_AfterElse_ReturnsStatementStart()
    {
        string sql = "IF @x > 0 SET @y = 1 ELSE ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.StatementStart, zone.Kind);
    }

    [Fact]
    public void Classify_InsideBeginBlock_ReturnsStatementStart()
    {
        string sql = "BEGIN ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.StatementStart, zone.Kind);
    }

    [Fact]
    public void Classify_AfterTryKeyword_ReturnsStatementStart()
    {
        string sql = "TRY ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.StatementStart, zone.Kind);
    }

    [Fact]
    public void Classify_AfterPrintKeyword_ReturnsExpression()
    {
        // PRINT takes an expression — same context as the AfterSelect side
        // for column / variable / function suggestions.
        string sql = "PRINT ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.Expression, zone.Kind);
    }

    [Fact]
    public void Classify_AfterRaiseKeyword_ReturnsExpression()
    {
        string sql = "RAISE ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.Expression, zone.Kind);
    }
}

namespace Heliosoph.DatumV.Tests.LanguageServer;

using Heliosoph.DatumV.LanguageServer;

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
    public void Classify_AfterCreateOrReplace_ReturnsAfterCreate()
    {
        // `CREATE OR REPLACE |` should land in the same zone as bare CREATE
        // so the menu offers FUNCTION / PROCEDURE / MODEL / VIEW.
        CompletionZone zone = CompletionContext.Classify("CREATE OR REPLACE ", 18);

        Assert.Equal(CompletionZoneKind.AfterCreate, zone.Kind);
    }

    [Fact]
    public void AfterCreate_OffersView()
    {
        IReadOnlyList<string> keywords = KeywordRegistry.GetKeywords(CompletionZoneKind.AfterCreate);
        Assert.Contains("VIEW", keywords);
    }

    [Fact]
    public void AfterDrop_OffersView()
    {
        IReadOnlyList<string> keywords = KeywordRegistry.GetKeywords(CompletionZoneKind.AfterDrop);
        Assert.Contains("VIEW", keywords);
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

    [Fact]
    public void Classify_AfterAlterTableIfExists_RoutesToAfterAlterTable()
    {
        // `ALTER TABLE IF EXISTS |` — cursor wants a table name. Without
        // the IF-vs-procedural disambiguator the walk-back hits `SqlToken.If`
        // first and routes to ProceduralExpression (totally wrong here).
        CompletionZone zone = CompletionContext.Classify("ALTER TABLE IF EXISTS ", 22);

        Assert.Equal(CompletionZoneKind.AfterAlterTable, zone.Kind);
    }

    [Fact]
    public void Classify_AfterAlterTableIfExistsWithTableName_RoutesToAfterAlterTable()
    {
        // `ALTER TABLE IF EXISTS users |` — user has typed the table name;
        // now wants verb suggestions (ADD/DROP/ALTER). Same zone as above
        // — the provider supplies both table names and the verb keywords
        // for AfterAlterTable so both cursor positions are satisfied.
        CompletionZone zone = CompletionContext.Classify("ALTER TABLE IF EXISTS users ", 28);

        Assert.Equal(CompletionZoneKind.AfterAlterTable, zone.Kind);
    }

    [Fact]
    public void Classify_AfterAlterColumnName_OffersDropOrSet()
    {
        // `ALTER TABLE t ALTER COLUMN id |` — verb position: DROP / SET.
        // Without the passedContent split in the inner-ALTER detector,
        // this would route to AfterAlterTableAlter (which only offers
        // COLUMN — wrong, COLUMN is already typed).
        const string sql = "ALTER TABLE t ALTER COLUMN id ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.AfterAlterColumnName, zone.Kind);
    }

    [Fact]
    public void Classify_AfterAlterTableAlter_StillOffersColumn()
    {
        // Regression: `ALTER TABLE t ALTER |` (no content past inner ALTER)
        // must still route to AfterAlterTableAlter (offering COLUMN).
        const string sql = "ALTER TABLE t ALTER ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.AfterAlterTableAlter, zone.Kind);
    }

    [Fact]
    public void Classify_AfterCreateIndexColumnList_ReturnsAfterCreateIndexColumns()
    {
        // Cursor sits past the `)` of the column list; USING / WITH suffixes
        // are next.
        CompletionZone zone = CompletionContext.Classify("CREATE INDEX idx ON t (col) ", 28);

        Assert.Equal(CompletionZoneKind.AfterCreateIndexColumns, zone.Kind);
    }

    [Fact]
    public void Classify_AfterCreateUniqueIndexColumnList_ReturnsAfterCreateIndexColumns()
    {
        CompletionZone zone = CompletionContext.Classify("CREATE UNIQUE INDEX idx ON t (col) ", 35);

        Assert.Equal(CompletionZoneKind.AfterCreateIndexColumns, zone.Kind);
    }

    [Fact]
    public void Classify_BeforeCreateIndexColumnList_DoesNotReturnAfterCreateIndexColumns()
    {
        // Cursor hasn't crossed the column list yet (`CREATE INDEX |`) —
        // should fall through to AfterCreate so the user sees TABLE / INDEX /
        // FUNCTION / etc. suggestions, not USING / WITH.
        CompletionZone zone = CompletionContext.Classify("CREATE INDEX ", 13);

        Assert.NotEqual(CompletionZoneKind.AfterCreateIndexColumns, zone.Kind);
    }

    [Fact]
    public void Classify_DropIndex_DoesNotReturnAfterCreateIndexColumns()
    {
        // DROP INDEX has no column list; INDEX without a preceding paren
        // group must not trigger the new zone.
        CompletionZone zone = CompletionContext.Classify("DROP INDEX ", 11);

        Assert.NotEqual(CompletionZoneKind.AfterCreateIndexColumns, zone.Kind);
    }

    [Fact]
    public void Classify_AfterCreateIndexUsing_ReturnsAfterCreateIndexUsing()
    {
        // After `USING `, the user is picking an index method (FTS).
        CompletionZone zone = CompletionContext.Classify("CREATE INDEX idx ON t (col) USING ", 34);

        Assert.Equal(CompletionZoneKind.AfterCreateIndexUsing, zone.Kind);
    }

    [Fact]
    public void Classify_AfterUsingInQuery_DoesNotReturnAfterCreateIndexUsing()
    {
        // USING in JOIN context (`JOIN t USING (col)`) — not CREATE INDEX.
        // Cursor right after USING in a JOIN must not pick up the new zone.
        CompletionZone zone = CompletionContext.Classify("SELECT * FROM a JOIN b USING ", 29);

        Assert.NotEqual(CompletionZoneKind.AfterCreateIndexUsing, zone.Kind);
    }

    [Fact]
    public void Classify_InsideCreateIndexWithParens_ReturnsInsideCreateIndexWithOptions()
    {
        // Cursor sits inside the WITH option list.
        CompletionZone zone = CompletionContext.Classify(
            "CREATE INDEX idx ON t (col) USING fts WITH (", 44);

        Assert.Equal(CompletionZoneKind.InsideCreateIndexWithOptions, zone.Kind);
    }

    [Fact]
    public void Classify_InsideWithCteParens_DoesNotReturnInsideCreateIndexWithOptions()
    {
        // CTE `WITH x AS (` — not CREATE INDEX. Must not pick up the new zone.
        CompletionZone zone = CompletionContext.Classify("WITH x AS (", 11);

        Assert.NotEqual(CompletionZoneKind.InsideCreateIndexWithOptions, zone.Kind);
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
    public void Classify_AfterIfKeyword_ReturnsProceduralExpression()
    {
        // `IF |` — predicate position. Procedural-expression zone (no row
        // context, so column names aren't offered — only vars and scalar
        // functions).
        string sql = "IF ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.ProceduralExpression, zone.Kind);
    }

    [Fact]
    public void Classify_AfterIfPredicateOperator_ReturnsProceduralExpression()
    {
        // `IF x > |` — predicate continues; the trailing `>` says we need
        // another operand, not a body. Still procedural-expression context.
        string sql = "IF x > ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.ProceduralExpression, zone.Kind);
    }

    [Fact]
    public void Classify_IfBPrefix_ReturnsProceduralExpressionWithPrefix()
    {
        // `IF b` — pinning the regression: typing a single-letter prefix
        // after IF must NOT land in the column-offering Expression zone,
        // because columns from `system_*` tables (e.g. `backend` on
        // `system_models`) would otherwise leak into a context where they
        // aren't legal.
        CompletionZone zone = CompletionContext.Classify("IF b", 4);

        Assert.Equal(CompletionZoneKind.ProceduralExpression, zone.Kind);
        Assert.Equal("b", zone.Prefix);
    }

    [Fact]
    public void Classify_AfterIfPredicateComplete_ReturnsStatementStart()
    {
        // `IF x > 1 |` — predicate looks done (last token is value-like);
        // the body is what's expected next. StatementStart includes BEGIN
        // and the procedural statement keywords.
        string sql = "IF x > 1 ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.StatementStart, zone.Kind);
    }

    [Fact]
    public void Classify_AfterWhilePredicateComplete_ReturnsStatementStart()
    {
        string sql = "WHILE i < 10 ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.StatementStart, zone.Kind);
    }

    [Fact]
    public void Classify_AfterElse_ReturnsStatementStart()
    {
        string sql = "IF x > 0 SET y = 1 ELSE ";
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
    public void Classify_AfterPrintKeyword_ReturnsProceduralExpression()
    {
        // PRINT takes an expression. Procedural — no row context, so column
        // names aren't offered.
        string sql = "PRINT ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.ProceduralExpression, zone.Kind);
    }

    [Fact]
    public void Classify_AfterRaiseKeyword_ReturnsProceduralExpression()
    {
        string sql = "RAISE ";
        CompletionZone zone = CompletionContext.Classify(sql, sql.Length);

        Assert.Equal(CompletionZoneKind.ProceduralExpression, zone.Kind);
    }

    [Fact]
    public void Classify_SingleLetterAtDocumentStart_ReturnsStatementStartWithPrefix()
    {
        // Pinning the empty-document edge case: typing "b" at the start of
        // a blank document should land in StatementStart with prefix="b".
        // Anything else (e.g. AfterSelect / Expression) would let column
        // names leak into the popup as the user is starting a fresh batch.
        CompletionZone zone = CompletionContext.Classify("b", 1);

        Assert.Equal(CompletionZoneKind.StatementStart, zone.Kind);
        Assert.Equal("b", zone.Prefix);
    }

    // ───────────────────── DECLARE type position ─────────────────────

    [Fact]
    public void Classify_AfterDeclareName_ReturnsAfterDeclareType()
    {
        // `DECLARE x ⌷` — type name expected.
        CompletionZone zone = CompletionContext.Classify("DECLARE x ", 11);

        Assert.Equal(CompletionZoneKind.AfterDeclareType, zone.Kind);
    }

    [Fact]
    public void Classify_AfterDeclareNameWithPartialType_ReturnsAfterDeclareTypeWithPrefix()
    {
        CompletionZone zone = CompletionContext.Classify("DECLARE x INT", 14);

        Assert.Equal(CompletionZoneKind.AfterDeclareType, zone.Kind);
        Assert.Equal("INT", zone.Prefix);
    }

    [Fact]
    public void Classify_AfterDeclareEquals_ReturnsProceduralExpression()
    {
        // Past `=` — initializer position, columns out of scope.
        CompletionZone zone = CompletionContext.Classify("DECLARE x INT32 = ", 19);

        Assert.Equal(CompletionZoneKind.ProceduralExpression, zone.Kind);
    }

    [Fact]
    public void Classify_RightAfterDeclareKeyword_SuppressesCompletions()
    {
        // User is naming the variable; nothing useful to suggest.
        CompletionZone zone = CompletionContext.Classify("DECLARE ", 8);

        Assert.Equal(CompletionZoneKind.AfterAs, zone.Kind);
    }

    [Fact]
    public void Classify_InsideCreateFunctionParamAfterVar_ReturnsAfterDeclareType()
    {
        CompletionZone zone = CompletionContext.Classify(
            "CREATE FUNCTION foo(x ", 23);

        Assert.Equal(CompletionZoneKind.AfterDeclareType, zone.Kind);
    }

    [Fact]
    public void Classify_InsideCreateProcedureParamAfterVar_ReturnsAfterDeclareType()
    {
        CompletionZone zone = CompletionContext.Classify(
            "CREATE PROCEDURE foo(x ", 24);

        Assert.Equal(CompletionZoneKind.AfterDeclareType, zone.Kind);
    }

    [Fact]
    public void Classify_InsideCreateFunctionAfterComma_SuppressesCompletions()
    {
        // After `, ` in a param list — the user is about to type a fresh
        // `var` name; we have nothing useful to suggest.
        CompletionZone zone = CompletionContext.Classify(
            "CREATE FUNCTION foo(x INT32, ", 30);

        Assert.Equal(CompletionZoneKind.AfterAs, zone.Kind);
    }

    [Fact]
    public void Classify_InsideCreateFunctionParamDefaultExpression_ReturnsProceduralExpression()
    {
        CompletionZone zone = CompletionContext.Classify(
            "CREATE FUNCTION foo(x INT32 = ", 31);

        Assert.Equal(CompletionZoneKind.ProceduralExpression, zone.Kind);
    }

    [Fact]
    public void Classify_InsideCastAs_ReturnsAfterDeclareType()
    {
        // CAST(x AS |) wants type completions, not the alias-suppression
        // path that bare AS uses elsewhere.
        CompletionZone zone = CompletionContext.Classify(
            "SELECT CAST(x AS ", 17);

        Assert.Equal(CompletionZoneKind.AfterDeclareType, zone.Kind);
    }

    [Fact]
    public void Classify_InsideCastAsWithPrefix_ReturnsAfterDeclareTypeWithPrefix()
    {
        CompletionZone zone = CompletionContext.Classify(
            "SELECT CAST(x AS Int", 20);

        Assert.Equal(CompletionZoneKind.AfterDeclareType, zone.Kind);
        Assert.Equal("Int", zone.Prefix);
    }

    [Fact]
    public void Classify_AfterReturns_ReturnsAfterDeclareType()
    {
        // CREATE FUNCTION foo(...) RETURNS | — type position.
        CompletionZone zone = CompletionContext.Classify(
            "CREATE FUNCTION foo(x INT32) RETURNS ", 38);

        Assert.Equal(CompletionZoneKind.AfterDeclareType, zone.Kind);
    }

    [Fact]
    public void Classify_AliasAfterFromTable_StillSuppressesCompletions()
    {
        // Regression check: the CAST-detection path must not catch
        // aliasing AS uses (FROM t AS u — no enclosing CAST paren).
        CompletionZone zone = CompletionContext.Classify(
            "SELECT * FROM t AS ", 19);

        Assert.Equal(CompletionZoneKind.AfterAs, zone.Kind);
    }

    // ───────────────────── Variables in scope ─────────────────────

    [Fact]
    public void Classify_AfterDeclare_ExposesVariableInScope()
    {
        CompletionZone zone = CompletionContext.Classify(
            "DECLARE xyz INT32 = 0\nIF ", 26);

        Assert.NotNull(zone.VariablesInScope);
        Assert.Contains("xyz", zone.VariablesInScope!);
    }

    [Fact]
    public void Classify_VariablePrefix_MatchesPartiallyTypedVar()
    {
        // `IF x` — prefix should be `x` so the popup filters to vars
        // starting with `x`. Variable kind tokens were previously
        // ineligible for prefix extraction.
        CompletionZone zone = CompletionContext.Classify(
            "DECLARE xyz INT32 = 0\nIF x", 28);

        Assert.Equal("x", zone.Prefix);
        Assert.Contains("xyz", zone.VariablesInScope!);
    }

    [Fact]
    public void Classify_BareIdentifierPrefix_OffersVariablesInProceduralExpression()
    {
        // Post-PG-alignment variables are bare identifiers. Typing a bare
        // identifier prefix inside a procedural expression context surfaces
        // in-scope variables alongside columns / built-ins; the prefix
        // filters the popup.
        CompletionZone zone = CompletionContext.Classify(
            "DECLARE xyz INT32 = 0\nIF x", 27);

        Assert.Equal("x", zone.Prefix);
        Assert.Equal(CompletionZoneKind.ProceduralExpression, zone.Kind);
        Assert.Contains("xyz", zone.VariablesInScope!);
    }

    [Fact]
    public void Classify_ForLoopVariable_ExposedInScope()
    {
        CompletionZone zone = CompletionContext.Classify(
            "FOR i = 1 TO 10 BEGIN PRINT ", 29);

        Assert.NotNull(zone.VariablesInScope);
        Assert.Contains("i", zone.VariablesInScope!);
    }

    [Fact]
    public void Classify_CatchErrorVariable_ExposedInScope()
    {
        CompletionZone zone = CompletionContext.Classify(
            "TRY SELECT 1 CATCH err PRINT ", 30);

        Assert.NotNull(zone.VariablesInScope);
        Assert.Contains("err", zone.VariablesInScope!);
    }

    [Fact]
    public void Classify_VariablesInScope_DeduplicatedAcrossDeclarations()
    {
        CompletionZone zone = CompletionContext.Classify(
            "DECLARE a INT32 = 0\nDECLARE b INT32 = 0\nDECLARE a INT32 = 1\nIF ", 65);

        // Even though a is "redeclared" in the source, the popup should
        // surface it once — duplicates are confusing in completion UI.
        Assert.Equal(2, zone.VariablesInScope!.Count(v => v == "a") +
                        zone.VariablesInScope!.Count(v => v == "b"));
        Assert.Single(zone.VariablesInScope!, v => v == "a");
    }

    // ───────────────────── RETURNING zone ─────────────────────

    [Fact]
    public void Classify_AfterReturning_Insert_ReturnsAfterReturning()
    {
        CompletionZone zone = CompletionContext.Classify(
            "INSERT INTO users (name) VALUES ('a') RETURNING ", 48);
        Assert.Equal(CompletionZoneKind.AfterReturning, zone.Kind);
    }

    [Fact]
    public void Classify_AfterReturning_Update_ReturnsAfterReturning()
    {
        CompletionZone zone = CompletionContext.Classify(
            "UPDATE users SET name = 'a' RETURNING ", 38);
        Assert.Equal(CompletionZoneKind.AfterReturning, zone.Kind);
    }

    [Fact]
    public void Classify_AfterReturning_Delete_ReturnsAfterReturning()
    {
        CompletionZone zone = CompletionContext.Classify(
            "DELETE FROM users RETURNING ", 28);
        Assert.Equal(CompletionZoneKind.AfterReturning, zone.Kind);
    }

    [Fact]
    public void Classify_AfterReturning_UpdateTargetInScope()
    {
        // Augmented ExtractTablesInScope picks up the UPDATE target so
        // RETURNING projections can complete columns of that table.
        CompletionZone zone = CompletionContext.Classify(
            "UPDATE users SET status = 'x' WHERE id = 1 RETURNING ", 53);

        Assert.Equal(CompletionZoneKind.AfterReturning, zone.Kind);
        Assert.NotNull(zone.TablesInScope);
        Assert.Contains("users", zone.TablesInScope!);
    }

    [Fact]
    public void Classify_AfterReturning_DeleteTargetInScope()
    {
        CompletionZone zone = CompletionContext.Classify(
            "DELETE FROM orders WHERE id = 1 RETURNING ", 42);

        Assert.Equal(CompletionZoneKind.AfterReturning, zone.Kind);
        Assert.NotNull(zone.TablesInScope);
        Assert.Contains("orders", zone.TablesInScope!);
    }

    [Fact]
    public void Classify_AfterReturning_InsertTargetInScope()
    {
        CompletionZone zone = CompletionContext.Classify(
            "INSERT INTO logs (msg) VALUES ('x') RETURNING ", 46);

        Assert.Equal(CompletionZoneKind.AfterReturning, zone.Kind);
        Assert.NotNull(zone.TablesInScope);
        Assert.Contains("logs", zone.TablesInScope!);
    }

    [Fact]
    public void Classify_AfterUpdateSet_OffersReturningKeyword()
    {
        // After `UPDATE t SET x = 1`, RETURNING should now be discoverable
        // alongside WHERE/FROM.
        CompletionZone zone = CompletionContext.Classify("UPDATE t SET x = 1 ", 19);

        Assert.Equal(CompletionZoneKind.AfterUpdateSet, zone.Kind);
    }
}

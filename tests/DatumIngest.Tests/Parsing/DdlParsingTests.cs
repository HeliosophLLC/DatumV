using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// Tests for DDL and DML statement parsing: CREATE TEMP TABLE, DROP TABLE,
/// INSERT INTO, UPDATE, ALTER TABLE, and multi-statement batch parsing.
/// </summary>
public class DdlParsingTests : ServiceTestBase
{
    // ───────────────────── CREATE TEMP TABLE ─────────────────────

    [Fact]
    public void CreateTempTableWithColumns()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE staging (id INT, name STRING, score FLOAT64)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Equal("staging", create.TableName);
        Assert.False(create.IfNotExists);
        Assert.Equal(3, create.Columns.Count);

        Assert.Equal("id", create.Columns[0].Name);
        Assert.Equal("INT", create.Columns[0].TypeName);
        Assert.True(create.Columns[0].Nullable);

        Assert.Equal("name", create.Columns[1].Name);
        Assert.Equal("STRING", create.Columns[1].TypeName);

        Assert.Equal("score", create.Columns[2].Name);
        Assert.Equal("FLOAT64", create.Columns[2].TypeName);
    }

    [Fact]
    public void CreateTempTableNotNullColumn()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE t (id INT NOT NULL, label STRING)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Equal(2, create.Columns.Count);
        Assert.False(create.Columns[0].Nullable);
        Assert.True(create.Columns[1].Nullable);
    }

    [Fact]
    public void CreateTemporaryTableSynonym()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMPORARY TABLE t (x INT)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Equal("t", create.TableName);
        Assert.Single(create.Columns);
    }

    [Fact]
    public void CreateTempTableIfNotExists()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE IF NOT EXISTS cache (key STRING, value STRING)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.True(create.IfNotExists);
        Assert.Equal("cache", create.TableName);
        Assert.Equal(2, create.Columns.Count);
    }

    /// <summary>
    /// CREATE TABLE (without TEMP/TEMPORARY) is accepted as an alias for
    /// CREATE TEMP TABLE — all tables created in a session context are temporary.
    /// </summary>
    [Fact]
    public void CreateTableWithoutTempKeyword_ParsesAsCreateTempTable()
    {
        Statement statement = SqlParser.ParseStatement("CREATE TABLE t (id INT)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Equal("t", create.TableName);
        Assert.Single(create.Columns);
    }

    /// <summary>
    /// SQL Server-style #name prefixed identifiers are valid temp table names.
    /// The # prefix is preserved in the table name for consistent catalog lookups.
    /// </summary>
    [Fact]
    public void CreateTable_WithHashPrefixedName_UsesHashPrefixedTableName()
    {
        Statement statement = SqlParser.ParseStatement("CREATE TABLE #test (id INT32)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Equal("#test", create.TableName);
        Assert.Single(create.Columns);
    }

    [Fact]
    public void CreateTempTableAsSelect()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE filtered AS SELECT * FROM orders WHERE amount > 100");

        CreateTableAsSelectStatement create = Assert.IsType<CreateTableAsSelectStatement>(statement);
        Assert.Equal("filtered", create.TableName);
        Assert.False(create.IfNotExists);
        Assert.IsType<SelectQueryExpression>(create.Query);
    }

    [Fact]
    public void CreateTempTableAsSelectIfNotExists()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE IF NOT EXISTS summary AS SELECT category, COUNT(*) AS cnt FROM products GROUP BY category");

        CreateTableAsSelectStatement create = Assert.IsType<CreateTableAsSelectStatement>(statement);
        Assert.True(create.IfNotExists);
        Assert.Equal("summary", create.TableName);
    }

    [Fact]
    public void CreateTableAsSelect_SchemaQualifiedTarget_CapturesSchemaName()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TABLE reports.monthly AS SELECT month, total FROM orders");

        CreateTableAsSelectStatement create = Assert.IsType<CreateTableAsSelectStatement>(statement);
        Assert.Equal("reports", create.SchemaName);
        Assert.Equal("monthly", create.TableName);
        Assert.False(create.IsTemp);
    }

    // ───────────────────── DROP TABLE ─────────────────────

    [Fact]
    public void DropTable()
    {
        Statement statement = SqlParser.ParseStatement("DROP TABLE staging");

        DropTableStatement drop = Assert.IsType<DropTableStatement>(statement);
        Assert.Equal("staging", drop.TableName);
        Assert.False(drop.IfExists);
    }

    [Fact]
    public void DropTableIfExists()
    {
        Statement statement = SqlParser.ParseStatement("DROP TABLE IF EXISTS staging");

        DropTableStatement drop = Assert.IsType<DropTableStatement>(statement);
        Assert.Equal("staging", drop.TableName);
        Assert.True(drop.IfExists);
    }

    // ───────────────────── INSERT INTO ─────────────────────

    [Fact]
    public void InsertIntoSelect()
    {
        Statement statement = SqlParser.ParseStatement(
            "INSERT INTO staging SELECT * FROM raw_data WHERE valid = 1");

        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.Equal("staging", insert.TableName);
        Assert.Null(insert.ColumnNames);
        InsertQuerySource source = Assert.IsType<InsertQuerySource>(insert.Source);
        Assert.IsType<SelectQueryExpression>(source.Query);
    }

    [Fact]
    public void InsertIntoWithColumnList()
    {
        Statement statement = SqlParser.ParseStatement(
            "INSERT INTO staging (id, name) SELECT user_id, user_name FROM users");

        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.Equal("staging", insert.TableName);
        Assert.NotNull(insert.ColumnNames);
        Assert.Equal(2, insert.ColumnNames.Count);
        Assert.Equal("id", insert.ColumnNames[0]);
        Assert.Equal("name", insert.ColumnNames[1]);
    }

    [Fact]
    public void InsertIntoValues()
    {
        Statement statement = SqlParser.ParseStatement(
            "INSERT INTO lookup (id, label) VALUES (1, 'alpha'), (2, 'beta')");

        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.Equal("lookup", insert.TableName);
        Assert.NotNull(insert.ColumnNames);
        Assert.Equal(2, insert.ColumnNames.Count);

        InsertValuesSource values = Assert.IsType<InsertValuesSource>(insert.Source);
        Assert.Equal(2, values.Rows.Count);
        Assert.Equal(2, values.Rows[0].Count);
        Assert.Equal(2, values.Rows[1].Count);
    }

    [Fact]
    public void InsertIntoValuesWithoutColumnList()
    {
        Statement statement = SqlParser.ParseStatement(
            "INSERT INTO t VALUES (42, 'hello')");

        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.Null(insert.ColumnNames);
        InsertValuesSource values = Assert.IsType<InsertValuesSource>(insert.Source);
        Assert.Single(values.Rows);
        Assert.Equal(2, values.Rows[0].Count);
    }

    [Fact]
    public void InsertIntoDefaultValues()
    {
        Statement statement = SqlParser.ParseStatement(
            "INSERT INTO t DEFAULT VALUES");

        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.Equal("t", insert.TableName);
        Assert.Null(insert.ColumnNames);
        Assert.IsType<InsertDefaultValuesSource>(insert.Source);
        Assert.Null(insert.Returning);
    }

    [Fact]
    public void InsertIntoDefaultValues_WithReturning_ParsesBoth()
    {
        Statement statement = SqlParser.ParseStatement(
            "INSERT INTO t DEFAULT VALUES RETURNING id");

        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.IsType<InsertDefaultValuesSource>(insert.Source);
        Assert.NotNull(insert.Returning);
        Assert.Single(insert.Returning);
    }

    // ─────── QA probes: INSERT … DEFAULT VALUES parser shape ───────

    [Fact]
    public void InsertIntoDefaultValues_LowercaseKeywords_Parses()
    {
        // SQL keywords are case-insensitive; the dialect tokenizer
        // matches DEFAULT / VALUES with EqualToIgnoreCase. A regression
        // here would indicate a casing mistake in DefaultValuesSourceParser.
        Statement statement = SqlParser.ParseStatement("INSERT INTO t default values");
        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.IsType<InsertDefaultValuesSource>(insert.Source);
    }

    [Fact]
    public void InsertInto_DefaultWithoutValues_Throws()
    {
        // `DEFAULT` alone is not a valid INSERT source — needs VALUES.
        // The error position should land at the DEFAULT token or just past.
        Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("INSERT INTO t DEFAULT"));
    }

    [Fact]
    public void InsertInto_DefaultValuesWithTrailingTuple_Throws()
    {
        // DEFAULT VALUES is a complete source; a trailing (...) must
        // not be silently consumed as a RETURNING / further VALUES.
        Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("INSERT INTO t DEFAULT VALUES (1, 2)"));
    }

    [Fact]
    public void InsertInto_EmptyColumnList_DefaultValues_ParsesAsDefaultValues()
    {
        // `INSERT INTO t () DEFAULT VALUES` — the column list parser
        // accepts an empty `()` and the source parser sees DEFAULT VALUES.
        // The parser's column-list normalization step turns `Length == 0`
        // into null, so the executor's "column list + DEFAULT VALUES"
        // rejection does not fire here. Document the current behavior
        // so a future tightening makes the test fail loudly.
        Statement statement = SqlParser.ParseStatement("INSERT INTO t () DEFAULT VALUES");
        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.Null(insert.ColumnNames);
        Assert.IsType<InsertDefaultValuesSource>(insert.Source);
    }

    // ───────────────────── INSERT … RETURNING ─────────────────────

    [Fact]
    public void Insert_NoReturningClause_FieldIsNull()
    {
        Statement statement = SqlParser.ParseStatement(
            "INSERT INTO t VALUES (1, 'a')");
        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.Null(insert.Returning);
    }

    [Fact]
    public void Insert_ReturningSingleColumn_ParsesAsSelectColumn()
    {
        Statement statement = SqlParser.ParseStatement(
            "INSERT INTO conversations (workspace, title) VALUES ('default', 'Chat') RETURNING id");

        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.NotNull(insert.Returning);
        Assert.Single(insert.Returning);
        ColumnReference col = Assert.IsType<ColumnReference>(insert.Returning[0].Expression);
        Assert.Equal("id", col.ColumnName);
    }

    [Fact]
    public void Insert_ReturningMultipleColumns_PreservesOrder()
    {
        Statement statement = SqlParser.ParseStatement(
            "INSERT INTO t VALUES (1, 'a') RETURNING id, name, created_at");

        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.NotNull(insert.Returning);
        Assert.Equal(3, insert.Returning.Count);
        Assert.Equal("id", Assert.IsType<ColumnReference>(insert.Returning[0].Expression).ColumnName);
        Assert.Equal("name", Assert.IsType<ColumnReference>(insert.Returning[1].Expression).ColumnName);
        Assert.Equal("created_at", Assert.IsType<ColumnReference>(insert.Returning[2].Expression).ColumnName);
    }

    [Fact]
    public void Insert_ReturningStar_ParsesAsSelectAllColumns()
    {
        Statement statement = SqlParser.ParseStatement(
            "INSERT INTO t VALUES (1, 'a') RETURNING *");

        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.NotNull(insert.Returning);
        Assert.Single(insert.Returning);
        Assert.IsType<SelectAllColumns>(insert.Returning[0]);
    }

    [Fact]
    public void Insert_ReturningExpressionWithAlias_KeepsAlias()
    {
        Statement statement = SqlParser.ParseStatement(
            "INSERT INTO t VALUES (1, 'a') RETURNING id * 10 AS scaled_id");

        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.NotNull(insert.Returning);
        Assert.Single(insert.Returning);
        SelectColumn col = insert.Returning[0];
        Assert.Equal("scaled_id", col.Alias);
        Assert.IsType<BinaryExpression>(col.Expression);
    }

    [Fact]
    public void Insert_SelectSourceWithReturning_ParsesEndToEnd()
    {
        Statement statement = SqlParser.ParseStatement(
            "INSERT INTO summary (id, total) SELECT id, sum FROM staging RETURNING id, total");

        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.IsType<InsertQuerySource>(insert.Source);
        Assert.NotNull(insert.Returning);
        Assert.Equal(2, insert.Returning.Count);
    }

    // ───────────────────── UPDATE ─────────────────────

    [Fact]
    public void UpdateWithWhere()
    {
        Statement statement = SqlParser.ParseStatement(
            "UPDATE staging SET status = 'processed', score = score + 1 WHERE id > 10");

        UpdateStatement update = Assert.IsType<UpdateStatement>(statement);
        Assert.Equal("staging", update.TableName);
        Assert.Equal(2, update.Assignments.Count);
        Assert.Equal("status", update.Assignments[0].ColumnName);
        Assert.Equal("score", update.Assignments[1].ColumnName);
        Assert.NotNull(update.Where);
    }

    [Fact]
    public void UpdateWithoutWhere()
    {
        Statement statement = SqlParser.ParseStatement(
            "UPDATE t SET flag = 1");

        UpdateStatement update = Assert.IsType<UpdateStatement>(statement);
        Assert.Equal("t", update.TableName);
        Assert.Single(update.Assignments);
        Assert.Equal("flag", update.Assignments[0].ColumnName);
        Assert.Null(update.Where);
    }

    [Fact]
    public void UpdateWithAlias()
    {
        Statement statement = SqlParser.ParseStatement(
            "UPDATE features f SET score = 1.0 WHERE f.id = 5");

        UpdateStatement update = Assert.IsType<UpdateStatement>(statement);
        Assert.Equal("features", update.TableName);
        Assert.Equal("f", update.Alias);
        Assert.Single(update.Assignments);
        Assert.NotNull(update.Where);
    }

    [Fact]
    public void UpdateWithAsAlias()
    {
        Statement statement = SqlParser.ParseStatement(
            "UPDATE features AS f SET score = 1.0");

        UpdateStatement update = Assert.IsType<UpdateStatement>(statement);
        Assert.Equal("features", update.TableName);
        Assert.Equal("f", update.Alias);
    }

    [Fact]
    public void UpdateWithFromClause()
    {
        Statement statement = SqlParser.ParseStatement(
            "UPDATE features SET score = raw.value FROM raw WHERE features.id = raw.id");

        UpdateStatement update = Assert.IsType<UpdateStatement>(statement);
        Assert.Equal("features", update.TableName);
        Assert.NotNull(update.From);
        TableReference rawSource = Assert.IsType<TableReference>(update.From!.Source);
        Assert.Equal("raw", rawSource.Name);
        Assert.NotNull(update.Where);
        Assert.Null(update.Joins);
    }

    [Fact]
    public void UpdateWithFromAndJoin()
    {
        Statement statement = SqlParser.ParseStatement(
            "UPDATE features SET score = raw.value * m.weight " +
            "FROM raw JOIN model AS m ON raw.model_id = m.id " +
            "WHERE features.id = raw.id");

        UpdateStatement update = Assert.IsType<UpdateStatement>(statement);
        Assert.Equal("features", update.TableName);
        Assert.NotNull(update.From);
        Assert.NotNull(update.Joins);
        Assert.Single(update.Joins!);
        Assert.NotNull(update.Where);
    }

    // ───────────────────── DELETE ─────────────────────

    /// <summary>DELETE FROM without WHERE parses with null predicate.</summary>
    [Fact]
    public void DeleteFromWithoutWhere()
    {
        Statement statement = SqlParser.ParseStatement("DELETE FROM staging");

        DeleteStatement delete = Assert.IsType<DeleteStatement>(statement);
        Assert.Equal("staging", delete.TableName);
        Assert.Null(delete.Where);
    }

    /// <summary>DELETE FROM with WHERE parses the predicate.</summary>
    [Fact]
    public void DeleteFromWithWhere()
    {
        Statement statement = SqlParser.ParseStatement(
            "DELETE FROM staging WHERE id = 1");

        DeleteStatement delete = Assert.IsType<DeleteStatement>(statement);
        Assert.Equal("staging", delete.TableName);
        Assert.NotNull(delete.Where);
        Assert.IsType<BinaryExpression>(delete.Where);
    }

    /// <summary>DELETE FROM with compound WHERE predicate.</summary>
    [Fact]
    public void DeleteFromWithCompoundWhere()
    {
        Statement statement = SqlParser.ParseStatement(
            "DELETE FROM staging WHERE id > 5 AND name = 'test'");

        DeleteStatement delete = Assert.IsType<DeleteStatement>(statement);
        Assert.Equal("staging", delete.TableName);
        Assert.NotNull(delete.Where);
    }

    // ───────────────────── ALTER TABLE ─────────────────────

    [Fact]
    public void AlterTableAddColumn()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE staging ADD COLUMN category STRING");

        AlterTableAddColumnStatement alter = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.Equal("staging", alter.TableName);
        Assert.Equal("category", alter.ColumnName);
        Assert.Equal("STRING", alter.TypeName);
        Assert.True(alter.Nullable);
        Assert.Null(alter.DefaultValue);
    }

    [Fact]
    public void AlterTableAddColumnWithoutColumnKeyword()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE staging ADD category STRING");

        AlterTableAddColumnStatement alter = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.Equal("category", alter.ColumnName);
        Assert.Equal("STRING", alter.TypeName);
    }

    [Fact]
    public void AlterTableAddColumnNotNull()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE t ADD COLUMN x INT NOT NULL");

        AlterTableAddColumnStatement alter = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.False(alter.Nullable);
    }

    [Fact]
    public void AlterTableAddColumnWithDefault()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE t ADD COLUMN score FLOAT64 DEFAULT 0");

        AlterTableAddColumnStatement alter = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.Equal("score", alter.ColumnName);
        Assert.Equal("FLOAT64", alter.TypeName);
        Assert.NotNull(alter.DefaultValue);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(alter.DefaultValue);
        Assert.Equal((sbyte)0, literal.Value);
    }

    /// <summary>
    /// Computed column: <c>ALTER TABLE t ADD COLUMN ratio FLOAT64 AS (revenue / cost)</c>.
    /// The parens around the expression match the CREATE TABLE form (and PG's
    /// `GENERATED ALWAYS AS (expr)` requirement); bare `AS expr` is rejected.
    /// </summary>
    [Fact]
    public void AlterTableAddColumnComputed()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE t ADD COLUMN ratio FLOAT64 AS (revenue / cost)");

        AlterTableAddColumnStatement alter = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.Equal("ratio", alter.ColumnName);
        Assert.Equal("FLOAT64", alter.TypeName);
        Assert.Null(alter.DefaultValue);
        Assert.NotNull(alter.ComputedExpression);
        BinaryExpression binary = Assert.IsType<BinaryExpression>(alter.ComputedExpression);
        Assert.Equal(BinaryOperator.Divide, binary.Operator);
    }

    /// <summary>
    /// Computed column with a function call: <c>AS (UPPER(name))</c>.
    /// </summary>
    [Fact]
    public void AlterTableAddColumnComputedWithFunction()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE t ADD COLUMN upper_name STRING AS (UPPER(name))");

        AlterTableAddColumnStatement alter = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.NotNull(alter.ComputedExpression);
        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(alter.ComputedExpression);
        Assert.Equal("UPPER", function.FunctionName);
    }

    /// <summary>
    /// Computed column with NOT NULL is accepted.
    /// </summary>
    [Fact]
    public void AlterTableAddColumnComputedNotNull()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE t ADD COLUMN flag BOOLEAN NOT NULL AS (x > 0)");

        AlterTableAddColumnStatement alter = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.False(alter.Nullable);
        Assert.NotNull(alter.ComputedExpression);
    }

    // ─── ALTER TABLE ADD COLUMN — constraint ordering (PG-compliant) ───
    //
    // Same shape as CREATE TABLE: NOT NULL / NULL / DEFAULT / GENERATED /
    // legacy bare AS / legacy bare IDENTITY may appear in any order, and
    // duplicates are rejected at parse time. PRIMARY KEY is intentionally
    // not accepted here (the AST has no slot for it, and ALTER-time PK
    // addition isn't supported in v1).

    /// <summary>
    /// GENERATED can precede NOT NULL (and vice versa).
    /// </summary>
    [Theory]
    [InlineData("NOT NULL GENERATED ALWAYS AS IDENTITY")]
    [InlineData("GENERATED ALWAYS AS IDENTITY NOT NULL")]
    public void AlterTableAddColumn_NotNullAndGenerated_AnyOrder(string constraints)
    {
        Statement statement = SqlParser.ParseStatement(
            $"ALTER TABLE t ADD COLUMN id Int64 {constraints}");

        AlterTableAddColumnStatement alter = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.False(alter.Nullable);
        Assert.NotNull(alter.Identity);
        Assert.False(alter.Identity!.AcceptUserValues);
    }

    /// <summary>
    /// All permutations of {NOT NULL, DEFAULT 0} on ADD COLUMN produce the same shape.
    /// </summary>
    [Theory]
    [InlineData("NOT NULL DEFAULT 0")]
    [InlineData("DEFAULT 0 NOT NULL")]
    public void AlterTableAddColumn_NotNullAndDefault_AnyOrder(string constraints)
    {
        Statement statement = SqlParser.ParseStatement(
            $"ALTER TABLE t ADD COLUMN x Int32 {constraints}");

        AlterTableAddColumnStatement alter = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.False(alter.Nullable);
        Assert.NotNull(alter.DefaultValue);
    }

    /// <summary>
    /// Bare <c>NULL</c> on ADD COLUMN parses, leaving the column nullable.
    /// </summary>
    [Fact]
    public void AlterTableAddColumn_BareNull_Parses()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE t ADD COLUMN name STRING NULL");

        AlterTableAddColumnStatement alter = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.True(alter.Nullable);
    }

    /// <summary>
    /// Duplicate <c>NOT NULL</c> on ADD COLUMN is rejected at parse time.
    /// </summary>
    [Fact]
    public void AlterTableAddColumn_DuplicateNotNull_Throws()
    {
        Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("ALTER TABLE t ADD COLUMN x Int32 NOT NULL NOT NULL"));
    }

    /// <summary>
    /// Duplicate <c>DEFAULT</c> on ADD COLUMN is rejected at parse time.
    /// </summary>
    [Fact]
    public void AlterTableAddColumn_DuplicateDefault_Throws()
    {
        Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("ALTER TABLE t ADD COLUMN x Int32 DEFAULT 1 DEFAULT 2"));
    }

    /// <summary>
    /// Two GENERATED clauses (or GENERATED + legacy bare IDENTITY) on
    /// ADD COLUMN are rejected at parse time.
    /// </summary>
    [Fact]
    public void AlterTableAddColumn_DuplicateGenerated_Throws()
    {
        Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement(
                "ALTER TABLE t ADD COLUMN id Int64 GENERATED ALWAYS AS IDENTITY GENERATED BY DEFAULT AS IDENTITY"));
    }

    /// <summary>
    /// Conflicting <c>NULL</c> + <c>NOT NULL</c> on ADD COLUMN is rejected.
    /// </summary>
    [Fact]
    public void AlterTableAddColumn_NullAndNotNull_Throws()
    {
        Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("ALTER TABLE t ADD COLUMN x Int32 NULL NOT NULL"));
    }

    // ── ALTER TABLE ADD COLUMN with PRIMARY KEY (parser surface) ──

    /// <summary>
    /// <c>ALTER TABLE t ADD COLUMN id Int64 PRIMARY KEY</c> parses with
    /// <see cref="AlterTableAddColumnStatement.PrimaryKey"/> set and
    /// <see cref="AlterTableAddColumnStatement.Nullable"/> implicitly false.
    /// </summary>
    [Fact]
    public void AlterTableAddColumn_PrimaryKey_Parses()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE t ADD COLUMN id Int64 PRIMARY KEY");

        AlterTableAddColumnStatement alter = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.True(alter.PrimaryKey);
        Assert.False(alter.Nullable, "PRIMARY KEY column should be implicitly NOT NULL.");
    }

    /// <summary>
    /// PRIMARY KEY and GENERATED ALWAYS AS IDENTITY can appear in either
    /// order on an ADD COLUMN (matches the CREATE TABLE surface).
    /// </summary>
    [Theory]
    [InlineData("PRIMARY KEY GENERATED ALWAYS AS IDENTITY")]
    [InlineData("GENERATED ALWAYS AS IDENTITY PRIMARY KEY")]
    public void AlterTableAddColumn_PrimaryKeyAndGenerated_AnyOrder(string constraints)
    {
        Statement statement = SqlParser.ParseStatement(
            $"ALTER TABLE t ADD COLUMN id Int64 {constraints}");

        AlterTableAddColumnStatement alter = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.True(alter.PrimaryKey);
        Assert.NotNull(alter.Identity);
        Assert.False(alter.Identity!.AcceptUserValues);
        Assert.False(alter.Nullable);
    }

    /// <summary>
    /// Duplicate PRIMARY KEY on ADD COLUMN is rejected at parse time.
    /// </summary>
    [Fact]
    public void AlterTableAddColumn_DuplicatePrimaryKey_Throws()
    {
        Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement(
                "ALTER TABLE t ADD COLUMN id Int64 PRIMARY KEY PRIMARY KEY"));
    }

    // ─── ALTER TABLE DROP CONSTRAINT (parser surface) ───

    /// <summary>
    /// <c>ALTER TABLE t DROP CONSTRAINT users_pkey</c> parses to
    /// <see cref="AlterTableDropConstraintStatement"/>.
    /// </summary>
    [Fact]
    public void AlterTableDropConstraint_Parses()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE users DROP CONSTRAINT users_pkey");

        AlterTableDropConstraintStatement drop = Assert.IsType<AlterTableDropConstraintStatement>(statement);
        Assert.Equal("users", drop.TableName);
        Assert.Equal("users_pkey", drop.ConstraintName);
        Assert.False(drop.IfExists);
    }

    /// <summary>
    /// <c>ALTER TABLE t DROP CONSTRAINT IF EXISTS …</c> sets IfExists=true.
    /// </summary>
    [Fact]
    public void AlterTableDropConstraint_IfExists_Parses()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE users DROP CONSTRAINT IF EXISTS users_pkey");

        AlterTableDropConstraintStatement drop = Assert.IsType<AlterTableDropConstraintStatement>(statement);
        Assert.True(drop.IfExists);
        Assert.Equal("users_pkey", drop.ConstraintName);
    }

    /// <summary>
    /// The legacy <c>DROP COLUMN</c> path is unaffected by the new
    /// <c>DROP CONSTRAINT</c> dispatch — a regression guard for the
    /// dispatcher that picks between the two DROP arms.
    /// </summary>
    [Fact]
    public void AlterTableDropColumn_StillParses_AfterDropConstraintAdded()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE users DROP COLUMN obsolete");

        AlterTableDropColumnStatement drop = Assert.IsType<AlterTableDropColumnStatement>(statement);
        Assert.Equal("obsolete", drop.ColumnName);
    }

    // ─── ALTER TABLE ALTER COLUMN DROP { IDENTITY | DEFAULT } (parser surface) ───

    [Fact]
    public void AlterTableAlterColumnDropIdentity_Parses()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE users ALTER COLUMN id DROP IDENTITY");

        AlterTableAlterColumnDropStatement alter = Assert.IsType<AlterTableAlterColumnDropStatement>(statement);
        Assert.Equal("users", alter.TableName);
        Assert.Equal("id", alter.ColumnName);
        Assert.Equal(AlterColumnDropTarget.Identity, alter.Target);
        Assert.False(alter.IfExists);
    }

    [Fact]
    public void AlterTableAlterColumnDropIdentity_IfExists_Parses()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE users ALTER COLUMN id DROP IDENTITY IF EXISTS");

        AlterTableAlterColumnDropStatement alter = Assert.IsType<AlterTableAlterColumnDropStatement>(statement);
        Assert.True(alter.IfExists);
        Assert.Equal(AlterColumnDropTarget.Identity, alter.Target);
    }

    [Fact]
    public void AlterTableAlterColumnDropDefault_Parses()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE users ALTER COLUMN created_at DROP DEFAULT");

        AlterTableAlterColumnDropStatement alter = Assert.IsType<AlterTableAlterColumnDropStatement>(statement);
        Assert.Equal("users", alter.TableName);
        Assert.Equal("created_at", alter.ColumnName);
        Assert.Equal(AlterColumnDropTarget.Default, alter.Target);
    }

    [Fact]
    public void AlterTableAlterColumnDropDefault_IfExists_Parses()
    {
        // PG doesn't accept IF EXISTS on DROP DEFAULT (it's idempotent), but
        // accepting it uniformly keeps the grammar simple — the catalog
        // treats DROP DEFAULT as idempotent regardless of the keyword.
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE users ALTER COLUMN created_at DROP DEFAULT IF EXISTS");

        AlterTableAlterColumnDropStatement alter = Assert.IsType<AlterTableAlterColumnDropStatement>(statement);
        Assert.True(alter.IfExists);
        Assert.Equal(AlterColumnDropTarget.Default, alter.Target);
    }

    [Fact]
    public void AlterTableAlterColumnDrop_UnknownTarget_Throws()
    {
        // PRIMARY KEY isn't an ALTER COLUMN target in PG either.
        Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement(
                "ALTER TABLE users ALTER COLUMN id DROP SOMETHING_ELSE"));
    }

    [Fact]
    public void AlterTableAlterColumnDropNotNull_Parses()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE users ALTER COLUMN id DROP NOT NULL");

        AlterTableAlterColumnDropStatement alter = Assert.IsType<AlterTableAlterColumnDropStatement>(statement);
        Assert.Equal("users", alter.TableName);
        Assert.Equal("id", alter.ColumnName);
        Assert.Equal(AlterColumnDropTarget.NotNull, alter.Target);
        Assert.False(alter.IfExists);
    }

    [Fact]
    public void AlterTableAlterColumnDropNotNull_IfExists_Parses()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE users ALTER COLUMN id DROP NOT NULL IF EXISTS");

        AlterTableAlterColumnDropStatement alter = Assert.IsType<AlterTableAlterColumnDropStatement>(statement);
        Assert.True(alter.IfExists);
        Assert.Equal(AlterColumnDropTarget.NotNull, alter.Target);
    }

    [Fact]
    public void AlterTableAlterColumnSetNotNull_Parses()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE users ALTER COLUMN id SET NOT NULL");

        AlterTableAlterColumnSetStatement alter = Assert.IsType<AlterTableAlterColumnSetStatement>(statement);
        Assert.Equal("users", alter.TableName);
        Assert.Equal("id", alter.ColumnName);
        Assert.Equal(AlterColumnSetTarget.NotNull, alter.Target);
    }

    [Fact]
    public void AlterTableAlterColumn_SetAndDrop_BothRouteCorrectly()
    {
        // Regression guard: the dispatcher that picks SET vs DROP must
        // route both correctly without ambiguity.
        Statement set = SqlParser.ParseStatement(
            "ALTER TABLE t ALTER COLUMN id SET NOT NULL");
        Statement drop = SqlParser.ParseStatement(
            "ALTER TABLE t ALTER COLUMN id DROP NOT NULL");

        Assert.IsType<AlterTableAlterColumnSetStatement>(set);
        Assert.IsType<AlterTableAlterColumnDropStatement>(drop);
    }

    // ─── ALTER TABLE IF EXISTS (table-level guard) ───

    /// <summary>
    /// <c>ALTER TABLE IF EXISTS name …</c> parses with the table-level
    /// IF EXISTS flag set, regardless of which body parser fires next.
    /// </summary>
    [Theory]
    [InlineData(
        "ALTER TABLE IF EXISTS users ADD COLUMN flag Boolean",
        typeof(AlterTableAddColumnStatement))]
    [InlineData(
        "ALTER TABLE IF EXISTS users DROP COLUMN flag",
        typeof(AlterTableDropColumnStatement))]
    [InlineData(
        "ALTER TABLE IF EXISTS users DROP CONSTRAINT users_pkey",
        typeof(AlterTableDropConstraintStatement))]
    [InlineData(
        "ALTER TABLE IF EXISTS users ALTER COLUMN id DROP DEFAULT",
        typeof(AlterTableAlterColumnDropStatement))]
    public void AlterTable_TableLevelIfExists_Parses(string sql, Type expectedType)
    {
        Statement statement = SqlParser.ParseStatement(sql);

        Assert.IsType(expectedType, statement);
        // Every ALTER TABLE body now carries a TableIfExists flag; verify
        // it surfaced on whichever AST node fired.
        bool tableIfExists = statement switch
        {
            AlterTableAddColumnStatement a => a.TableIfExists,
            AlterTableDropColumnStatement a => a.TableIfExists,
            AlterTableDropConstraintStatement a => a.TableIfExists,
            AlterTableAlterColumnDropStatement a => a.TableIfExists,
            _ => false,
        };
        Assert.True(tableIfExists, "TableIfExists should be true after `ALTER TABLE IF EXISTS …`.");
    }

    /// <summary>
    /// Plain <c>ALTER TABLE name …</c> (no IF EXISTS) leaves the flag false.
    /// </summary>
    [Fact]
    public void AlterTable_WithoutTableLevelIfExists_FlagFalse()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE users ADD COLUMN flag Boolean");

        AlterTableAddColumnStatement add = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.False(add.TableIfExists);
    }

    // ─── User-supplied CONSTRAINT names in CREATE TABLE ───

    /// <summary>
    /// Column-level <c>CONSTRAINT name PRIMARY KEY</c>. The name surfaces on
    /// <see cref="CreateTableStatement.PrimaryKeyConstraintName"/>.
    /// </summary>
    [Fact]
    public void CreateTable_ColumnLevelNamedPk_Parses()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE t (id Int32 CONSTRAINT my_pk PRIMARY KEY, name String)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Equal("my_pk", create.PrimaryKeyConstraintName);
        Assert.True(create.Columns[0].PrimaryKey);
    }

    /// <summary>
    /// Table-level <c>CONSTRAINT name PRIMARY KEY (cols)</c>.
    /// </summary>
    [Fact]
    public void CreateTable_TableLevelNamedPk_Parses()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE t (a Int32, b Int32, CONSTRAINT compound_pk PRIMARY KEY (a, b))");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Equal("compound_pk", create.PrimaryKeyConstraintName);
        Assert.NotNull(create.PrimaryKeyColumns);
        Assert.Equal(["a", "b"], create.PrimaryKeyColumns);
    }

    /// <summary>
    /// No <c>CONSTRAINT</c> prefix → name is null (catalog derives the
    /// default <c>&lt;table&gt;_pkey</c>).
    /// </summary>
    [Fact]
    public void CreateTable_UnnamedPk_PrimaryKeyConstraintNameIsNull()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE t (id Int32 PRIMARY KEY)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Null(create.PrimaryKeyConstraintName);
    }

    /// <summary>
    /// Column-level CONSTRAINT can stand in any constraint-order
    /// position (it's part of the constraint set).
    /// </summary>
    [Theory]
    [InlineData("CONSTRAINT my_pk PRIMARY KEY NOT NULL")]
    [InlineData("NOT NULL CONSTRAINT my_pk PRIMARY KEY")]
    public void CreateTable_NamedPk_OrderIndependent(string constraints)
    {
        Statement statement = SqlParser.ParseStatement(
            $"CREATE TEMP TABLE t (id Int32 {constraints})");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Equal("my_pk", create.PrimaryKeyConstraintName);
        Assert.True(create.Columns[0].PrimaryKey);
    }

    // ───────────────────── Query as Statement ─────────────────────

    [Fact]
    public void SelectParsesAsQueryStatement()
    {
        Statement statement = SqlParser.ParseStatement("SELECT 1");

        QueryStatement query = Assert.IsType<QueryStatement>(statement);
        Assert.IsType<SelectQueryExpression>(query.Query);
    }

    [Fact]
    public void SelectWithCtesParsesAsQueryStatement()
    {
        Statement statement = SqlParser.ParseStatement(
            "WITH cte AS (SELECT 1 AS x) SELECT * FROM cte");

        QueryStatement query = Assert.IsType<QueryStatement>(statement);
        SelectQueryExpression selectQuery = Assert.IsType<SelectQueryExpression>(query.Query);
        Assert.NotNull(selectQuery.Statement.CommonTableExpressions);
    }

    // ───────────────────── Batch parsing ─────────────────────

    [Fact]
    public void SingleStatementBatch()
    {
        IReadOnlyList<Statement> statements = SqlParser.ParseBatch("SELECT 1");

        Assert.Single(statements);
        Assert.IsType<QueryStatement>(statements[0]);
    }

    [Fact]
    public void TwoStatementBatch()
    {
        IReadOnlyList<Statement> statements = SqlParser.ParseBatch(
            "CREATE TEMP TABLE t (x INT); SELECT * FROM t");

        Assert.Equal(2, statements.Count);
        Assert.IsType<CreateTableStatement>(statements[0]);
        Assert.IsType<QueryStatement>(statements[1]);
    }

    [Fact]
    public void MultiStatementBatchWithTrailingSemicolon()
    {
        IReadOnlyList<Statement> statements = SqlParser.ParseBatch(
            "SELECT 1; SELECT 2;");

        Assert.Equal(2, statements.Count);
        Assert.IsType<QueryStatement>(statements[0]);
        Assert.IsType<QueryStatement>(statements[1]);
    }

    [Fact]
    public void MixedDdlDmlBatch()
    {
        IReadOnlyList<Statement> statements = SqlParser.ParseBatch("""
            CREATE TEMP TABLE staging (id INT, value STRING);
            INSERT INTO staging VALUES (1, 'hello'), (2, 'world');
            UPDATE staging SET value = 'updated' WHERE id = 1;
            DELETE FROM staging WHERE id = 2;
            ALTER TABLE staging ADD COLUMN flag INT DEFAULT 0;
            SELECT * FROM staging;
            DROP TABLE staging
            """);

        Assert.Equal(7, statements.Count);
        Assert.IsType<CreateTableStatement>(statements[0]);
        Assert.IsType<InsertStatement>(statements[1]);
        Assert.IsType<UpdateStatement>(statements[2]);
        Assert.IsType<DeleteStatement>(statements[3]);
        Assert.IsType<AlterTableAddColumnStatement>(statements[4]);
        Assert.IsType<QueryStatement>(statements[5]);
        Assert.IsType<DropTableStatement>(statements[6]);
    }

    [Fact]
    public void BatchWithCreateAsSelectAndQuery()
    {
        IReadOnlyList<Statement> statements = SqlParser.ParseBatch(
            "CREATE TEMP TABLE filtered AS SELECT * FROM orders WHERE amount > 100; SELECT COUNT(*) FROM filtered");

        Assert.Equal(2, statements.Count);
        Assert.IsType<CreateTableAsSelectStatement>(statements[0]);
        Assert.IsType<QueryStatement>(statements[1]);
    }

    // ─── Missing-column-type diagnostics — ALTER TABLE ADD COLUMN ───

    [Fact]
    public void AlterTableAddColumn_MissingTypeBeforeAs_ReportsFriendlyError()
    {
        ParseException ex = Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("ALTER TABLE t ADD COLUMN ratio AS (revenue / cost)"));
        Assert.Contains("ALTER TABLE ADD COLUMN 'ratio'", ex.Message);
        Assert.Contains("missing column type before AS", ex.Message);
    }

    [Fact]
    public void AlterTableAddColumn_MissingTypeBeforeDefault_ReportsFriendlyError()
    {
        ParseException ex = Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("ALTER TABLE t ADD COLUMN score DEFAULT 0"));
        Assert.Contains("ALTER TABLE ADD COLUMN 'score'", ex.Message);
        Assert.Contains("missing column type before DEFAULT", ex.Message);
    }

    [Fact]
    public void AlterTableAddColumn_MissingTypeBeforeNotNull_ReportsFriendlyError()
    {
        ParseException ex = Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("ALTER TABLE t ADD COLUMN x NOT NULL"));
        Assert.Contains("ALTER TABLE ADD COLUMN 'x'", ex.Message);
        Assert.Contains("missing column type before NOT", ex.Message);
    }

    [Fact]
    public void AlterTableAddColumn_MissingTypeBeforeGenerated_ReportsFriendlyError()
    {
        ParseException ex = Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("ALTER TABLE t ADD COLUMN id GENERATED ALWAYS AS IDENTITY"));
        Assert.Contains("ALTER TABLE ADD COLUMN 'id'", ex.Message);
        Assert.Contains("missing column type before GENERATED", ex.Message);
    }

    [Fact]
    public void AlterTableAddColumn_MissingTypeBeforeIdentity_ReportsFriendlyError()
    {
        ParseException ex = Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("ALTER TABLE t ADD COLUMN id IDENTITY"));
        Assert.Contains("ALTER TABLE ADD COLUMN 'id'", ex.Message);
        Assert.Contains("missing column type before IDENTITY", ex.Message);
    }

    // ─── Missing-column-type diagnostics — CREATE TABLE ───

    [Fact]
    public void CreateTable_MissingTypeBeforeAs_ReportsFriendlyError()
    {
        ParseException ex = Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("CREATE TABLE t (ratio AS (revenue / cost))"));
        Assert.Contains("column 'ratio'", ex.Message);
        Assert.Contains("missing column type before AS", ex.Message);
    }

    [Fact]
    public void CreateTable_MissingTypeBeforeDefault_ReportsFriendlyError()
    {
        ParseException ex = Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("CREATE TABLE t (score DEFAULT 0)"));
        Assert.Contains("column 'score'", ex.Message);
        Assert.Contains("missing column type before DEFAULT", ex.Message);
    }

    [Fact]
    public void CreateTable_MissingTypeBeforeNotNull_ReportsFriendlyError()
    {
        ParseException ex = Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("CREATE TABLE t (x NOT NULL)"));
        Assert.Contains("column 'x'", ex.Message);
        Assert.Contains("missing column type before NOT", ex.Message);
    }

    [Fact]
    public void CreateTable_MissingTypeBeforePrimary_ReportsFriendlyError()
    {
        // `id PRIMARY KEY` without a type — distinct from the table-level
        // trailing `, PRIMARY KEY (cols)` form, which is unaffected.
        ParseException ex = Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("CREATE TABLE t (id PRIMARY KEY)"));
        Assert.Contains("column 'id'", ex.Message);
        Assert.Contains("missing column type before PRIMARY", ex.Message);
    }

    [Fact]
    public void CreateTable_TableLevelPrimaryKey_StillParses()
    {
        // Regression: the trailing-column .Try() backtrack path must still
        // resolve `, PRIMARY KEY (col)` to the table-level constraint after
        // the helper falls through. The friendly-error throw must not fire
        // when PRIMARY appears in the *trailing-comma* position rather than
        // immediately after a bare column name.
        Statement statement = SqlParser.ParseStatement(
            "CREATE TABLE t (id INT32, name STRING, PRIMARY KEY (id))");
        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Equal(2, create.Columns.Count);
        Assert.Equal("id", create.Columns[0].Name);
        Assert.Equal("name", create.Columns[1].Name);
    }

    // ───────────────────── Error cases ─────────────────────

    [Fact]
    public void ParseBatchRejectsEmptyInput()
    {
        Assert.Throws<ParseException>(() =>
            SqlParser.ParseBatch(""));
    }

    // ───────────────────── PRIMARY KEY ─────────────────────

    /// <summary>
    /// PRIMARY KEY constraint is parsed and sets <see cref="ColumnDefinition.PrimaryKey"/> to true.
    /// </summary>
    [Fact]
    public void CreateTempTable_PrimaryKeyColumn()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE orders (id INT PRIMARY KEY, name STRING)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Equal(2, create.Columns.Count);
        Assert.True(create.Columns[0].PrimaryKey);
        Assert.False(create.Columns[1].PrimaryKey);
        Assert.NotNull(create.PrimaryKeyColumns);
        Assert.Single(create.PrimaryKeyColumns);
        Assert.Equal("id", create.PrimaryKeyColumns[0]);
    }

    /// <summary>
    /// A PRIMARY KEY column is implicitly NOT NULL even without an explicit NOT NULL keyword.
    /// </summary>
    [Fact]
    public void CreateTempTable_PrimaryKeyImpliesNotNull()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE orders (id INT PRIMARY KEY, label STRING)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.False(create.Columns[0].Nullable, "PRIMARY KEY column should be implicitly NOT NULL.");
        Assert.True(create.Columns[1].Nullable, "Non-PK column should remain nullable by default.");
    }

    /// <summary>
    /// Explicit NOT NULL combined with PRIMARY KEY parses without error.
    /// </summary>
    [Fact]
    public void CreateTempTable_PrimaryKeyWithExplicitNotNull()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE orders (id INT NOT NULL PRIMARY KEY)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.True(create.Columns[0].PrimaryKey);
        Assert.False(create.Columns[0].Nullable);
    }

    /// <summary>
    /// The words PRIMARY and KEY are valid as column names when used independently.
    /// </summary>
    [Fact]
    public void CreateTempTable_PrimaryAndKeyAsColumnNames()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE t (primary STRING, key STRING, value STRING)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Equal(3, create.Columns.Count);
        Assert.Equal("primary", create.Columns[0].Name);
        Assert.Equal("key", create.Columns[1].Name);
        Assert.Equal("value", create.Columns[2].Name);
        Assert.False(create.Columns[0].PrimaryKey);
        Assert.False(create.Columns[1].PrimaryKey);
    }

    /// <summary>
    /// Table-level PRIMARY KEY clause: <c>PRIMARY KEY (user_id, product_id)</c>.
    /// </summary>
    [Fact]
    public void CreateTempTable_TableLevelCompositePrimaryKey()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE orders (user_id INT, product_id INT, quantity INT, PRIMARY KEY (user_id, product_id))");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Equal(3, create.Columns.Count);
        Assert.NotNull(create.PrimaryKeyColumns);
        Assert.Equal(2, create.PrimaryKeyColumns.Count);
        Assert.Equal("user_id", create.PrimaryKeyColumns[0]);
        Assert.Equal("product_id", create.PrimaryKeyColumns[1]);
    }

    /// <summary>
    /// Table-level PRIMARY KEY with a single column.
    /// </summary>
    [Fact]
    public void CreateTempTable_TableLevelSinglePrimaryKey()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE orders (id INT, name STRING, PRIMARY KEY (id))");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.NotNull(create.PrimaryKeyColumns);
        Assert.Single(create.PrimaryKeyColumns);
        Assert.Equal("id", create.PrimaryKeyColumns[0]);
    }

    /// <summary>
    /// No PK declared — PrimaryKeyColumns is null.
    /// </summary>
    [Fact]
    public void CreateTempTable_NoPrimaryKey_PrimaryKeyColumnsIsNull()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE data (x INT, y STRING)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Null(create.PrimaryKeyColumns);
    }

    // ───────────────────── Column constraint ordering (PG-compliant) ─────────────────────
    //
    // PostgreSQL accepts column constraints (NULL / NOT NULL / PRIMARY KEY /
    // DEFAULT / GENERATED …) in any order and any number — see
    // https://www.postgresql.org/docs/current/sql-createtable.html. These
    // tests pin order-independence, bare NULL acceptance, and
    // parser-level rejection of duplicate clauses with positional errors.

    /// <summary>
    /// The PG-canonical example that motivated this section:
    /// <c>GENERATED ALWAYS AS IDENTITY PRIMARY KEY</c> (GENERATED before PK)
    /// must parse identically to <c>PRIMARY KEY GENERATED ALWAYS AS IDENTITY</c>.
    /// </summary>
    [Fact]
    public void CreateTable_GeneratedAlwaysAsIdentity_BeforePrimaryKey_Parses()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TABLE t (id Int64 GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name STRING)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        ColumnDefinition id = create.Columns[0];
        Assert.True(id.PrimaryKey);
        Assert.NotNull(id.Identity);
        Assert.False(id.Identity!.AcceptUserValues);
        Assert.False(id.Nullable, "PRIMARY KEY column should be implicitly NOT NULL.");
    }

    /// <summary>
    /// All six permutations of {PRIMARY KEY, NOT NULL, DEFAULT 0} produce
    /// the same ColumnDefinition shape — confirms the parser doesn't
    /// depend on declaration order.
    /// </summary>
    [Theory]
    [InlineData("PRIMARY KEY NOT NULL DEFAULT 0")]
    [InlineData("PRIMARY KEY DEFAULT 0 NOT NULL")]
    [InlineData("NOT NULL PRIMARY KEY DEFAULT 0")]
    [InlineData("NOT NULL DEFAULT 0 PRIMARY KEY")]
    [InlineData("DEFAULT 0 PRIMARY KEY NOT NULL")]
    [InlineData("DEFAULT 0 NOT NULL PRIMARY KEY")]
    public void CreateTable_ColumnConstraints_AnyOrder_ProducesSameShape(string constraints)
    {
        Statement statement = SqlParser.ParseStatement(
            $"CREATE TABLE t (id Int32 {constraints})");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        ColumnDefinition col = create.Columns[0];
        Assert.True(col.PrimaryKey);
        Assert.False(col.Nullable);
        Assert.NotNull(col.DefaultValue);
    }

    /// <summary>
    /// GENERATED ALWAYS AS IDENTITY can precede or follow PRIMARY KEY.
    /// </summary>
    [Theory]
    [InlineData("PRIMARY KEY GENERATED ALWAYS AS IDENTITY")]
    [InlineData("GENERATED ALWAYS AS IDENTITY PRIMARY KEY")]
    public void CreateTable_PrimaryKeyAndGenerated_AnyOrder(string constraints)
    {
        Statement statement = SqlParser.ParseStatement(
            $"CREATE TABLE t (id Int64 {constraints})");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        ColumnDefinition col = create.Columns[0];
        Assert.True(col.PrimaryKey);
        Assert.NotNull(col.Identity);
        Assert.False(col.Identity!.AcceptUserValues);
    }

    /// <summary>
    /// Bare <c>NULL</c> is a column constraint in PG (the explicit
    /// counterpart of <c>NOT NULL</c>). It must parse, leaving the
    /// column nullable.
    /// </summary>
    [Fact]
    public void CreateTable_BareNullConstraint_Parses()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TABLE t (id Int32 NULL, name STRING NULL)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.True(create.Columns[0].Nullable);
        Assert.True(create.Columns[1].Nullable);
    }

    /// <summary>
    /// Bare <c>NULL</c> composes with other constraints regardless of order.
    /// </summary>
    [Theory]
    [InlineData("NULL DEFAULT 0")]
    [InlineData("DEFAULT 0 NULL")]
    public void CreateTable_BareNull_WithOtherConstraints_Parses(string constraints)
    {
        Statement statement = SqlParser.ParseStatement(
            $"CREATE TABLE t (x Int32 {constraints})");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.True(create.Columns[0].Nullable);
        Assert.NotNull(create.Columns[0].DefaultValue);
    }

    /// <summary>
    /// Duplicate <c>PRIMARY KEY</c> on the same column is rejected at
    /// parse time so the error carries a token position.
    /// </summary>
    [Fact]
    public void CreateTable_DuplicatePrimaryKey_Throws()
    {
        Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("CREATE TABLE t (id Int32 PRIMARY KEY PRIMARY KEY)"));
    }

    /// <summary>
    /// Duplicate <c>NOT NULL</c> on the same column is rejected at parse time.
    /// </summary>
    [Fact]
    public void CreateTable_DuplicateNotNull_Throws()
    {
        Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("CREATE TABLE t (id Int32 NOT NULL NOT NULL)"));
    }

    /// <summary>
    /// Duplicate <c>DEFAULT</c> on the same column is rejected at parse time.
    /// </summary>
    [Fact]
    public void CreateTable_DuplicateDefault_Throws()
    {
        Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("CREATE TABLE t (id Int32 DEFAULT 1 DEFAULT 2)"));
    }

    /// <summary>
    /// Conflicting <c>NULL</c> and <c>NOT NULL</c> on the same column is
    /// rejected at parse time (both occupy the nullability slot).
    /// </summary>
    [Fact]
    public void CreateTable_NullAndNotNull_Throws()
    {
        Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("CREATE TABLE t (id Int32 NULL NOT NULL)"));
    }

    /// <summary>
    /// Two <c>GENERATED</c> clauses on the same column are rejected at
    /// parse time. Also covers the GENERATED + legacy bare IDENTITY combo
    /// — both target the Identity slot.
    /// </summary>
    [Fact]
    public void CreateTable_DuplicateGenerated_Throws()
    {
        Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement(
                "CREATE TABLE t (id Int64 GENERATED ALWAYS AS IDENTITY GENERATED BY DEFAULT AS IDENTITY)"));
    }

    /// <summary>
    /// Duplicate clauses report an error position pointing at the second
    /// occurrence, not at end-of-input — so editors can pinpoint the
    /// offending token.
    /// </summary>
    [Fact]
    public void CreateTable_DuplicatePrimaryKey_ErrorPositionPointsAtSecondOccurrence()
    {
        ParseException ex = Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("CREATE TABLE t (id Int32 PRIMARY KEY PRIMARY KEY)"));

        // The second `PRIMARY` token starts at column 38 (1-based).
        // Allow some slack because Superpower may report at or just past
        // the offending token; the key invariant is "not at EOF".
        Assert.True(ex.ErrorPosition.HasValue);
        Assert.True(
            ex.ErrorPosition.Column >= 30 && ex.ErrorPosition.Column <= 50,
            $"Expected error position near the duplicate PRIMARY KEY (col ~38), got column {ex.ErrorPosition.Column}.");
    }

    // ───────────────────── ANALYZE ─────────────────────

    /// <summary>
    /// ANALYZE table parses to an <see cref="AnalyzeTableStatement"/>.
    /// </summary>
    [Fact]
    public void AnalyzeTable()
    {
        Statement statement = SqlParser.ParseStatement("ANALYZE features");

        AnalyzeTableStatement analyze = Assert.IsType<AnalyzeTableStatement>(statement);
        Assert.Equal("features", analyze.TableName);
    }

    /// <summary>
    /// The word ANALYZE is valid as a column name.
    /// </summary>
    [Fact]
    public void AnalyzeAsColumnName()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE t (analyze STRING, value INT)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Equal("analyze", create.Columns[0].Name);
    }

    /// <summary>
    /// ANALYZE can appear in a batch with other statements.
    /// </summary>
    [Fact]
    public void AnalyzeInBatch()
    {
        IReadOnlyList<Statement> statements = SqlParser.ParseBatch(
            "INSERT INTO t VALUES (1); ANALYZE t");

        Assert.Equal(2, statements.Count);
        Assert.IsType<InsertStatement>(statements[0]);
        Assert.IsType<AnalyzeTableStatement>(statements[1]);
    }

    // ───────────────────── REINDEX ─────────────────────

    [Fact]
    public void ReindexTable()
    {
        Statement statement = SqlParser.ParseStatement("REINDEX features");

        ReindexTableStatement reindex = Assert.IsType<ReindexTableStatement>(statement);
        Assert.Equal("features", reindex.TableName);
    }

    [Fact]
    public void ReindexTableWithKeyword()
    {
        // PostgreSQL-compatible `REINDEX TABLE name` form.
        Statement statement = SqlParser.ParseStatement("REINDEX TABLE features");

        ReindexTableStatement reindex = Assert.IsType<ReindexTableStatement>(statement);
        Assert.Equal("features", reindex.TableName);
    }

    [Fact]
    public void ReindexAsColumnName()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE t (reindex STRING, value INT)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Equal("reindex", create.Columns[0].Name);
    }

    [Fact]
    public void ReindexInBatch()
    {
        IReadOnlyList<Statement> statements = SqlParser.ParseBatch(
            "INSERT INTO t VALUES (1); REINDEX t");

        Assert.Equal(2, statements.Count);
        Assert.IsType<InsertStatement>(statements[0]);
        Assert.IsType<ReindexTableStatement>(statements[1]);
    }

    // ───────────────────── CREATE INDEX / DROP INDEX ─────────────────────

    [Fact]
    public void CreateIndexSingleColumn()
    {
        Statement statement = SqlParser.ParseStatement("CREATE INDEX idx_uid ON users (user_id)");

        CreateIndexStatement create = Assert.IsType<CreateIndexStatement>(statement);
        Assert.Equal("idx_uid", create.IndexName);
        Assert.Equal("users", create.TableName);
        Assert.Equal(new[] { "user_id" }, create.Columns);
        Assert.False(create.IfNotExists);
    }

    [Fact]
    public void CreateIndexCompositeColumns()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE INDEX idx_order ON orders (customer_id, order_date, product_id)");

        CreateIndexStatement create = Assert.IsType<CreateIndexStatement>(statement);
        Assert.Equal("idx_order", create.IndexName);
        Assert.Equal("orders", create.TableName);
        Assert.Equal(new[] { "customer_id", "order_date", "product_id" }, create.Columns);
    }

    [Fact]
    public void CreateIndexIfNotExists()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE INDEX IF NOT EXISTS idx_uid ON users (user_id)");

        CreateIndexStatement create = Assert.IsType<CreateIndexStatement>(statement);
        Assert.True(create.IfNotExists);
    }

    [Fact]
    public void CreateUniqueIndex()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE UNIQUE INDEX idx_users_email ON users (email)");

        CreateIndexStatement create = Assert.IsType<CreateIndexStatement>(statement);
        Assert.Equal("idx_users_email", create.IndexName);
        Assert.Equal("users", create.TableName);
        Assert.Equal(new[] { "email" }, create.Columns);
        Assert.True(create.IsUnique);
    }

    [Fact]
    public void CreateUniqueIndexWithIfNotExists()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_users_email ON users (email)");

        CreateIndexStatement create = Assert.IsType<CreateIndexStatement>(statement);
        Assert.True(create.IsUnique);
        Assert.True(create.IfNotExists);
    }

    [Fact]
    public void CreateIndexWithoutUniqueIsNotUnique()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE INDEX idx_users_email ON users (email)");

        CreateIndexStatement create = Assert.IsType<CreateIndexStatement>(statement);
        Assert.False(create.IsUnique);
    }

    [Fact]
    public void CreateIndex_NoUsingOrWith_MethodAndOptionsAreNull()
    {
        Statement statement = SqlParser.ParseStatement("CREATE INDEX idx_uid ON users (user_id)");

        CreateIndexStatement create = Assert.IsType<CreateIndexStatement>(statement);
        Assert.Null(create.Method);
        Assert.Null(create.Options);
    }

    [Fact]
    public void CreateIndex_UsingFts_CapturesMethodLowercased()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        CreateIndexStatement create = Assert.IsType<CreateIndexStatement>(statement);
        Assert.Equal("idx_msg_body", create.IndexName);
        Assert.Equal("messages", create.TableName);
        Assert.Equal(new[] { "body" }, create.Columns);
        Assert.Equal("fts", create.Method);
        Assert.Null(create.Options);
    }

    [Fact]
    public void CreateIndex_UsingFtsLowercase_StillMatches()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE INDEX idx_msg_body ON messages (body) using fts");

        CreateIndexStatement create = Assert.IsType<CreateIndexStatement>(statement);
        Assert.Equal("fts", create.Method);
    }

    [Fact]
    public void CreateIndex_UsingFtsWithAnalyzerOption_CapturesBoth()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE INDEX idx_msg_body ON messages (body) USING FTS WITH (analyzer = 'simple_en')");

        CreateIndexStatement create = Assert.IsType<CreateIndexStatement>(statement);
        Assert.Equal("fts", create.Method);
        Assert.NotNull(create.Options);
        Assert.Single(create.Options);
        Assert.Equal("simple_en", create.Options["analyzer"]);
    }

    [Fact]
    public void CreateIndex_WithMultipleOptions_CapturesAll()
    {
        // Multi-option round-trip — exercises the comma-delimited list shape
        // even though FTS only uses one option in v1.
        Statement statement = SqlParser.ParseStatement(
            "CREATE INDEX idx_x ON t (c) USING fts WITH (analyzer = 'simple_en', other = 'foo')");

        CreateIndexStatement create = Assert.IsType<CreateIndexStatement>(statement);
        Assert.NotNull(create.Options);
        Assert.Equal(2, create.Options.Count);
        Assert.Equal("simple_en", create.Options["analyzer"]);
        Assert.Equal("foo", create.Options["other"]);
    }

    [Fact]
    public void CreateIndex_OptionKeysAreCaseInsensitive()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE INDEX idx_x ON t (c) USING fts WITH (ANALYZER = 'simple_en')");

        CreateIndexStatement create = Assert.IsType<CreateIndexStatement>(statement);
        Assert.NotNull(create.Options);
        Assert.Equal("simple_en", create.Options["analyzer"]);
        Assert.Equal("simple_en", create.Options["ANALYZER"]); // case-insensitive lookup
    }

    [Fact]
    public void CreateIndex_OptionWithEscapedQuote_PreservesValue()
    {
        // Double-single-quote is the SQL string escape; parser must collapse it.
        Statement statement = SqlParser.ParseStatement(
            "CREATE INDEX idx_x ON t (c) USING fts WITH (k = 'foo''bar')");

        CreateIndexStatement create = Assert.IsType<CreateIndexStatement>(statement);
        Assert.Equal("foo'bar", create.Options!["k"]);
    }

    [Fact]
    public void DropIndex()
    {
        Statement statement = SqlParser.ParseStatement("DROP INDEX idx_uid");

        DropIndexStatement drop = Assert.IsType<DropIndexStatement>(statement);
        Assert.Equal("idx_uid", drop.IndexName);
        Assert.False(drop.IfExists);
    }

    [Fact]
    public void DropIndexIfExists()
    {
        Statement statement = SqlParser.ParseStatement("DROP INDEX IF EXISTS idx_uid");

        DropIndexStatement drop = Assert.IsType<DropIndexStatement>(statement);
        Assert.True(drop.IfExists);
    }

    // ───────────────────── Backward compatibility ─────────────────────

    [Fact]
    public void ParseStillReturnsQueryExpression()
    {
        QueryExpression query = SqlParser.Parse("SELECT 1");
        Assert.IsType<SelectQueryExpression>(query);
    }

    [Fact]
    public void SemicolonInSingleParseThrows()
    {
        Assert.Throws<ParseException>(() =>
            SqlParser.Parse("SELECT 1; SELECT 2"));
    }
}

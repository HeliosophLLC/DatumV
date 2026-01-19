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
    /// Computed column: <c>ALTER TABLE t ADD COLUMN ratio FLOAT64 AS revenue / cost</c>.
    /// </summary>
    [Fact]
    public void AlterTableAddColumnComputed()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE t ADD COLUMN ratio FLOAT64 AS revenue / cost");

        AlterTableAddColumnStatement alter = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.Equal("ratio", alter.ColumnName);
        Assert.Equal("FLOAT64", alter.TypeName);
        Assert.Null(alter.DefaultValue);
        Assert.NotNull(alter.ComputedExpression);
        BinaryExpression binary = Assert.IsType<BinaryExpression>(alter.ComputedExpression);
        Assert.Equal(BinaryOperator.Divide, binary.Operator);
    }

    /// <summary>
    /// Computed column with a function call: <c>AS UPPER(name)</c>.
    /// </summary>
    [Fact]
    public void AlterTableAddColumnComputedWithFunction()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE t ADD COLUMN upper_name STRING AS UPPER(name)");

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
            "ALTER TABLE t ADD COLUMN flag BOOLEAN NOT NULL AS x > 0");

        AlterTableAddColumnStatement alter = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.False(alter.Nullable);
        Assert.NotNull(alter.ComputedExpression);
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

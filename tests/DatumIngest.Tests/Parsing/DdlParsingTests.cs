using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// Tests for DDL and DML statement parsing: CREATE TEMP TABLE, DROP TABLE,
/// INSERT INTO, UPDATE, ALTER TABLE, and multi-statement batch parsing.
/// </summary>
public class DdlParsingTests
{
    // ───────────────────── CREATE TEMP TABLE ─────────────────────

    [Fact]
    public void CreateTempTableWithColumns()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE staging (id INT, name STRING, score FLOAT64)");

        CreateTempTableStatement create = Assert.IsType<CreateTempTableStatement>(statement);
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

        CreateTempTableStatement create = Assert.IsType<CreateTempTableStatement>(statement);
        Assert.Equal(2, create.Columns.Count);
        Assert.False(create.Columns[0].Nullable);
        Assert.True(create.Columns[1].Nullable);
    }

    [Fact]
    public void CreateTemporaryTableSynonym()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMPORARY TABLE t (x INT)");

        CreateTempTableStatement create = Assert.IsType<CreateTempTableStatement>(statement);
        Assert.Equal("t", create.TableName);
        Assert.Single(create.Columns);
    }

    [Fact]
    public void CreateTempTableIfNotExists()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE IF NOT EXISTS cache (key STRING, value STRING)");

        CreateTempTableStatement create = Assert.IsType<CreateTempTableStatement>(statement);
        Assert.True(create.IfNotExists);
        Assert.Equal("cache", create.TableName);
        Assert.Equal(2, create.Columns.Count);
    }

    [Fact]
    public void CreateTempTableAsSelect()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE filtered AS SELECT * FROM orders WHERE amount > 100");

        CreateTempTableAsSelectStatement create = Assert.IsType<CreateTempTableAsSelectStatement>(statement);
        Assert.Equal("filtered", create.TableName);
        Assert.False(create.IfNotExists);
        Assert.IsType<SelectQueryExpression>(create.Query);
    }

    [Fact]
    public void CreateTempTableAsSelectIfNotExists()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE IF NOT EXISTS summary AS SELECT category, COUNT(*) AS cnt FROM products GROUP BY category");

        CreateTempTableAsSelectStatement create = Assert.IsType<CreateTempTableAsSelectStatement>(statement);
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
        Assert.Equal(0.0, literal.Value);
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
        Assert.IsType<CreateTempTableStatement>(statements[0]);
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
        Assert.IsType<CreateTempTableStatement>(statements[0]);
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
        Assert.IsType<CreateTempTableAsSelectStatement>(statements[0]);
        Assert.IsType<QueryStatement>(statements[1]);
    }

    // ───────────────────── Error cases ─────────────────────

    [Fact]
    public void CreateWithoutTempRejects()
    {
        Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement("CREATE TABLE t (x INT)"));
    }

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

        CreateTempTableStatement create = Assert.IsType<CreateTempTableStatement>(statement);
        Assert.Equal(2, create.Columns.Count);
        Assert.True(create.Columns[0].PrimaryKey);
        Assert.False(create.Columns[1].PrimaryKey);
    }

    /// <summary>
    /// A PRIMARY KEY column is implicitly NOT NULL even without an explicit NOT NULL keyword.
    /// </summary>
    [Fact]
    public void CreateTempTable_PrimaryKeyImpliesNotNull()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TEMP TABLE orders (id INT PRIMARY KEY, label STRING)");

        CreateTempTableStatement create = Assert.IsType<CreateTempTableStatement>(statement);
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

        CreateTempTableStatement create = Assert.IsType<CreateTempTableStatement>(statement);
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

        CreateTempTableStatement create = Assert.IsType<CreateTempTableStatement>(statement);
        Assert.Equal(3, create.Columns.Count);
        Assert.Equal("primary", create.Columns[0].Name);
        Assert.Equal("key", create.Columns[1].Name);
        Assert.Equal("value", create.Columns[2].Name);
        Assert.False(create.Columns[0].PrimaryKey);
        Assert.False(create.Columns[1].PrimaryKey);
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

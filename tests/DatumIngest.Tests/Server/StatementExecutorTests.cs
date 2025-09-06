using DatumIngest.Catalog;
using DatumIngest.DatumFile;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Server;

namespace DatumIngest.Tests.Server;

/// <summary>
/// End-to-end tests for DDL/DML statement execution through <see cref="CommandDispatcher"/>.
/// Each test issues SQL statements against a session and verifies catalog state,
/// file contents, and query results.
/// </summary>
public sealed class StatementExecutorTests : IDisposable
{
    private readonly FunctionRegistry _functionRegistry = FunctionRegistry.CreateDefault();
    private readonly SessionManager _sessionManager;
    private readonly CommandDispatcher _dispatcher;
    private readonly Session _session;

    /// <summary>
    /// Sets up a dispatcher with a session that has no external data sources.
    /// </summary>
    public StatementExecutorTests()
    {
        _sessionManager = new SessionManager(_functionRegistry);
        TableCatalog catalog = new();
        _session = _sessionManager.CreateLocalSession(SessionRole.Admin, catalog);
        _dispatcher = new CommandDispatcher(_sessionManager);
    }

    /// <summary>
    /// Disposes test resources and cleans up temp files.
    /// </summary>
    public void Dispose()
    {
        _session.Dispose();

        string tempDirectory = Path.Combine(Path.GetTempPath(), $"datum_session_{_session.SessionId:N}");
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private async Task<CommandResult> ExecuteAsync(string sql)
    {
        return await _dispatcher.DispatchAsync(_session, sql, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<List<Row>> CollectRowsAsync(CommandResult result)
    {
        List<Row> rows = new();
        if (result.Rows is null) return rows;

        await foreach (RowBatch batch in result.Rows)
        {
            for (int index = 0; index < batch.Count; index++)
            {
                rows.Add(batch[index]);
            }
        }

        return rows;
    }

    // ──────────────────── CREATE TEMP TABLE ────────────────────

    /// <summary>
    /// CREATE TEMP TABLE creates an empty table that can be queried.
    /// </summary>
    [Fact]
    public async Task CreateTempTable_CreatesQueryableEmptyTable()
    {
        CommandResult createResult = await ExecuteAsync(
            "CREATE TEMP TABLE staging (id INT, name STRING)");

        Assert.Equal(CommandResultKind.AffectedRows, createResult.Kind);
        Assert.Equal(0, createResult.AffectedRowCount);

        // Table should be registered in the catalog and queryable.
        CommandResult selectResult = await ExecuteAsync("SELECT * FROM staging");
        Assert.Equal(CommandResultKind.StreamingRows, selectResult.Kind);

        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Empty(rows);
    }

    /// <summary>
    /// CREATE TEMP TABLE with IF NOT EXISTS does not fail for duplicate names.
    /// </summary>
    [Fact]
    public async Task CreateTempTable_IfNotExists_SupressesDuplicate()
    {
        await ExecuteAsync("CREATE TEMP TABLE staging (id INT)");
        CommandResult secondResult = await ExecuteAsync(
            "CREATE TEMP TABLE IF NOT EXISTS staging (id INT)");

        Assert.Equal(CommandResultKind.AffectedRows, secondResult.Kind);
        Assert.Equal(0, secondResult.AffectedRowCount);
    }

    /// <summary>
    /// CREATE TEMP TABLE without IF NOT EXISTS returns an error for duplicate names.
    /// </summary>
    [Fact]
    public async Task CreateTempTable_Duplicate_ReturnsError()
    {
        await ExecuteAsync("CREATE TEMP TABLE staging (id INT)");
        CommandResult secondResult = await ExecuteAsync("CREATE TEMP TABLE staging (id INT)");

        Assert.Equal(CommandResultKind.Error, secondResult.Kind);
        Assert.Contains("already exists", secondResult.Message);
    }

    // ──────────────────── DROP TABLE ────────────────────

    /// <summary>
    /// DROP TABLE removes the table from the catalog.
    /// </summary>
    [Fact]
    public async Task DropTable_RemovesTableFromCatalog()
    {
        await ExecuteAsync("CREATE TEMP TABLE staging (id INT)");
        CommandResult dropResult = await ExecuteAsync("DROP TABLE staging");

        Assert.Equal(CommandResultKind.AffectedRows, dropResult.Kind);

        // Querying the dropped table should fail.
        CommandResult selectResult = await ExecuteAsync("SELECT * FROM staging");
        Assert.Equal(CommandResultKind.Error, selectResult.Kind);
    }

    /// <summary>
    /// DROP TABLE IF EXISTS succeeds silently when the table does not exist.
    /// </summary>
    [Fact]
    public async Task DropTable_IfExists_SilentWhenMissing()
    {
        CommandResult dropResult = await ExecuteAsync("DROP TABLE IF EXISTS nonexistent");

        Assert.Equal(CommandResultKind.AffectedRows, dropResult.Kind);
        Assert.Equal(0, dropResult.AffectedRowCount);
    }

    /// <summary>
    /// DROP TABLE without IF EXISTS returns an error when the table does not exist.
    /// </summary>
    [Fact]
    public async Task DropTable_Missing_ReturnsError()
    {
        CommandResult dropResult = await ExecuteAsync("DROP TABLE nonexistent");

        Assert.Equal(CommandResultKind.Error, dropResult.Kind);
        Assert.Contains("does not exist", dropResult.Message);
    }

    // ──────────────────── INSERT INTO ... VALUES ────────────────────

    /// <summary>
    /// INSERT INTO with VALUES adds rows that can be queried back.
    /// </summary>
    [Fact]
    public async Task InsertValues_AddsQueryableRows()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, name STRING)");
        CommandResult insertResult = await ExecuteAsync(
            "INSERT INTO data VALUES (1, 'Alice'), (2, 'Bob')");

        Assert.Equal(CommandResultKind.AffectedRows, insertResult.Kind);
        Assert.Equal(2, insertResult.AffectedRowCount);

        CommandResult selectResult = await ExecuteAsync("SELECT id, name FROM data");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(2, rows.Count);

        Assert.Equal(1, rows[0]["id"].AsInt32());
        Assert.Equal("Alice", rows[0]["name"].AsString());
        Assert.Equal(2, rows[1]["id"].AsInt32());
        Assert.Equal("Bob", rows[1]["name"].AsString());
    }

    /// <summary>
    /// INSERT INTO with column list maps values to the correct columns.
    /// </summary>
    [Fact]
    public async Task InsertValues_WithColumnList_MapsCorrectly()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, name STRING, score FLOAT64)");
        CommandResult insertResult = await ExecuteAsync(
            "INSERT INTO data (name, id) VALUES ('Alice', 1)");

        Assert.Equal(CommandResultKind.AffectedRows, insertResult.Kind);
        Assert.Equal(1, insertResult.AffectedRowCount);

        CommandResult selectResult = await ExecuteAsync("SELECT id, name, score FROM data");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Single(rows);

        Assert.Equal(1, rows[0]["id"].AsInt32());
        Assert.Equal("Alice", rows[0]["name"].AsString());
        Assert.True(rows[0]["score"].IsNull);
    }

    /// <summary>
    /// INSERT INTO with NULL values stores nulls correctly.
    /// </summary>
    [Fact]
    public async Task InsertValues_WithNull_StoresNullCorrectly()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, name STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, NULL)");

        CommandResult selectResult = await ExecuteAsync("SELECT id, name FROM data");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Single(rows);
        Assert.Equal(1, rows[0]["id"].AsInt32());
        Assert.True(rows[0]["name"].IsNull);
    }

    /// <summary>
    /// INSERT INTO with negative numeric literals handles unary minus.
    /// </summary>
    [Fact]
    public async Task InsertValues_WithNegativeNumber_HandlesUnaryMinus()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (value FLOAT64)");
        await ExecuteAsync("INSERT INTO data VALUES (-3.14)");

        CommandResult selectResult = await ExecuteAsync("SELECT value FROM data");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Single(rows);
        Assert.Equal(-3.14, rows[0]["value"].AsFloat64(), precision: 10);
    }

    /// <summary>
    /// INSERT INTO a nonexistent table returns an error.
    /// </summary>
    [Fact]
    public async Task Insert_IntoMissingTable_ReturnsError()
    {
        CommandResult result = await ExecuteAsync(
            "INSERT INTO nonexistent VALUES (1)");

        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("does not exist", result.Message);
    }

    // ──────────────────── INSERT INTO ... SELECT ────────────────────

    /// <summary>
    /// INSERT INTO ... SELECT materializes query results into the target table.
    /// </summary>
    [Fact]
    public async Task InsertSelect_MaterializesQueryResults()
    {
        await ExecuteAsync("CREATE TEMP TABLE source (id INT, name STRING)");
        await ExecuteAsync("INSERT INTO source VALUES (1, 'A'), (2, 'B'), (3, 'C')");

        await ExecuteAsync("CREATE TEMP TABLE target (id INT, name STRING)");
        CommandResult insertResult = await ExecuteAsync(
            "INSERT INTO target SELECT id, name FROM source WHERE id > 1");

        Assert.Equal(CommandResultKind.AffectedRows, insertResult.Kind);
        Assert.Equal(2, insertResult.AffectedRowCount);

        CommandResult selectResult = await ExecuteAsync("SELECT id, name FROM target");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(2, rows.Count);
    }

    // ──────────────────── UPDATE ────────────────────

    /// <summary>
    /// UPDATE with constant assignment replaces all values in the column.
    /// </summary>
    [Fact]
    public async Task Update_ConstantAssignment_ReplacesAllValues()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, status STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, 'pending'), (2, 'pending')");

        CommandResult updateResult = await ExecuteAsync(
            "UPDATE data SET status = 'done'");

        Assert.Equal(CommandResultKind.AffectedRows, updateResult.Kind);
        Assert.Equal(2, updateResult.AffectedRowCount);

        CommandResult selectResult = await ExecuteAsync("SELECT id, status FROM data");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("done", row["status"].AsString()));
    }

    // ──────────────────── ALTER TABLE ADD COLUMN ────────────────────

    /// <summary>
    /// ALTER TABLE ADD COLUMN adds a nullable column filled with nulls.
    /// </summary>
    [Fact]
    public async Task AlterTableAddColumn_AddsNullableColumn()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT)");
        await ExecuteAsync("INSERT INTO data VALUES (1), (2)");

        CommandResult alterResult = await ExecuteAsync(
            "ALTER TABLE data ADD COLUMN note STRING");

        Assert.Equal(CommandResultKind.AffectedRows, alterResult.Kind);

        CommandResult selectResult = await ExecuteAsync("SELECT id, note FROM data");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0]["note"].IsNull);
        Assert.True(rows[1]["note"].IsNull);
    }

    /// <summary>
    /// ALTER TABLE ADD COLUMN with a default value fills existing rows with that value.
    /// </summary>
    [Fact]
    public async Task AlterTableAddColumn_WithDefault_FillsExistingRows()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT)");
        await ExecuteAsync("INSERT INTO data VALUES (1), (2)");

        CommandResult alterResult = await ExecuteAsync(
            "ALTER TABLE data ADD COLUMN active BOOLEAN DEFAULT true");

        Assert.Equal(CommandResultKind.AffectedRows, alterResult.Kind);

        CommandResult selectResult = await ExecuteAsync("SELECT id, active FROM data");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0]["active"].AsBoolean());
        Assert.True(rows[1]["active"].AsBoolean());
    }

    /// <summary>
    /// ALTER TABLE on a nonexistent table returns an error.
    /// </summary>
    [Fact]
    public async Task AlterTable_MissingTable_ReturnsError()
    {
        CommandResult result = await ExecuteAsync(
            "ALTER TABLE nonexistent ADD COLUMN x INT");

        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("does not exist", result.Message);
    }

    // ──────────────────── CREATE TEMP TABLE AS SELECT ────────────────────

    /// <summary>
    /// CREATE TEMP TABLE AS SELECT materializes query results into a new table.
    /// </summary>
    [Fact]
    public async Task CreateTempTableAsSelect_MaterializesResults()
    {
        // Set up a source table with data.
        await ExecuteAsync("CREATE TEMP TABLE source (id INT, name STRING)");
        await ExecuteAsync("INSERT INTO source VALUES (1, 'A'), (2, 'B'), (3, 'C')");

        CommandResult createResult = await ExecuteAsync(
            "CREATE TEMP TABLE derived AS SELECT id, name FROM source WHERE id <= 2");

        Assert.Equal(CommandResultKind.AffectedRows, createResult.Kind);
        Assert.Equal(2, createResult.AffectedRowCount);

        CommandResult selectResult = await ExecuteAsync("SELECT * FROM derived");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(2, rows.Count);
    }

    // ──────────────────── Multiple operations ────────────────────

    /// <summary>
    /// A sequence of DDL/DML operations works correctly together:
    /// create, insert, update, alter, query, drop.
    /// </summary>
    [Fact]
    public async Task MultipleOperations_EndToEnd()
    {
        await ExecuteAsync("CREATE TEMP TABLE inventory (item STRING, quantity INT)");
        await ExecuteAsync("INSERT INTO inventory VALUES ('Widget', 10), ('Gadget', 5)");
        await ExecuteAsync("UPDATE inventory SET quantity = 99");
        await ExecuteAsync("ALTER TABLE inventory ADD COLUMN warehouse STRING DEFAULT 'Main'");

        CommandResult selectResult = await ExecuteAsync(
            "SELECT item, quantity, warehouse FROM inventory");
        List<Row> rows = await CollectRowsAsync(selectResult);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(99, row["quantity"].AsInt32());
            Assert.Equal("Main", row["warehouse"].AsString());
        });

        CommandResult dropResult = await ExecuteAsync("DROP TABLE inventory");
        Assert.Equal(CommandResultKind.AffectedRows, dropResult.Kind);
    }

    // ──────────────────── DELETE ────────────────────

    /// <summary>
    /// DELETE FROM without WHERE deletes all rows.
    /// </summary>
    [Fact]
    public async Task DeleteAll_RemovesAllRows()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, name STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, 'Alice'), (2, 'Bob'), (3, 'Carol')");

        CommandResult deleteResult = await ExecuteAsync("DELETE FROM data");

        Assert.Equal(CommandResultKind.AffectedRows, deleteResult.Kind);
        Assert.Equal(3, deleteResult.AffectedRowCount);

        CommandResult selectResult = await ExecuteAsync("SELECT * FROM data");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Empty(rows);
    }

    /// <summary>
    /// DELETE FROM with WHERE deletes only matching rows.
    /// </summary>
    [Fact]
    public async Task DeleteWithWhere_RemovesMatchingRows()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, name STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, 'Alice'), (2, 'Bob'), (3, 'Carol')");

        CommandResult deleteResult = await ExecuteAsync("DELETE FROM data WHERE id = 2");

        Assert.Equal(CommandResultKind.AffectedRows, deleteResult.Kind);
        Assert.Equal(1, deleteResult.AffectedRowCount);

        CommandResult selectResult = await ExecuteAsync("SELECT id, name FROM data");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0]["id"].AsInt32());
        Assert.Equal(3, rows[1]["id"].AsInt32());
    }

    /// <summary>
    /// DELETE FROM with WHERE that matches no rows reports zero affected.
    /// </summary>
    [Fact]
    public async Task DeleteWithWhere_NoMatch_ZeroAffected()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT)");
        await ExecuteAsync("INSERT INTO data VALUES (1), (2), (3)");

        CommandResult deleteResult = await ExecuteAsync("DELETE FROM data WHERE id = 99");

        Assert.Equal(CommandResultKind.AffectedRows, deleteResult.Kind);
        Assert.Equal(0, deleteResult.AffectedRowCount);

        CommandResult selectResult = await ExecuteAsync("SELECT * FROM data");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(3, rows.Count);
    }

    /// <summary>
    /// DELETE FROM a nonexistent table returns an error.
    /// </summary>
    [Fact]
    public async Task Delete_MissingTable_ReturnsError()
    {
        CommandResult result = await ExecuteAsync("DELETE FROM nonexistent");

        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("does not exist", result.Message);
    }

    /// <summary>
    /// INSERT after DELETE appends new rows alongside tombstoned ones.
    /// </summary>
    [Fact]
    public async Task InsertAfterDelete_AppendsCorrectly()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, name STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, 'Alice'), (2, 'Bob')");
        await ExecuteAsync("DELETE FROM data WHERE id = 1");
        await ExecuteAsync("INSERT INTO data VALUES (3, 'Carol')");

        CommandResult selectResult = await ExecuteAsync("SELECT id, name FROM data ORDER BY id");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0]["id"].AsInt32());
        Assert.Equal(3, rows[1]["id"].AsInt32());
    }

    /// <summary>
    /// Multiple deletes work correctly — second delete on already-sparse data.
    /// </summary>
    [Fact]
    public async Task MultipleDeletes_WorkCorrectly()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT)");
        await ExecuteAsync("INSERT INTO data VALUES (1), (2), (3), (4), (5)");

        await ExecuteAsync("DELETE FROM data WHERE id <= 2");
        await ExecuteAsync("DELETE FROM data WHERE id = 4");

        CommandResult selectResult = await ExecuteAsync("SELECT id FROM data ORDER BY id");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(2, rows.Count);
        Assert.Equal(3, rows[0]["id"].AsInt32());
        Assert.Equal(5, rows[1]["id"].AsInt32());
    }

    // ──────────────────── Regular SELECT still works ────────────────────

    /// <summary>
    /// Regular SELECT queries continue to work through the updated dispatcher.
    /// </summary>
    [Fact]
    public async Task RegularSelect_StillWorksAfterDispatcherChange()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (x INT)");
        await ExecuteAsync("INSERT INTO data VALUES (1), (2), (3)");

        CommandResult selectResult = await ExecuteAsync("SELECT x FROM data");
        Assert.Equal(CommandResultKind.StreamingRows, selectResult.Kind);

        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(3, rows.Count);
    }
}

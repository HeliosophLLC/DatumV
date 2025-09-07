using DatumIngest.Catalog;
using DatumIngest.DatumFile;
using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Manifest;
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

    // ──────────────────── PRIMARY KEY ENFORCEMENT ────────────────────

    /// <summary>
    /// INSERT with a single-column PRIMARY KEY succeeds when keys are unique.
    /// </summary>
    [Fact]
    public async Task Insert_SingleColumnPrimaryKey_Succeeds()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT PRIMARY KEY, name STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, 'A'), (2, 'B')");

        CommandResult selectResult = await ExecuteAsync("SELECT id, name FROM data ORDER BY id");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0]["id"].AsInt32());
        Assert.Equal(2, rows[1]["id"].AsInt32());
    }

    /// <summary>
    /// INSERT with a duplicate single-column PK value within the same batch returns an error.
    /// </summary>
    [Fact]
    public async Task Insert_DuplicateInBatch_ReturnsError()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT PRIMARY KEY, name STRING)");

        CommandResult result = await ExecuteAsync(
            "INSERT INTO data VALUES (1, 'A'), (1, 'B')");

        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("PRIMARY KEY violation", result.Message);
    }

    /// <summary>
    /// INSERT with a duplicate value against existing rows returns an error.
    /// </summary>
    [Fact]
    public async Task Insert_DuplicateAgainstExisting_ReturnsError()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT PRIMARY KEY, name STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, 'A')");

        CommandResult result = await ExecuteAsync(
            "INSERT INTO data VALUES (2, 'B'), (1, 'C')");

        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("PRIMARY KEY violation", result.Message);
    }

    /// <summary>
    /// Composite PRIMARY KEY enforces uniqueness on the combination of columns.
    /// </summary>
    [Fact]
    public async Task Insert_CompositePrimaryKey_EnforcesUniqueness()
    {
        await ExecuteAsync(
            "CREATE TEMP TABLE data (user_id INT, product_id INT, quantity INT, PRIMARY KEY (user_id, product_id))");

        await ExecuteAsync("INSERT INTO data VALUES (1, 100, 5), (1, 200, 3)");

        CommandResult result = await ExecuteAsync(
            "INSERT INTO data VALUES (2, 100, 1), (1, 100, 7)");

        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("PRIMARY KEY violation", result.Message);
    }

    /// <summary>
    /// Composite PRIMARY KEY allows rows where individual columns repeat but the combination is unique.
    /// </summary>
    [Fact]
    public async Task Insert_CompositePrimaryKey_AllowsPartialOverlap()
    {
        await ExecuteAsync(
            "CREATE TEMP TABLE data (user_id INT, product_id INT, quantity INT, PRIMARY KEY (user_id, product_id))");

        await ExecuteAsync("INSERT INTO data VALUES (1, 100, 5)");

        CommandResult result = await ExecuteAsync(
            "INSERT INTO data VALUES (1, 200, 3), (2, 100, 1)");

        Assert.Equal(CommandResultKind.AffectedRows, result.Kind);

        CommandResult selectResult = await ExecuteAsync("SELECT user_id, product_id FROM data ORDER BY user_id, product_id");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(3, rows.Count);
    }

    /// <summary>
    /// INSERT INTO ... SELECT also enforces the primary key constraint.
    /// </summary>
    [Fact]
    public async Task InsertSelect_DuplicatePrimaryKey_ReturnsError()
    {
        await ExecuteAsync("CREATE TEMP TABLE source (id INT, name STRING)");
        await ExecuteAsync("INSERT INTO source VALUES (1, 'A'), (2, 'B')");

        await ExecuteAsync("CREATE TEMP TABLE target (id INT PRIMARY KEY, name STRING)");
        await ExecuteAsync("INSERT INTO target VALUES (1, 'X')");

        CommandResult result = await ExecuteAsync(
            "INSERT INTO target SELECT id, name FROM source");

        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("PRIMARY KEY violation", result.Message);
    }

    /// <summary>
    /// A table with no PRIMARY KEY allows duplicate values freely.
    /// </summary>
    [Fact]
    public async Task Insert_NoPrimaryKey_AllowsDuplicates()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, name STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, 'A'), (1, 'B')");

        CommandResult selectResult = await ExecuteAsync("SELECT id FROM data");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(2, rows.Count);
    }

    /// <summary>
    /// INSERT with NULL in a NOT NULL column returns an error.
    /// </summary>
    [Fact]
    public async Task Insert_NullIntoNotNullColumn_ReturnsError()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT NOT NULL, name STRING)");

        CommandResult result = await ExecuteAsync(
            "INSERT INTO data VALUES (NULL, 'A')");

        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("NOT NULL", result.Message);
        Assert.Contains("id", result.Message);
    }

    /// <summary>
    /// INSERT with NULL in a PRIMARY KEY column returns a NOT NULL error
    /// because PRIMARY KEY implies NOT NULL.
    /// </summary>
    [Fact]
    public async Task Insert_NullIntoPrimaryKeyColumn_ReturnsError()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT PRIMARY KEY, name STRING)");

        CommandResult result = await ExecuteAsync(
            "INSERT INTO data VALUES (NULL, 'A')");

        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("NOT NULL", result.Message);
    }

    /// <summary>
    /// INSERT with NULL in a nullable column succeeds normally.
    /// </summary>
    [Fact]
    public async Task Insert_NullIntoNullableColumn_Succeeds()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT NOT NULL, name STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, NULL)");

        CommandResult selectResult = await ExecuteAsync("SELECT id, name FROM data");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Row row = Assert.Single(rows);
        Assert.True(row["name"].IsNull);
    }

    /// <summary>
    /// INSERT INTO ... SELECT with NULL in a NOT NULL column returns an error.
    /// </summary>
    [Fact]
    public async Task InsertSelect_NullIntoNotNullColumn_ReturnsError()
    {
        await ExecuteAsync("CREATE TEMP TABLE source (id INT, name STRING)");
        await ExecuteAsync("INSERT INTO source VALUES (NULL, 'A')");

        await ExecuteAsync("CREATE TEMP TABLE target (id INT NOT NULL, name STRING)");

        CommandResult result = await ExecuteAsync(
            "INSERT INTO target SELECT id, name FROM source");

        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("NOT NULL", result.Message);
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

    /// <summary>
    /// UPDATE targeting a PRIMARY KEY column returns an error.
    /// </summary>
    [Fact]
    public async Task Update_PrimaryKeyColumn_ReturnsError()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT PRIMARY KEY, name STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, 'A'), (2, 'B')");

        CommandResult result = await ExecuteAsync("UPDATE data SET id = 99");

        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("primary key column", result.Message);
        Assert.Contains("id", result.Message);
    }

    /// <summary>
    /// UPDATE targeting a non-PK column on a table with a PRIMARY KEY succeeds.
    /// </summary>
    [Fact]
    public async Task Update_NonPrimaryKeyColumn_Succeeds()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT PRIMARY KEY, name STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, 'A'), (2, 'B')");

        CommandResult result = await ExecuteAsync("UPDATE data SET name = 'X'");

        Assert.Equal(CommandResultKind.AffectedRows, result.Kind);
        Assert.Equal(2, result.AffectedRowCount);
    }

    /// <summary>
    /// UPDATE with an expression assignment evaluates the expression for each row.
    /// </summary>
    [Fact]
    public async Task Update_ExpressionAssignment_EvaluatesPerRow()
    {
        await ExecuteAsync("CREATE TEMP TABLE scores (id INT, score DOUBLE)");
        await ExecuteAsync("INSERT INTO scores VALUES (1, 10.0), (2, 20.0)");

        CommandResult updateResult = await ExecuteAsync("UPDATE scores SET score = score * 2.0");

        Assert.Equal(CommandResultKind.AffectedRows, updateResult.Kind);
        Assert.Equal(2, updateResult.AffectedRowCount);

        List<Row> rows = await CollectRowsAsync(await ExecuteAsync("SELECT id, score FROM scores ORDER BY id"));
        Assert.Equal(2, rows.Count);
        Assert.Equal(20.0, rows[0]["score"].AsFloat64());
        Assert.Equal(40.0, rows[1]["score"].AsFloat64());
    }

    /// <summary>
    /// UPDATE with a WHERE clause only modifies rows matching the predicate.
    /// </summary>
    [Fact]
    public async Task Update_WithWhere_OnlyUpdatesMatchingRows()
    {
        await ExecuteAsync("CREATE TEMP TABLE items (id INT, status STRING)");
        await ExecuteAsync("INSERT INTO items VALUES (1, 'new'), (2, 'new'), (3, 'done')");

        CommandResult updateResult = await ExecuteAsync(
            "UPDATE items SET status = 'processed' WHERE status = 'new'");

        Assert.Equal(CommandResultKind.AffectedRows, updateResult.Kind);
        Assert.Equal(2, updateResult.AffectedRowCount);

        List<Row> rows = await CollectRowsAsync(await ExecuteAsync("SELECT id, status FROM items ORDER BY id"));
        Assert.Equal(3, rows.Count);
        Assert.Equal("processed", rows[0]["status"].AsString());
        Assert.Equal("processed", rows[1]["status"].AsString());
        Assert.Equal("done", rows[2]["status"].AsString());
    }

    /// <summary>
    /// UPDATE SET col1 = col2 copies values across columns.
    /// </summary>
    [Fact]
    public async Task Update_CrossColumnAssignment_CopiesValues()
    {
        await ExecuteAsync("CREATE TEMP TABLE audit (id INT, current_name STRING, backup_name STRING)");
        await ExecuteAsync("INSERT INTO audit VALUES (1, 'Alice', ''), (2, 'Bob', '')");

        await ExecuteAsync("UPDATE audit SET backup_name = current_name");

        List<Row> rows = await CollectRowsAsync(await ExecuteAsync("SELECT id, backup_name FROM audit ORDER BY id"));
        Assert.Equal("Alice", rows[0]["backup_name"].AsString());
        Assert.Equal("Bob", rows[1]["backup_name"].AsString());
    }

    /// <summary>
    /// UPDATE with a WHERE predicate that matches no rows updates nothing.
    /// </summary>
    [Fact]
    public async Task Update_NoMatchingWhere_UpdatesZeroRows()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, value DOUBLE)");
        await ExecuteAsync("INSERT INTO data VALUES (1, 5.0), (2, 10.0)");

        CommandResult result = await ExecuteAsync("UPDATE data SET value = 999.0 WHERE id = 99");

        Assert.Equal(CommandResultKind.AffectedRows, result.Kind);
        Assert.Equal(0, result.AffectedRowCount);

        // Values must be unchanged.
        List<Row> rows = await CollectRowsAsync(await ExecuteAsync("SELECT id, value FROM data ORDER BY id"));
        Assert.Equal(5.0, rows[0]["value"].AsFloat64());
        Assert.Equal(10.0, rows[1]["value"].AsFloat64());
    }

    // ──────────────────── UPDATE...FROM ────────────────────

    /// <summary>
    /// UPDATE...FROM enriches target rows using matching rows from a source table.
    /// </summary>
    [Fact]
    public async Task UpdateFrom_BasicJoin_EnrichesTargetRows()
    {
        await ExecuteAsync("CREATE TEMP TABLE features (id INT, score DOUBLE)");
        await ExecuteAsync("INSERT INTO features VALUES (1, 0.0), (2, 0.0), (3, 0.0)");

        await ExecuteAsync("CREATE TEMP TABLE raw (id INT, value DOUBLE)");
        await ExecuteAsync("INSERT INTO raw VALUES (1, 10.5), (2, 20.5)");

        CommandResult updateResult = await ExecuteAsync(
            "UPDATE features SET score = raw.value FROM raw WHERE features.id = raw.id");

        Assert.Equal(CommandResultKind.AffectedRows, updateResult.Kind);
        Assert.Equal(2, updateResult.AffectedRowCount);

        List<Row> rows = await CollectRowsAsync(await ExecuteAsync("SELECT id, score FROM features ORDER BY id"));
        Assert.Equal(3, rows.Count);
        Assert.Equal(10.5, rows[0]["score"].AsFloat64());
        Assert.Equal(20.5, rows[1]["score"].AsFloat64());
        // Row id=3 had no match: score stays 0.0.
        Assert.Equal(0.0, rows[2]["score"].AsFloat64());
    }

    /// <summary>
    /// UPDATE...FROM with an explicit alias on the target table resolves target
    /// columns through the alias.
    /// </summary>
    [Fact]
    public async Task UpdateFrom_WithAlias_ResolvesTargetColumnsViaAlias()
    {
        await ExecuteAsync("CREATE TEMP TABLE features (id INT, score DOUBLE)");
        await ExecuteAsync("INSERT INTO features VALUES (1, 0.0), (2, 0.0)");

        await ExecuteAsync("CREATE TEMP TABLE raw (id INT, value DOUBLE)");
        await ExecuteAsync("INSERT INTO raw VALUES (1, 42.0), (2, 84.0)");

        CommandResult updateResult = await ExecuteAsync(
            "UPDATE features AS f SET score = raw.value FROM raw WHERE f.id = raw.id");

        Assert.Equal(CommandResultKind.AffectedRows, updateResult.Kind);
        Assert.Equal(2, updateResult.AffectedRowCount);

        List<Row> rows = await CollectRowsAsync(await ExecuteAsync("SELECT id, score FROM features ORDER BY id"));
        Assert.Equal(42.0, rows[0]["score"].AsFloat64());
        Assert.Equal(84.0, rows[1]["score"].AsFloat64());
    }

    /// <summary>
    /// UPDATE...FROM with a WHERE that matches no source rows updates nothing.
    /// </summary>
    [Fact]
    public async Task UpdateFrom_NoMatch_UpdatesZeroRows()
    {
        await ExecuteAsync("CREATE TEMP TABLE features (id INT, score DOUBLE)");
        await ExecuteAsync("INSERT INTO features VALUES (1, 5.0)");

        await ExecuteAsync("CREATE TEMP TABLE raw (id INT, value DOUBLE)");
        await ExecuteAsync("INSERT INTO raw VALUES (99, 100.0)");

        CommandResult result = await ExecuteAsync(
            "UPDATE features SET score = raw.value FROM raw WHERE features.id = raw.id");

        Assert.Equal(CommandResultKind.AffectedRows, result.Kind);
        Assert.Equal(0, result.AffectedRowCount);

        List<Row> rows = await CollectRowsAsync(await ExecuteAsync("SELECT score FROM features"));
        Assert.Equal(5.0, rows[0]["score"].AsFloat64());
    }

    /// <summary>
    /// UPDATE...FROM SET expression can apply arithmetic using the source column value.
    /// </summary>
    [Fact]
    public async Task UpdateFrom_ExpressionInSet_AppliesArithmetic()
    {
        await ExecuteAsync("CREATE TEMP TABLE products (id INT, price DOUBLE)");
        await ExecuteAsync("INSERT INTO products VALUES (1, 100.0), (2, 200.0)");

        await ExecuteAsync("CREATE TEMP TABLE discounts (id INT, factor DOUBLE)");
        await ExecuteAsync("INSERT INTO discounts VALUES (1, 0.9), (2, 0.75)");

        await ExecuteAsync(
            "UPDATE products SET price = products.price * discounts.factor FROM discounts WHERE products.id = discounts.id");

        List<Row> rows = await CollectRowsAsync(await ExecuteAsync("SELECT id, price FROM products ORDER BY id"));
        Assert.Equal(90.0, rows[0]["price"].AsFloat64());
        Assert.Equal(150.0, rows[1]["price"].AsFloat64());
    }

    /// <summary>
    /// UPDATE...FROM can set multiple columns in a single statement.
    /// </summary>
    [Fact]
    public async Task UpdateFrom_MultipleSetColumns_UpdatesAllColumns()
    {
        await ExecuteAsync("CREATE TEMP TABLE features (id INT, a DOUBLE, b DOUBLE)");
        await ExecuteAsync("INSERT INTO features VALUES (1, 0.0, 0.0), (2, 0.0, 0.0)");

        await ExecuteAsync("CREATE TEMP TABLE src (id INT, x DOUBLE, y DOUBLE)");
        await ExecuteAsync("INSERT INTO src VALUES (1, 10.0, 20.0), (2, 30.0, 40.0)");

        await ExecuteAsync(
            "UPDATE features SET a = src.x, b = src.y FROM src WHERE features.id = src.id");

        List<Row> rows = await CollectRowsAsync(await ExecuteAsync("SELECT id, a, b FROM features ORDER BY id"));
        Assert.Equal(10.0, rows[0]["a"].AsFloat64());
        Assert.Equal(20.0, rows[0]["b"].AsFloat64());
        Assert.Equal(30.0, rows[1]["a"].AsFloat64());
        Assert.Equal(40.0, rows[1]["b"].AsFloat64());
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

    /// <summary>
    /// ALTER TABLE ADD COLUMN with a computed expression evaluates the expression
    /// against existing rows and persists the results.
    /// </summary>
    [Fact]
    public async Task AlterTableAddColumn_Computed_EvaluatesExpression()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (price FLOAT64, quantity FLOAT64)");
        await ExecuteAsync("INSERT INTO data VALUES (10.0, 3.0), (25.0, 2.0)");

        CommandResult alterResult = await ExecuteAsync(
            "ALTER TABLE data ADD COLUMN total FLOAT64 AS price * quantity");

        Assert.Equal(CommandResultKind.AffectedRows, alterResult.Kind);

        CommandResult selectResult = await ExecuteAsync("SELECT price, quantity, total FROM data");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(2, rows.Count);
        Assert.Equal(30.0, rows[0]["total"].AsFloat64(), precision: 5);
        Assert.Equal(50.0, rows[1]["total"].AsFloat64(), precision: 5);
    }

    /// <summary>
    /// Computed column with integer arithmetic coerces the result to the declared type.
    /// </summary>
    [Fact]
    public async Task AlterTableAddColumn_Computed_CoercesToDeclaredType()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (a INT, b INT)");
        await ExecuteAsync("INSERT INTO data VALUES (10, 3), (20, 4)");

        CommandResult alterResult = await ExecuteAsync(
            "ALTER TABLE data ADD COLUMN sum_ab INT AS a + b");

        Assert.Equal(CommandResultKind.AffectedRows, alterResult.Kind);

        CommandResult selectResult = await ExecuteAsync("SELECT a, b, sum_ab FROM data");
        List<Row> rows = await CollectRowsAsync(selectResult);
        Assert.Equal(2, rows.Count);
        Assert.Equal(13, rows[0]["sum_ab"].AsInt32());
        Assert.Equal(24, rows[1]["sum_ab"].AsInt32());
    }

    /// <summary>
    /// DEFAULT and AS (computed) are mutually exclusive; specifying both returns an error.
    /// </summary>
    [Fact]
    public async Task AlterTableAddColumn_DefaultAndComputed_ReturnsError()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (x INT)");
        await ExecuteAsync("INSERT INTO data VALUES (1)");

        CommandResult result = await ExecuteAsync(
            "ALTER TABLE data ADD COLUMN y INT DEFAULT 0 AS x + 1");

        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("mutually exclusive", result.Message);
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

    // ──────────────────── TABLE MUTABILITY ────────────────────

    /// <summary>
    /// INSERT into a read-only table returns an error without modifying the file.
    /// </summary>
    [Fact]
    public async Task InsertIntoReadOnlyTable_ReturnsError()
    {
        await ExecuteAsync("CREATE TEMP TABLE source (id INT)");
        await ExecuteAsync("INSERT INTO source VALUES (1)");

        TableDescriptor? descriptor = null;
        _session.Catalog.TryResolve("source", out descriptor);

        // Re-register the same file as read-only to simulate a non-temp table.
        TableDescriptor readOnlyDescriptor = new(
            "datum", "readonly_data", descriptor!.FilePath,
            new Dictionary<string, string>(),
            Mutability: TableMutability.ReadOnly);
        _session.Catalog.Register(readOnlyDescriptor);

        CommandResult result = await ExecuteAsync("INSERT INTO readonly_data VALUES (99)");
        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("read-only", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// DROP TABLE on a read-only table returns an error.
    /// </summary>
    [Fact]
    public async Task DropReadOnlyTable_ReturnsError()
    {
        await ExecuteAsync("CREATE TEMP TABLE source (id INT)");

        TableDescriptor? descriptor = null;
        _session.Catalog.TryResolve("source", out descriptor);

        TableDescriptor readOnlyDescriptor = new(
            "datum", "readonly_data", descriptor!.FilePath,
            new Dictionary<string, string>(),
            Mutability: TableMutability.ReadOnly);
        _session.Catalog.Register(readOnlyDescriptor);

        CommandResult result = await ExecuteAsync("DROP TABLE readonly_data");
        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("read-only", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// UPDATE on a read-only table returns an error.
    /// </summary>
    [Fact]
    public async Task UpdateReadOnlyTable_ReturnsError()
    {
        await ExecuteAsync("CREATE TEMP TABLE source (id INT)");

        TableDescriptor? descriptor = null;
        _session.Catalog.TryResolve("source", out descriptor);

        TableDescriptor readOnlyDescriptor = new(
            "datum", "readonly_data", descriptor!.FilePath,
            new Dictionary<string, string>(),
            Mutability: TableMutability.ReadOnly);
        _session.Catalog.Register(readOnlyDescriptor);

        CommandResult result = await ExecuteAsync("UPDATE readonly_data SET id = 1");
        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("read-only", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// DELETE from a read-only table returns an error.
    /// </summary>
    [Fact]
    public async Task DeleteFromReadOnlyTable_ReturnsError()
    {
        await ExecuteAsync("CREATE TEMP TABLE source (id INT)");

        TableDescriptor? descriptor = null;
        _session.Catalog.TryResolve("source", out descriptor);

        TableDescriptor readOnlyDescriptor = new(
            "datum", "readonly_data", descriptor!.FilePath,
            new Dictionary<string, string>(),
            Mutability: TableMutability.ReadOnly);
        _session.Catalog.Register(readOnlyDescriptor);

        CommandResult result = await ExecuteAsync("DELETE FROM readonly_data WHERE id = 1");
        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("read-only", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Session-owned tables (temp tables) permit all DDL/DML operations.
    /// </summary>
    [Fact]
    public async Task SessionOwnedTable_AllowsInsertUpdateDelete()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, name STRING)");
        CommandResult insertResult = await ExecuteAsync("INSERT INTO data VALUES (1, 'Alice')");
        Assert.Equal(CommandResultKind.AffectedRows, insertResult.Kind);

        CommandResult updateResult = await ExecuteAsync("UPDATE data SET name = 'Bob'");
        Assert.Equal(CommandResultKind.AffectedRows, updateResult.Kind);

        CommandResult deleteResult = await ExecuteAsync("DELETE FROM data WHERE id = 1");
        Assert.Equal(CommandResultKind.AffectedRows, deleteResult.Kind);
    }

    // ──────────────────── SIDECAR GENERATION ────────────────────

    /// <summary>
    /// After INSERT into a temp table, the catalog has a registered source index.
    /// </summary>
    [Fact]
    public async Task InsertIntoTempTable_RegistersSourceIndex()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, name STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, 'Alice'), (2, 'Bob')");

        bool hasIndex = _session.Catalog.TryGetIndex("data", out SourceIndex? index);
        Assert.True(hasIndex, "Source index should be registered on the catalog after INSERT.");
        Assert.NotNull(index);
        Assert.Equal(2, index!.Schema.TotalRowCount);
    }

    /// <summary>
    /// After INSERT into a temp table, the catalog has a registered manifest with column statistics.
    /// </summary>
    [Fact]
    public async Task InsertIntoTempTable_RegistersManifest()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, name STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, 'Alice'), (2, 'Bob')");

        bool hasManifest = _session.Catalog.TryGetManifest("data", out QueryResultsManifest? manifest);
        Assert.True(hasManifest, "Manifest should be registered on the catalog after INSERT.");
        Assert.NotNull(manifest);
        Assert.Equal(2, manifest!.RowCount);
        Assert.True(manifest.Features.Any(f => f.Name == "id"), "Manifest should contain column 'id'.");
        Assert.True(manifest.Features.Any(f => f.Name == "name"), "Manifest should contain column 'name'.");
    }

    /// <summary>
    /// Manifest includes correct statistics for columns with NULL values.
    /// </summary>
    [Fact]
    public async Task InsertIntoTempTable_ManifestHandlesNullColumns()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, name STRING, score FLOAT64)");
        await ExecuteAsync("INSERT INTO data (id, name) VALUES (1, 'Alice')");

        bool hasManifest = _session.Catalog.TryGetManifest("data", out QueryResultsManifest? manifest);
        Assert.True(hasManifest, "Manifest should be registered even when columns have NULLs.");
        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.RowCount);
    }

    /// <summary>
    /// Sidecar index file is written alongside the .datum file.
    /// </summary>
    [Fact]
    public async Task InsertIntoTempTable_WritesIndexSidecarFile()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, name STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, 'Alice')");

        _session.Catalog.TryResolve("data", out TableDescriptor? descriptor);
        string indexPath = Path.ChangeExtension(descriptor!.FilePath, ".datum-index");
        Assert.True(File.Exists(indexPath), "Index sidecar file should exist on disk.");
    }

    /// <summary>
    /// Sidecar manifest file is written alongside the .datum file.
    /// </summary>
    [Fact]
    public async Task InsertIntoTempTable_WritesManifestSidecarFile()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, name STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, 'Alice')");

        _session.Catalog.TryResolve("data", out TableDescriptor? descriptor);
        string manifestPath = Path.ChangeExtension(descriptor!.FilePath, ".datum-manifest");
        Assert.True(File.Exists(manifestPath), "Manifest sidecar file should exist on disk.");
    }

    // ──────────────────── ANALYZE ────────────────────

    /// <summary>
    /// ANALYZE rebuilds the manifest on a table that was mutated after initial INSERT.
    /// </summary>
    [Fact]
    public async Task AnalyzeTable_RebuildsSidecarsAfterUpdate()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT, name STRING)");
        await ExecuteAsync("INSERT INTO data VALUES (1, 'Alice'), (2, 'Bob')");

        // Mutate data — sidecars become stale.
        await ExecuteAsync("UPDATE data SET name = 'Charlie'");

        CommandResult analyzeResult = await ExecuteAsync("ANALYZE data");
        Assert.Equal(CommandResultKind.AffectedRows, analyzeResult.Kind);

        bool hasManifest = _session.Catalog.TryGetManifest("data", out QueryResultsManifest? manifest);
        Assert.True(hasManifest, "Manifest should be registered after ANALYZE.");
        Assert.NotNull(manifest);
        Assert.Equal(2, manifest!.RowCount);
    }

    /// <summary>
    /// ANALYZE rebuilds the index and manifest after ALTER TABLE ADD COLUMN.
    /// </summary>
    [Fact]
    public async Task AnalyzeTable_RebuildsSidecarsAfterAlterTable()
    {
        await ExecuteAsync("CREATE TEMP TABLE data (id INT)");
        await ExecuteAsync("INSERT INTO data VALUES (1), (2), (3)");

        await ExecuteAsync("ALTER TABLE data ADD COLUMN label STRING DEFAULT 'x'");
        CommandResult analyzeResult = await ExecuteAsync("ANALYZE data");
        Assert.Equal(CommandResultKind.AffectedRows, analyzeResult.Kind);

        bool hasIndex = _session.Catalog.TryGetIndex("data", out SourceIndex? index);
        Assert.True(hasIndex, "Index should be rebuilt after ANALYZE.");
        Assert.Equal(3, index!.Schema.TotalRowCount);

        bool hasManifest = _session.Catalog.TryGetManifest("data", out QueryResultsManifest? manifest);
        Assert.True(hasManifest, "Manifest should be rebuilt after ANALYZE.");
        Assert.Equal(2, manifest!.Features.Count);
    }

    /// <summary>
    /// ANALYZE on a non-existent table returns an error.
    /// </summary>
    [Fact]
    public async Task AnalyzeTable_NonExistentTable_ReturnsError()
    {
        CommandResult result = await ExecuteAsync("ANALYZE missing");
        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("does not exist", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}

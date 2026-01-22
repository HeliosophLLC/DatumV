using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.V2;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// PR10b tests for SQL <c>DEFAULT &lt;literal&gt;</c> on
/// <c>CREATE TABLE</c> columns and <c>ALTER TABLE ADD/DROP COLUMN</c>.
/// Cover: literal validation, defaults persisted via the v4 footer
/// prologue and surfaced through <see cref="ColumnInfo.DefaultExpression"/>,
/// catalog reopen, and the deliberate gaps (<c>ALTER ADD … DEFAULT</c>
/// and <c>ALTER ADD … NOT NULL</c> are rejected pending backfill).
/// </summary>
public sealed class AlterTableAndDefaultTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr10b_{Guid.NewGuid():N}");
    private string CatalogPath => Path.Combine(_tempDir, ".datum-catalog.json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    // ──────────────────── DEFAULT on CREATE TEMP TABLE ────────────────────

    [Fact]
    public void CreateTempTable_DefaultIntLiteral_StoredOnColumnInfo()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        catalog.Plan("CREATE TEMP TABLE t (a Int32 DEFAULT 5, b String)");

        Schema schema = catalog["t"].GetSchema();
        LiteralExpression literal = Assert.IsType<LiteralExpression>(schema.Columns[0].DefaultExpression);
        Assert.Equal(5L, Convert.ToInt64(literal.Value));
        Assert.Null(schema.Columns[1].DefaultExpression);
    }

    [Fact]
    public void CreateTempTable_DefaultStringLiteral_RoundTrips()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        catalog.Plan("CREATE TEMP TABLE t (status String DEFAULT 'pending')");

        Schema schema = catalog["t"].GetSchema();
        LiteralExpression literal = Assert.IsType<LiteralExpression>(schema.Columns[0].DefaultExpression);
        Assert.Equal("pending", literal.Value);
    }

    [Fact]
    public void CreateTempTable_DefaultNullLiteral_PreservedAsLiteral()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        catalog.Plan("CREATE TEMP TABLE t (a Int32 DEFAULT NULL)");

        Schema schema = catalog["t"].GetSchema();
        LiteralExpression literal = Assert.IsType<LiteralExpression>(schema.Columns[0].DefaultExpression);
        Assert.Null(literal.Value);
    }

    [Fact]
    public void CreateTempTable_DefaultNegativeNumber_AcceptedAsLiteral()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        catalog.Plan("CREATE TEMP TABLE t (a Int32 DEFAULT -7)");

        Expression? expr = catalog["t"].GetSchema().Columns[0].DefaultExpression;
        UnaryExpression unary = Assert.IsType<UnaryExpression>(expr);
        Assert.Equal(UnaryOperator.Negate, unary.Operator);
        LiteralExpression operand = Assert.IsType<LiteralExpression>(unary.Operand);
        Assert.Equal(7L, Convert.ToInt64(operand.Value));
    }

    [Fact]
    public void CreateTempTable_DefaultBoolLiteral_RoundTrips()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        catalog.Plan("CREATE TEMP TABLE t (flag Boolean DEFAULT true)");

        LiteralExpression literal = Assert.IsType<LiteralExpression>(
            catalog["t"].GetSchema().Columns[0].DefaultExpression);
        Assert.Equal(true, literal.Value);
    }

    [Fact]
    public void CreateTempTable_DefaultNonLiteral_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        // Function call as DEFAULT — not a literal.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (a Int32 DEFAULT now())"));
        Assert.Contains("must be a literal", ex.Message);
    }

    [Fact]
    public void CreateTempTable_DefaultArithmeticExpression_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        // 1 + 2 is a binary expression, not a literal.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (a Int32 DEFAULT 1 + 2)"));
        Assert.Contains("must be a literal", ex.Message);
    }

    // ──────────────────── DEFAULT on persistent CREATE TABLE ────────────────────

    [Fact]
    public void CreatePersistentTable_DefaultsPersistedInFooterPrologue()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32, status String DEFAULT 'pending', score Int32 DEFAULT -1)");
        }

        // Read the .datum file directly and inspect the prologue.
        string datumPath = Path.Combine(_tempDir, "users.datum");
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        Assert.Equal(2, reader.Footer.Prologue.ColumnDefaults.Count);

        Dictionary<ushort, string> bySlot = reader.Footer.Prologue.ColumnDefaults
            .ToDictionary(d => d.ColumnIndex, d => d.SqlFragment);
        Assert.True(bySlot.ContainsKey(1));
        Assert.True(bySlot.ContainsKey(2));
        // Fragments are SQL — exact format depends on QueryExplainer but
        // both should round-trip back to the parsed expressions.
        Assert.Contains("pending", bySlot[1]);
        Assert.Contains("1", bySlot[2]);
    }

    [Fact]
    public void CreatePersistentTable_ReopenedCatalog_RestoresDefaultsOnSchema()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32, status String DEFAULT 'pending')");
        }

        using TableCatalog reopened = new(pool, CatalogPath);
        Schema schema = reopened["users"].GetSchema();

        Assert.Null(schema.Columns[0].DefaultExpression);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(schema.Columns[1].DefaultExpression);
        Assert.Equal("pending", literal.Value);
    }

    // ──────────────────── ALTER TABLE ADD COLUMN ────────────────────

    [Fact]
    public void AlterTable_AddColumn_OnTempTable_AppendsNullableColumn()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        catalog.Plan("ALTER TABLE t ADD COLUMN name String");

        Schema schema = catalog["t"].GetSchema();
        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("name", schema.Columns[1].Name);
        Assert.Equal(DataKind.String, schema.Columns[1].Kind);
        Assert.True(schema.Columns[1].Nullable);
    }

    [Fact]
    public void AlterTable_AddColumn_OptionalColumnKeyword_Accepted()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        // COLUMN keyword is optional.
        catalog.Plan("ALTER TABLE t ADD score Float64");

        Schema schema = catalog["t"].GetSchema();
        Assert.Equal("score", schema.Columns[1].Name);
        Assert.Equal(DataKind.Float64, schema.Columns[1].Kind);
    }

    [Fact]
    public async Task AlterTable_AddColumn_WithDefault_NewInsertsAutoFill()
    {
        // ALTER ADD COLUMN now accepts DEFAULT. Pre-existing rows read
        // NULL (column wasn't present); INSERTs after the ALTER that
        // omit the new column pick up the DEFAULT.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");
        catalog.Plan("INSERT INTO t VALUES (1), (2)");

        catalog.Plan("ALTER TABLE t ADD COLUMN status String DEFAULT 'pending'");

        // Insert a row without supplying the new column — should DEFAULT-fill.
        catalog.Plan("INSERT INTO t (id) VALUES (3)");

        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(3, batch.Count);
            // Existing rows backfilled NULL.
            Assert.True(batch[0][1].IsNull);
            Assert.True(batch[1][1].IsNull);
            // New row picks up the DEFAULT.
            Assert.Equal("pending", batch[2][1].AsString(batch.Arena));
            batch.Dispose();
        }
    }

    [Fact]
    public void AlterTable_AddColumn_WithDefault_RejectsNonLiteral()
    {
        // Same literal-only validation as CREATE TABLE — function calls
        // and other computed expressions are rejected at ALTER time.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE t ADD COLUMN created_at DateTime DEFAULT now()"));
        Assert.Contains("literal", ex.Message);
    }

    [Fact]
    public void AlterTable_AddColumn_WithNotNull_Throws_PendingBackfill()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE t ADD COLUMN score Int32 NOT NULL"));
        Assert.Contains("NOT NULL", ex.Message);
    }

    [Fact]
    public void AlterTable_AddColumn_OnPersistentTable_PersistsAcrossReopen()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32)");
            catalog.Plan("ALTER TABLE users ADD COLUMN name String");
        }

        using TableCatalog reopened = new(pool, CatalogPath);
        Schema schema = reopened["users"].GetSchema();
        Assert.Equal(["id", "name"], schema.Columns.Select(c => c.Name));
    }

    // ──────────────────── ALTER TABLE DROP COLUMN ────────────────────

    [Fact]
    public void AlterTable_DropColumn_OnTempTable_RemovesColumn()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String, score Int32)");

        catalog.Plan("ALTER TABLE t DROP COLUMN name");

        Schema schema = catalog["t"].GetSchema();
        Assert.Equal(["id", "score"], schema.Columns.Select(c => c.Name));
    }

    [Fact]
    public void AlterTable_DropColumn_OptionalColumnKeyword_Accepted()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        catalog.Plan("ALTER TABLE t DROP name");

        Assert.Equal(["id"], catalog["t"].GetSchema().Columns.Select(c => c.Name));
    }

    [Fact]
    public void AlterTable_DropColumn_Missing_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE t DROP COLUMN nope"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void AlterTable_DropColumn_IfExists_NoOpWhenMissing()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        // Should not throw.
        catalog.Plan("ALTER TABLE t DROP COLUMN IF EXISTS nope");

        Assert.Equal(["id"], catalog["t"].GetSchema().Columns.Select(c => c.Name));
    }

    [Fact]
    public void AlterTable_DropColumn_OnPersistentTable_PersistsAcrossReopen()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32, name String)");
            catalog.Plan("ALTER TABLE users DROP COLUMN name");
        }

        using TableCatalog reopened = new(pool, CatalogPath);
        Schema schema = reopened["users"].GetSchema();
        Assert.Equal(["id"], schema.Columns.Select(c => c.Name));
    }

    [Fact]
    public void AlterTable_OnMissingTable_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("ALTER TABLE nope ADD COLUMN x Int32"));
    }
}

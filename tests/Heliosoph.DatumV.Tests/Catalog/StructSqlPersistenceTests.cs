using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.DatumFile.V2;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// End-to-end struct persistence through the SQL surface: CREATE TABLE with
/// a Struct column, INSERT struct literals, and read the fields back after a
/// cold catalog reopen. The cold reopen is the load-bearing part — it proves
/// the append path emitted the on-disk type table (descriptor blobs + column
/// StructTypeId), not just that the in-memory registry happened to survive.
/// </summary>
public sealed class StructSqlPersistenceTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_struct_sql_{Guid.NewGuid():N}");
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

    [Fact]
    public async Task Insert_StructLiterals_ColdReopen_FieldsResolve()
    {
        Pool pool = CreatePool();
        using (TableCatalog catalog = CreateCatalog(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (id Int32, s Struct<x: Int32, y: String>)");
            catalog.Plan(
                "INSERT INTO t VALUES " +
                "(1, {x: 10, y: 'alpha alpha alpha alpha alpha'}), " +
                "(2, {x: 20, y: 'beta beta beta beta beta beta'})");
        }

        // The .datum file itself must carry the type table — a reader with
        // no access to the writing session's registry depends on it.
        string datumPath = Path.Combine(_tempDir, "data", "public", "t.datum");
        using (DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath))
        {
            Assert.True(
                (reader.Header.Flags & DatumFileFlagsV2.HasTypeTable) != 0,
                "SQL INSERT of struct values must emit the on-disk type table");
            Assert.NotEmpty(reader.Footer.TypeTable);
            Assert.NotNull(reader.Footer.Columns[1].StructTypeId);
        }

        using TableCatalog reopened = CreateCatalog(pool, CatalogPath);

        // The reopened provider must rebuild the struct column's field list
        // from the file's type table — the planner's field resolution and
        // the write-side coercion both key off ColumnInfo.Fields.
        Schema reopenedSchema = reopened["t"].GetSchema();
        Assert.NotNull(reopenedSchema.Columns[1].Fields);
        Assert.Equal(["x", "y"], reopenedSchema.Columns[1].Fields!.Select(f => f.Name));

        StatementPlan plan = reopened.Plan("SELECT id, t.s.x AS x, t.s.y AS y FROM t ORDER BY id");
        List<(int Id, int X, string Y)> rows = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                Row row = batch[r];
                rows.Add((
                    row[0].AsInt32(),
                    row[1].AsInt32(),
                    row[2].AsString(batch.Arena, reopened.SidecarRegistry)));
            }
        }

        Assert.Equal(
        [
            (1, 10, "alpha alpha alpha alpha alpha"),
            (2, 20, "beta beta beta beta beta beta"),
        ], rows);
    }

    [Fact]
    public async Task Insert_SecondSession_SameShape_ReusesTypeTableEntry()
    {
        // Two separate INSERT statements (two append sessions, two writer
        // instances) writing the same shape must converge on ONE type-table
        // entry — the second session seeds its allocator from the existing
        // table instead of appending a duplicate or colliding id.
        Pool pool = CreatePool();
        using (TableCatalog catalog = CreateCatalog(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (s Struct<x: Int32, y: String>)");
            catalog.Plan("INSERT INTO t VALUES ({x: 1, y: 'one one one one one one'})");
            catalog.Plan("INSERT INTO t VALUES ({x: 2, y: 'two two two two two two'})");
        }

        string datumPath = Path.Combine(_tempDir, "data", "public", "t.datum");
        using (DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath))
        {
            Assert.Single(reader.Footer.TypeTable);
        }

        using TableCatalog reopened = CreateCatalog(pool, CatalogPath);
        StatementPlan plan = reopened.Plan("SELECT t.s.x AS x FROM t ORDER BY x");
        List<int> xs = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                xs.Add(batch[r][0].AsInt32());
            }
        }
        Assert.Equal([1, 2], xs);
    }

    [Fact]
    public async Task Ctas_StructColumn_PropagatesFieldsAndData()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool, CatalogPath);
        catalog.Plan("CREATE TABLE src (id Int32, s Struct<x: Int32, y: String>)");
        catalog.Plan(
            "INSERT INTO src VALUES " +
            "(1, {x: 10, y: 'alpha alpha alpha alpha alpha'}), " +
            "(2, {x: 20, y: 'beta beta beta beta beta beta'})");

        catalog.Plan("CREATE TABLE dst AS SELECT id, s FROM src");

        // The CTAS target's schema must carry the struct field list — both
        // in-session and from the file after the source is gone.
        Schema dstSchema = catalog["dst"].GetSchema();
        Assert.NotNull(dstSchema.Columns[1].Fields);
        Assert.Equal(["x", "y"], dstSchema.Columns[1].Fields!.Select(f => f.Name));

        catalog.Plan("DROP TABLE src");
        StatementPlan plan = catalog.Plan(
            "SELECT id, dst.s.x AS x, dst.s.y AS y FROM dst ORDER BY id");
        List<(int Id, int X, string Y)> rows = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                Row row = batch[r];
                rows.Add((
                    row[0].AsInt32(),
                    row[1].AsInt32(),
                    row[2].AsString(batch.Arena, catalog.SidecarRegistry)));
            }
        }
        Assert.Equal(
        [
            (1, 10, "alpha alpha alpha alpha alpha"),
            (2, 20, "beta beta beta beta beta beta"),
        ], rows);
    }

    [Fact]
    public async Task Ctas_ArrayOfStructColumn_CopiesAcrossTables()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool, CatalogPath);

        // Decoy sidecar-backed table registered first, so a dangling slot
        // pointer into the wrong blob file cannot resolve to the right
        // bytes by registration-order luck.
        catalog.Plan("CREATE TABLE decoy (junk String)");
        catalog.Plan($"INSERT INTO decoy VALUES ('{new string('x', 500)}')");

        catalog.Plan("CREATE TABLE src (id Int32, dets Array<Struct<label: String, score: Float32>>)");
        // Scores use exactly-representable Float32 values — the strict
        // lossless-coercion policy rejects e.g. 0.9 (inexact in Float32).
        catalog.Plan(
            "INSERT INTO src VALUES (1, [" +
            "{label: 'cat cat cat cat cat cat', score: 0.5}, " +
            "{label: 'dog dog dog dog dog dog', score: 0.25}])");

        catalog.Plan("CREATE TABLE dst AS SELECT id, dets FROM src");
        catalog.Plan("DROP TABLE src");

        // The CTAS target must not accumulate spurious type-table entries —
        // one declared shape, one entry, referenced by the column footer.
        string dstPath = Path.Combine(_tempDir, "data", "public", "dst.datum");
        using (DatumFileReaderV2 rd = DatumFileReaderV2.Open(dstPath))
        {
            Assert.Single(rd.Footer.TypeTable);
            Assert.Equal((ushort)1, rd.Footer.Columns[1].StructTypeId);
        }

        // PG-style 1-based array indexing: dets[1] is the first element.
        StatementPlan plan = catalog.Plan(
            "SELECT dets[1].label AS l0, dets[2].label AS l1, dets[1].score AS s0 FROM dst");
        List<(string L0, string L1, float S0)> rows = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                Row row = batch[r];
                rows.Add((
                    row[0].AsString(batch.Arena, catalog.SidecarRegistry),
                    row[1].AsString(batch.Arena, catalog.SidecarRegistry),
                    row[2].AsFloat32()));
            }
        }

        Assert.Single(rows);
        Assert.Equal("cat cat cat cat cat cat", rows[0].L0);
        Assert.Equal("dog dog dog dog dog dog", rows[0].L1);
        Assert.Equal(0.5f, rows[0].S0);
    }

    [Fact]
    public async Task IndexSeek_ScalarStructColumn_ResolvesFieldsThroughSeekPath()
    {
        // A struct column projected out of an index seek decodes through a
        // different path than a sequential scan — DatumFileSeekSessionV2, not
        // ScanOperator's streaming decode. Its fields must still resolve,
        // whether from the runtime TypeId the seek session stamps or the
        // schema-fields fallback in the expression evaluator. The cold reopen
        // makes the file's on-disk type table the only source of shape.
        Pool pool = CreatePool();
        using (TableCatalog catalog = CreateCatalog(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (id Int32, s Struct<x: Int32, y: String>)");
            catalog.Plan("CREATE INDEX idx_t_id ON t (id)");
            catalog.Plan(
                "INSERT INTO t VALUES " +
                "(1, {x: 10, y: 'alpha alpha alpha alpha alpha'}), " +
                "(2, {x: 20, y: 'beta beta beta beta beta beta'}), " +
                "(3, {x: 30, y: 'gamma gamma gamma gamma gamma'})");
        }

        using TableCatalog reopened = CreateCatalog(pool, CatalogPath);

        // Prove the seek path actually fired — correctness alone can't
        // distinguish a seek from a chunked scan that filtered to one row.
        ExplainPlanNode root = await AnalyzePlanAsync(
            reopened.Plan("SELECT t.s.x AS x, t.s.y AS y FROM t WHERE id = 2"));
        ExplainPlanNode? scan = FindScanNode(root);
        Assert.NotNull(scan);
        Assert.True(
            scan!.ExactSeekRowsFetched is > 0,
            "WHERE id = 2 on an indexed column should fire the exact-seek path.");

        StatementPlan plan = reopened.Plan("SELECT t.s.x AS x, t.s.y AS y FROM t WHERE id = 2");
        List<(int X, string Y)> rows = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                Row row = batch[r];
                rows.Add((row[0].AsInt32(), row[1].AsString(batch.Arena, reopened.SidecarRegistry)));
            }
        }

        (int X, string Y) only = Assert.Single(rows);
        Assert.Equal(20, only.X);
        Assert.Equal("beta beta beta beta beta beta", only.Y);
    }

    private static ExplainPlanNode? FindScanNode(ExplainPlanNode node)
    {
        if (node.OperatorName == "Scan") return node;
        foreach (ExplainPlanNode child in node.Children)
        {
            ExplainPlanNode? found = FindScanNode(child);
            if (found is not null) return found;
        }
        return null;
    }
}

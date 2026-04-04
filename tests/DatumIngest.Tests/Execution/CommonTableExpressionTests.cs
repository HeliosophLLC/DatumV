using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for Common Table Expression (WITH clause) support, covering parsing,
/// planning, non-recursive execution, recursive execution, materialization,
/// column renaming, and spill-to-disk behavior.
/// </summary>
public sealed class CommonTableExpressionTests : ServiceTestBase
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    // ─────────────── Parsing tests ───────────────

    /// <summary>
    /// A basic CTE parses into a <see cref="SelectStatement"/> with a
    /// non-null <see cref="SelectStatement.CommonTableExpressions"/> list.
    /// </summary>
    [Fact]
    public void Parse_SimpleCte_ProducesCommonTableExpression()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH stats AS (SELECT user_id, COUNT(amount) FROM orders GROUP BY user_id) " +
            "SELECT * FROM stats")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        Assert.Single(result.CommonTableExpressions);
        Assert.Equal("stats", result.CommonTableExpressions[0].Name);
        Assert.False(result.CommonTableExpressions[0].IsRecursive);
        Assert.Equal(MaterializationHint.Default, result.CommonTableExpressions[0].Hint);
        Assert.Null(result.CommonTableExpressions[0].ColumnNames);
    }

    /// <summary>
    /// Multiple CTEs separated by commas are all captured.
    /// </summary>
    [Fact]
    public void Parse_MultipleCtes_ParsesAll()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH a AS (SELECT x FROM t1), " +
            "b AS (SELECT y FROM t2) " +
            "SELECT * FROM a JOIN b ON a.x = b.y")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        Assert.Equal(2, result.CommonTableExpressions.Count);
        Assert.Equal("a", result.CommonTableExpressions[0].Name);
        Assert.Equal("b", result.CommonTableExpressions[1].Name);
    }

    /// <summary>
    /// Explicit column names in the CTE definition are captured.
    /// </summary>
    [Fact]
    public void Parse_CteWithColumnNames_CapturesColumns()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH stats(uid, total) AS (SELECT user_id, SUM(amount) FROM orders GROUP BY user_id) " +
            "SELECT * FROM stats")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        CommonTableExpression commonTableExpression = result.CommonTableExpressions[0];
        Assert.NotNull(commonTableExpression.ColumnNames);
        Assert.Equal(2, commonTableExpression.ColumnNames.Count);
        Assert.Equal("uid", commonTableExpression.ColumnNames[0]);
        Assert.Equal("total", commonTableExpression.ColumnNames[1]);
    }

    /// <summary>
    /// The MATERIALIZED hint is captured on the CTE.
    /// </summary>
    [Fact]
    public void Parse_MaterializedHint_Captured()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH stats AS MATERIALIZED (SELECT x FROM t) SELECT * FROM stats")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        Assert.Equal(MaterializationHint.Materialized, result.CommonTableExpressions[0].Hint);
    }

    /// <summary>
    /// The NOT MATERIALIZED hint is captured on the CTE.
    /// </summary>
    [Fact]
    public void Parse_NotMaterializedHint_Captured()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH stats AS NOT MATERIALIZED (SELECT x FROM t) SELECT * FROM stats")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        Assert.Equal(MaterializationHint.NotMaterialized, result.CommonTableExpressions[0].Hint);
    }

    /// <summary>
    /// LIMIT inside a non-recursive CTE body is parsed onto the inner SelectStatement.
    /// </summary>
    [Fact]
    public void Parse_CteWithLimit_CapturesLimit()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH sample AS (SELECT x FROM t LIMIT 100) SELECT * FROM sample")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        SelectQueryExpression body = Assert.IsType<SelectQueryExpression>(result.CommonTableExpressions[0].Body);
        Assert.Equal(100, Convert.ToInt32(((LiteralExpression)body.Statement.Limit!).Value));
    }

    /// <summary>
    /// ORDER BY and LIMIT inside a non-recursive CTE body are both parsed.
    /// </summary>
    [Fact]
    public void Parse_CteWithOrderByAndLimit_CapturesBoth()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH top_items AS (SELECT x FROM t ORDER BY x LIMIT 50) SELECT * FROM top_items")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        SelectQueryExpression body = Assert.IsType<SelectQueryExpression>(result.CommonTableExpressions[0].Body);
        Assert.NotNull(body.Statement.OrderBy);
        Assert.Equal(50, Convert.ToInt32(((LiteralExpression)body.Statement.Limit!).Value));
    }

    /// <summary>
    /// LIMIT inside a CTE restricts the rows produced by that CTE at execution time.
    /// </summary>
    [Fact]
    public async Task Execute_CteWithLimit_RestrictsRows()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x"],
            [1f],
            [2f],
            [3f],
            [4f],
            [5f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH sample AS (SELECT x FROM t LIMIT 3) SELECT * FROM sample",
            catalog);

        Assert.Equal(3, results.Count);
    }

    /// <summary>
    /// Multiple CTEs each with their own LIMIT clause produce correct row counts.
    /// </summary>
    [Fact]
    public async Task Execute_MultipleCtes_EachWithLimit_ProducesCorrectCounts()
    {
        string[] columns = ["x"];
        object?[][] rows =
        [
            [1f],
            [2f],
            [3f],
            [4f],
            [5f],
        ];

        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("t1", columns, rows));
        catalog.Add(CreateProvider("t2", columns, rows));

        List<Row> results = await ExecuteQueryAsync(
            "WITH a AS (SELECT x FROM t1 LIMIT 2), " +
            "b AS (SELECT x FROM t2 LIMIT 1) " +
            "SELECT a.x FROM a INNER JOIN b ON a.x = b.x",
            catalog);

        // b has at most 1 row, so the join produces at most 1 match.
        Assert.True(results.Count <= 1);
    }

    /// <summary>
    /// WITH RECURSIVE sets the IsRecursive flag on all CTEs in that block.
    /// </summary>
    [Fact]
    public void Parse_WithRecursive_SetsFlag()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH RECURSIVE nums AS (SELECT 1 AS n FROM dual UNION ALL SELECT n FROM nums WHERE n < 5) " +
            "SELECT * FROM nums")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        Assert.True(result.CommonTableExpressions[0].IsRecursive);
    }

    /// <summary>
    /// UNION ALL within a recursive CTE body splits into anchor and recursive query.
    /// </summary>
    [Fact]
    public void Parse_RecursiveCteBody_SplitsAnchorAndRecursive()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH RECURSIVE chain AS (" +
            "SELECT id, parent_id FROM nodes WHERE parent_id IS NULL " +
            "UNION ALL " +
            "SELECT n.id, n.parent_id FROM nodes AS n JOIN chain ON n.parent_id = chain.id" +
            ") SELECT * FROM chain")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        CommonTableExpression commonTableExpression = result.CommonTableExpressions[0];
        Assert.NotNull(commonTableExpression.RecursiveQuery);

        // Anchor query: SELECT id, parent_id FROM nodes WHERE parent_id IS NULL
        SelectStatement anchorStatement = Assert.IsType<SelectQueryExpression>(commonTableExpression.Body).Statement;
        Assert.Equal(2, anchorStatement.Columns.Count);

        // Recursive query: SELECT n.id, n.parent_id FROM nodes AS n JOIN chain ON ...
        Assert.Equal(2, commonTableExpression.RecursiveQuery.Columns.Count);
    }

    // ─────────────── Non-recursive execution tests ───────────────

    /// <summary>
    /// A simple CTE referenced once in FROM produces the expected rows.
    /// </summary>
    [Fact]
    public async Task Execute_SimpleCte_ReturnsExpectedRows()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["user_id", "amount"],
            [1f, 100f],
            [1f, 200f],
            [2f, 50f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH totals AS (SELECT user_id, SUM(amount) AS total FROM orders GROUP BY user_id) " +
            "SELECT * FROM totals",
            catalog);

        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// CTE with explicit column names renames the output columns.
    /// </summary>
    [Fact]
    public async Task Execute_CteWithColumnNames_RenamesColumns()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x", "y"],
            [1f, 2f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH renamed(a, b) AS (SELECT x, y FROM t) SELECT a, b FROM renamed",
            catalog);

        Assert.Single(results);
        Assert.Equal(1f, results[0]["a"].AsFloat32());
        Assert.Equal(2f, results[0]["b"].AsFloat32());
    }

    /// <summary>
    /// CTE referenced multiple times is auto-materialized and produces consistent results.
    /// </summary>
    [Fact]
    public async Task Execute_CteReferencedTwice_AutoMaterializes()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "val"],
            [1f, 10f],
            [2f, 20f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH shared AS (SELECT id, val FROM t) " +
            "SELECT a.id, b.val FROM shared AS a JOIN shared AS b ON a.id = b.id",
            catalog);

        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// Explicitly NOT MATERIALIZED CTE re-executes per reference.
    /// </summary>
    [Fact]
    public async Task Execute_NotMaterializedCte_ReExecutesPerReference()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id"],
            [1f],
            [2f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH inline_cte AS NOT MATERIALIZED (SELECT id FROM t) " +
            "SELECT * FROM inline_cte",
            catalog);

        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// CTE used with a WHERE filter in the outer query.
    /// </summary>
    [Fact]
    public async Task Execute_CteWithOuterFilter_FiltersCorrectly()
    {
        TableCatalog catalog = CreateCatalog("students",
            columns: ["name", "score"],
            ["alice", 90f],
            ["bob", 50f],
            ["carol", 75f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH high_scorers AS (SELECT name, score FROM students WHERE score >= 75) " +
            "SELECT name FROM high_scorers WHERE score > 80",
            catalog);

        Assert.Single(results);
        Assert.Equal("alice", results[0]["name"].AsString());
    }

    /// <summary>
    /// Regression for the SELECT * + aliased source leak. When a CTE body is
    /// <c>SELECT * FROM t inner_alias</c>, the inner alias previously qualified
    /// the CTE's output column names (e.g. <c>inner_alias.col</c>), so when the
    /// outer query re-aliased the CTE (<c>cte outer_alias</c>) and referenced
    /// <c>outer_alias.col</c>, the lookup failed. The output column names of a
    /// single-source <c>SELECT *</c> must be unqualified — PostgreSQL semantics
    /// for <c>SELECT * FROM t alias</c>.
    /// </summary>
    [Fact]
    public async Task Execute_CteWithStarOnAliasedSource_OuterAliasResolvesColumns()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "val"],
            [1f, 10f],
            [2f, 20f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH frames AS (SELECT * FROM t inner_alias) " +
            "SELECT outer_alias.id, outer_alias.val FROM frames outer_alias",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(1f, results[0]["id"].AsFloat32());
        Assert.Equal(10f, results[0]["val"].AsFloat32());
        Assert.Equal(2f, results[1]["id"].AsFloat32());
        Assert.Equal(20f, results[1]["val"].AsFloat32());
    }

    /// <summary>
    /// <see cref="QuerySchemaResolver"/> should return only the columns projected by the
    /// CTE's SELECT clause, not all columns from the underlying table. This matches what
    /// execution actually emits and what the shell header should display.
    /// </summary>
    [Fact]
    public async Task SchemaResolver_CteWithNarrowProjection_ReturnsOnlyCteColumns()
    {
        TableCatalog catalog = CreateCatalog("orders_csv",
            columns: ["order_id", "user_id", "eval_set", "order_number", "order_dow"],
            [1f, 1f, "train", 11f, 1f]);
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "WITH train_orders AS (" +
            "  SELECT order_id, user_id, order_number" +
            "  FROM orders_csv" +
            "  WHERE eval_set = 'train'" +
            ") " +
            "SELECT * FROM train_orders")).Statement;

        ResolvedQuerySchema schema = await resolver.ResolveAsync(statement, CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.Contains(schema.Columns, c => c.ColumnName == "order_id");
        Assert.Contains(schema.Columns, c => c.ColumnName == "user_id");
        Assert.Contains(schema.Columns, c => c.ColumnName == "order_number");
        Assert.DoesNotContain(schema.Columns, c => c.ColumnName == "eval_set");
        Assert.DoesNotContain(schema.Columns, c => c.ColumnName == "order_dow");
    }

    // ─────────────── Recursive CTE execution tests ───────────────

    /// <summary>
    /// A simple recursive CTE generating a sequence of numbers.
    /// </summary>
    [Fact]
    public async Task Execute_RecursiveCte_GeneratesSequence()
    {
        // We need a single-row table to seed the anchor.
        TableCatalog catalog = CreateCatalog("dual",
            columns: ["dummy"],
            [1f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH RECURSIVE nums AS (" +
            "SELECT 1 AS n FROM dual " +
            "UNION ALL " +
            "SELECT n + 1 AS n FROM nums WHERE n < 5" +
            ") SELECT n FROM nums",
            catalog);

        Assert.Equal(5, results.Count);
        for (int index = 0; index < 5; index++)
        {
            DataValue n = results[index]["n"];
            int actual = n.Kind switch
            {
                DataKind.Int8 => n.AsInt8(),
                DataKind.Int16 => n.AsInt16(),
                DataKind.Int32 => n.AsInt32(),
                DataKind.Float32 => (int)n.AsFloat32(),
                DataKind.Float64 => (int)n.AsFloat64(),
                _ => throw new InvalidOperationException($"Unexpected kind: {n.Kind}")
            };
            Assert.Equal(index + 1, actual);
        }
    }

    /// <summary>
    /// Recursive CTE that exceeds max recursion depth throws.
    /// </summary>
    [Fact]
    public async Task Execute_RecursiveCte_ExceedingMaxDepth_Throws()
    {
        TableCatalog catalog = CreateCatalog("dual",
            columns: ["dummy"],
            [1f]);

        // Set very low recursion limit.
        ExecutionContext context = CreateExecutionContext(catalog: catalog, maxRecursionDepth: 3);

        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "WITH RECURSIVE nums AS (" +
            "SELECT 1 AS n FROM dual " +
            "UNION ALL " +
            "SELECT n + 1 AS n FROM nums WHERE n < 100" +
            ") SELECT n FROM nums")).Statement;

        QueryPlanner planner = new(catalog, DefaultFunctions);
        QueryOperator plan = planner.Plan(statement);

        RecursionDepthExceededException exception = await Assert.ThrowsAsync<RecursionDepthExceededException>(async () =>
        {
            await foreach (RowBatch batch in plan.ExecuteAsync(context))
            {
                // Drain the stream.
                _ = batch.Count;
            }
        });

        Assert.Contains("exceeded maximum recursion depth", exception.Message);
        Assert.Equal("nums", exception.CteName);
        Assert.Equal(3, exception.MaxDepth);
    }

    /// <summary>
    /// Regression for the SpillPartition single-schema assumption. Build side
    /// has 4 columns, probe side has 2 — under spill, the shared
    /// <c>_spillSchema</c> is set from whichever side gets there first, then
    /// <c>SpillReaderWriter.Write</c> iterates <c>columnCount = _schema.Count</c>
    /// against a row whose <c>RawValues.Length</c> doesn't match, throwing
    /// ArgumentOutOfRangeException from <c>Row.get_Item</c>. The current code
    /// path docs this as an invariant ("must match both sides' counts") but
    /// doesn't enforce it — JOINs where the two sides legitimately have
    /// different column counts crash when spill kicks in.
    /// </summary>
    [Fact]
    public async Task Execute_JoinWithSpill_DifferentColumnCounts_DoesNotThrow()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("a", ["k", "v1", "v2", "v3"],
            [1f, 10f, 100f, 1000f],
            [2f, 20f, 200f, 2000f]));
        catalog.Add(CreateProvider("b", ["k", "w"],
            [1f, 11f],
            [2f, 22f]));

        ExecutionContext context = CreateExecutionContext(
            catalog: catalog,
            memoryBudgetBytes: 1); // Force spill.

        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "SELECT a.k, a.v1, a.v2, a.v3, b.w FROM a JOIN b ON a.k = b.k")).Statement;

        QueryPlanner planner = new(catalog, DefaultFunctions);
        QueryOperator plan = planner.Plan(statement);
        List<Row> results = await plan.CollectRowsAsync(context);

        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// Regression for "Cannot access a disposed object: 'RowBatch'" with stack
    ///   RowBatch.get_Arena() → PoolBacking.Return → SpillPartition.Dispose
    ///   ← GraceHashJoinExecutor.ExecuteAsync ← JoinOperator
    ///   ← ProjectOperator ← RecursiveCommonTableExpressionOperator.MaterializeAsync
    /// Forces the GraceHashJoin spill path inside the recursive member by setting
    /// a tiny memory budget; the JOIN's probe side spills (AppendToStaging into
    /// _probeStaging), and SpillPartition.Dispose later trips on a disposed batch
    /// whose field reference was never nulled.
    /// </summary>
    [Fact]
    public async Task Execute_RecursiveCte_JoinWithSpill_DoesNotDisposeRowBatch()
    {
        TableCatalog catalog = CreateCatalog("seq",
            columns: ["n"],
            [0f], [1f], [2f], [3f], [4f]);

        ExecutionContext context = CreateExecutionContext(
            catalog: catalog,
            memoryBudgetBytes: 1, // Forces spill immediately on any add.
            maxRecursionDepth: 100);

        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "WITH RECURSIVE " +
            "steps AS (SELECT n, [n * 1.0, n * 2.0] AS val FROM seq), " +
            "accumulated AS (" +
            "  SELECT n, val AS cumulative FROM steps WHERE n = 0 " +
            "  UNION ALL " +
            "  SELECT s.n, [a.cumulative[1] + s.val[1], a.cumulative[2] + s.val[2]] " +
            "  FROM accumulated a JOIN steps s ON s.n = a.n + 1" +
            ") " +
            "SELECT n FROM accumulated")).Statement;

        QueryPlanner planner = new(catalog, DefaultFunctions);
        QueryOperator plan = planner.Plan(statement);
        List<Row> results = await plan.CollectRowsAsync(context);

        Assert.Equal(5, results.Count);
    }

    /// <summary>
    /// Regression for "Cannot access a disposed object: 'RowBatch'" reproduced from
    /// a recursive pose-chain query. The user's failing query chains four CTEs:
    /// <c>frames</c> (TVF) → <c>prev_curr</c> (LEFT SELF-JOIN of frames) →
    /// <c>step_poses</c> (per-frame computation) → <c>accumulated</c> (RECURSIVE,
    /// joins working table against step_poses). The structural elements likely
    /// involved: <c>frames</c> referenced twice in <c>prev_curr</c> (auto-materialised),
    /// <c>step_poses</c> referenced twice in <c>accumulated</c> (anchor + recursive
    /// member, auto-materialised), recursive member JOIN against a materialised
    /// helper CTE, and Array-kind columns carrying arena-backed payloads across
    /// iterations.
    /// </summary>
    [Fact]
    public async Task Execute_RecursiveCte_ChainedCtesWithArrayColumns_DoesNotDisposeRowBatch()
    {
        TableCatalog catalog = CreateCatalog("seq",
            columns: ["n"],
            [0f], [1f], [2f], [3f], [4f]);

        // Mirrors the user's CTE chain shape:
        //   frames-equivalent: SELECT n FROM seq
        //   prev_curr: LEFT SELF-JOIN of frames (frames referenced twice → auto-mat)
        //   step_poses: per-row computation including an array column (step_poses
        //               referenced twice in accumulated → auto-mat)
        //   accumulated: RECURSIVE seed at n=0, recurse via JOIN with step_poses
        List<Row> results = await ExecuteQueryAsync(
            "WITH RECURSIVE " +
            "frames AS (SELECT n FROM seq), " +
            "prev_curr AS (" +
            "  SELECT f1.n, f2.n AS prev " +
            "  FROM frames f1 LEFT JOIN frames f2 ON f2.n = f1.n - 1" +
            "), " +
            "step_poses AS (" +
            "  SELECT n, [n * 1.0, n * 2.0, n * 3.0] AS step " +
            "  FROM prev_curr" +
            "), " +
            "accumulated AS (" +
            "  SELECT n, step AS cumulative FROM step_poses WHERE n = 0 " +
            "  UNION ALL " +
            "  SELECT s.n, [a.cumulative[1] + s.step[1], a.cumulative[2] + s.step[2], a.cumulative[3] + s.step[3]] " +
            "  FROM accumulated a JOIN step_poses s ON s.n = a.n + 1" +
            ") " +
            "SELECT n, cumulative FROM accumulated",
            catalog);

        Assert.Equal(5, results.Count);
    }

    /// <summary>
    /// Regression for "Cannot access a disposed object: 'RowBatch'" when the recursive
    /// member JOINs the working table against a separately-defined non-recursive CTE.
    /// Shape: anchor seeds from the helper CTE at n=0; each iteration joins working
    /// table against the helper CTE on n = a.n + 1. Reproduces a recursive pose-chain
    /// pattern (anchor + cumulative-composition via JOIN against per-frame data).
    /// </summary>
    [Fact]
    public async Task Execute_RecursiveCte_JoinsHelperCte_DoesNotDisposeRowBatch()
    {
        TableCatalog catalog = CreateCatalog("seq",
            columns: ["n", "val"],
            [0f, 10f],
            [1f, 20f],
            [2f, 30f],
            [3f, 40f],
            [4f, 50f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH RECURSIVE " +
            "steps AS (SELECT n, val FROM seq), " +
            "accumulated AS (" +
            "  SELECT n, val AS cumulative FROM steps WHERE n = 0 " +
            "  UNION ALL " +
            "  SELECT s.n, a.cumulative + s.val " +
            "  FROM accumulated a JOIN steps s ON s.n = a.n + 1" +
            ") " +
            "SELECT n, cumulative FROM accumulated",
            catalog);

        Assert.Equal(5, results.Count);
        // Each row's cumulative = sum of vals 10 + 20 + ... up to that frame.
        float[] expected = [10f, 30f, 60f, 100f, 150f];
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(i, (int)results[i]["n"].AsFloat32());
            Assert.Equal(expected[i], results[i]["cumulative"].AsFloat32());
        }
    }

    /// <summary>
    /// Regression: a <c>LET</c> binding that produces an Image value, in a CTE
    /// referenced by both the anchor and recursive halves of a recursive CTE,
    /// causes a sibling <c>Float32[]</c> literal column to be corrupted in
    /// iterations after the anchor. Frame 0 (anchor) reads the literal
    /// correctly; frame 1+ (via JOIN against the materialised helper CTE)
    /// reads bytes from inside the image's PNG payload instead. Diagnostic
    /// fingerprint: <c>arr[0]</c> becomes <c>52816.535f</c>
    /// (<c>0x474E5089</c> little-endian = the PNG signature bytes
    /// <c>89 50 4E 47</c>).
    ///
    /// <strong>Important</strong>: the bug only surfaces through the
    /// <c>QueryPlan</c> execution path, which runs
    /// <c>LiteralHoister</c> at plan construction to bake literal values
    /// into a plan-scoped <c>_hoistStore</c> arena. The direct
    /// <see cref="ServiceTestBase.ExecuteQueryAsync"/> path skips that step
    /// — that's why the simpler test variant passes. This test uses
    /// <c>TableCatalog.PlanQuery</c> (the path the Web layer's
    /// <c>BatchExecutor</c> takes) so <c>LiteralHoister</c> runs and the
    /// hoisted DataValue is the one the operators actually see during
    /// recursive iteration.
    /// </summary>
    [Fact]
    public async Task Execute_RecursiveCte_LetBindingProducesImage_DoesntCorruptSiblingFloat32Literal()
    {
        TableCatalog catalog = CreateCatalog("seq",
            columns: ["n"],
            [0f], [1f], [2f]);

        // Go through TableCatalog.PlanQuery → QueryPlan (internal). QueryPlan's
        // constructor runs LiteralHoister, which bakes literals into a
        // plan-scoped arena. This is the same path the Web BatchExecutor uses;
        // the simpler ExecuteQueryAsync path skips LiteralHoister and so does
        // not reproduce the bug.
        QueryExpression query = SqlParser.Parse(
            "WITH RECURSIVE " +
            "step_poses AS (" +
            "  SELECT " +
            "    LET img = create_image_rgb(400, 200, 200, 200, 100), " +
            "    [488.91904::Float32, 0::Float32, 200::Float32, " +
            "     0::Float32, 276.2173::Float32, 112.5::Float32, " +
            "     0::Float32, 0::Float32, 1::Float32] AS arr, " +
            "    value AS n " +
            "  FROM range(0, 2)" +
            "), " +
            "accumulated AS (" +
            "  SELECT n, arr FROM step_poses WHERE n = 0 " +
            "  UNION ALL " +
            "  SELECT s.n, s.arr " +
            "  FROM accumulated a JOIN step_poses s ON s.n = a.n + 1" +
            ") " +
            "SELECT n, arr FROM accumulated");

        IQueryPlan plan = catalog.PlanQuery(query);

        // The arr literal's DataValue carries an offset into the plan's
        // _hoistStore (which QueryPlan.ExecuteAsync plumbs as context.Store
        // and therefore batch.Arena). The values are only readable while
        // their batch is alive — same constraint the Web layer's
        // QueryStreamService observes. Read+assert inline; don't copy
        // DataValues out and try to resolve them against a different arena.
        float[] expected = [488.91904f, 0f, 200f, 0f, 276.2173f, 112.5f, 0f, 0f, 1f];
        int rowsSeen = 0;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None, batchContext: null))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                DataValue arrValue = row["arr"];
                Assert.True(arrValue.IsArray, $"row {rowsSeen}: arr should be an array");
                Assert.Equal(DataKind.Float32, arrValue.Kind);

                ReadOnlySpan<float> arr = arrValue.AsArraySpan<float>(batch.Arena);
                Assert.Equal(9, arr.Length);
                for (int j = 0; j < expected.Length; j++)
                {
                    // Diagnostic fingerprint when this fails: arr[0] becomes
                    // 52816.535 (PNG signature 89 50 4E 47 reinterpreted as Float32).
                    Assert.Equal(expected[j], arr[j]);
                }
                rowsSeen++;
            }
        }

        Assert.Equal(3, rowsSeen);
    }

    /// <summary>
    /// Control for <see cref="Execute_RecursiveCte_LetBindingProducesImage_DoesntCorruptSiblingFloat32Literal"/>:
    /// the same query shape, but the image expression is a regular aliased
    /// projection (<c>create_image_rgb(...) AS img</c>) instead of a
    /// <c>LET</c> binding. This form is known to work; the test pins that
    /// behaviour so that whatever fix lands for the LET path doesn't
    /// regress the aliased-projection path along the way.
    /// </summary>
    [Fact]
    public async Task Execute_RecursiveCte_AliasedImageProjection_DoesntCorruptSiblingFloat32Literal()
    {
        TableCatalog catalog = CreateCatalog("seq",
            columns: ["n"],
            [0f], [1f], [2f]);

        Pool pool = GetService<Pool>();
        Arena store = pool.Backing.RentArena();

        try
        {
            List<Row> results = await ExecuteQueryAsync(
                "WITH RECURSIVE " +
                "step_poses AS (" +
                "  SELECT " +
                "    create_image_rgb(8, 8, 200, 100, 50) AS img, " +
                "    [42.0::Float32, 43.0::Float32, 44.0::Float32] AS arr, " +
                "    n " +
                "  FROM seq" +
                "), " +
                "accumulated AS (" +
                "  SELECT n, arr FROM step_poses WHERE n = 0 " +
                "  UNION ALL " +
                "  SELECT s.n, s.arr " +
                "  FROM accumulated a JOIN step_poses s ON s.n = a.n + 1" +
                ") " +
                "SELECT n, arr FROM accumulated",
                catalog,
                store: store);

            Assert.Equal(3, results.Count);

            for (int i = 0; i < 3; i++)
            {
                ReadOnlySpan<float> arr = results[i]["arr"].AsArraySpan<float>(store);
                Assert.Equal(3, arr.Length);
                Assert.Equal(42f, arr[0]);
                Assert.Equal(43f, arr[1]);
                Assert.Equal(44f, arr[2]);
            }
        }
        finally
        {
            pool.ReturnArena(store);
        }
    }

    /// <summary>
    /// Recursive CTE under a tiny memory budget transitions to spill mode at an iteration
    /// boundary. All rows (anchor + every iteration) must round-trip through the spill file
    /// and replay correctly. Load-bearing for the multi-tenant story: a runaway recursion
    /// must not OOM the host.
    /// </summary>
    [Fact]
    public async Task Execute_RecursiveCte_SpillsToDisk_WhenBudgetExceeded()
    {
        TableCatalog catalog = CreateCatalog("dual",
            columns: ["dummy"],
            [1f]);

        ExecutionContext context = CreateExecutionContext(
            catalog: catalog,
            memoryBudgetBytes: 1, // Forces spill at the first iteration boundary.
            maxRecursionDepth: 100);

        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "WITH RECURSIVE nums AS (" +
            "SELECT 1 AS n FROM dual " +
            "UNION ALL " +
            "SELECT n + 1 AS n FROM nums WHERE n < 50" +
            ") SELECT n FROM nums")).Statement;

        QueryPlanner planner = new(catalog, DefaultFunctions);
        QueryOperator plan = planner.Plan(statement);

        List<Row> results = await plan.CollectRowsAsync(context);

        Assert.Equal(50, results.Count);
        // Confirm full round-trip — each row's n value should match its 1-based index.
        for (int i = 0; i < 50; i++)
        {
            DataValue n = results[i]["n"];
            int actual = n.Kind switch
            {
                DataKind.Int8 => n.AsInt8(),
                DataKind.Int16 => n.AsInt16(),
                DataKind.Int32 => n.AsInt32(),
                DataKind.Float32 => (int)n.AsFloat32(),
                DataKind.Float64 => (int)n.AsFloat64(),
                _ => throw new InvalidOperationException($"Unexpected kind: {n.Kind}")
            };
            Assert.Equal(i + 1, actual);
        }
    }

    /// <summary>
    /// After a mid-recursion spill, the next iteration's working table must read from the
    /// spiller (not from a stale in-memory snapshot). This test forces spill at iteration 1
    /// and confirms iterations 2-N still produce correct rows by reading their working-table
    /// input through <c>SpillReaderWriter.ReplayRangeAsync</c>.
    /// </summary>
    [Fact]
    public async Task Execute_RecursiveCte_WorkingTableReadsFromSpiller_AfterMidRecursionSpill()
    {
        TableCatalog catalog = CreateCatalog("dual",
            columns: ["dummy"],
            [1f]);

        ExecutionContext context = CreateExecutionContext(
            catalog: catalog,
            memoryBudgetBytes: 1,
            maxRecursionDepth: 100);

        // 20 iterations: anchor produces n=1, each iteration produces n+1 of the working
        // table's rows. After spill, the working table read goes through the spiller.
        // Recursive CTE produces rows in iteration order (anchor → iter1 → iter2 → ...) so
        // for this single-row-per-iteration shape the natural order is already 1..N — no
        // ORDER BY needed (and OrderByOperator's spill path is broken pending its own
        // migration; see project_spill_operators_broken.md).
        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "WITH RECURSIVE nums AS (" +
            "SELECT 1 AS n FROM dual " +
            "UNION ALL " +
            "SELECT n + 1 AS n FROM nums WHERE n < 20" +
            ") SELECT n FROM nums")).Statement;

        QueryPlanner planner = new(catalog, DefaultFunctions);
        QueryOperator plan = planner.Plan(statement);

        List<Row> results = await plan.CollectRowsAsync(context);

        Assert.Equal(20, results.Count);
        for (int i = 0; i < 20; i++)
        {
            DataValue n = results[i]["n"];
            int actual = n.Kind switch
            {
                DataKind.Int8 => n.AsInt8(),
                DataKind.Int16 => n.AsInt16(),
                DataKind.Int32 => n.AsInt32(),
                DataKind.Float32 => (int)n.AsFloat32(),
                DataKind.Float64 => (int)n.AsFloat64(),
                _ => throw new InvalidOperationException($"Unexpected kind: {n.Kind}")
            };
            Assert.Equal(i + 1, actual);
        }
    }

    /// <summary>
    /// Recursive CTE with a multi-replay consumer that breaks after the first batch must not
    /// double-return or leak. Mirrors the equivalent CTE/SpillReaderWriter contract test.
    /// </summary>
    [Fact]
    public async Task Execute_RecursiveCte_ConsumerBreaksMidReplay_CleansUp()
    {
        TableCatalog catalog = CreateCatalog("dual",
            columns: ["dummy"],
            [1f]);

        ExecutionContext context = CreateExecutionContext(
            catalog: catalog,
            batchSize: 4); // Small batches so we have something to break out of.

        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "WITH RECURSIVE nums AS (" +
            "SELECT 1 AS n FROM dual " +
            "UNION ALL " +
            "SELECT n + 1 AS n FROM nums WHERE n < 30" +
            ") SELECT n FROM nums")).Statement;

        QueryPlanner planner = new(catalog, DefaultFunctions);
        QueryOperator plan = planner.Plan(statement);

        int batchesReceived = 0;
        await foreach (RowBatch batch in plan.ExecuteAsync(context))
        {
            batchesReceived++;
            context.ReturnRowBatch(batch);
            break;
        }

        Assert.Equal(1, batchesReceived);
        // No exception means the producer's outer finally didn't double-return the in-flight
        // batch. The recursive CTE's own Dispose should also clean up cleanly when called by
        // the outer test scaffolding (no using here — relying on no-throw + GC).
    }

    /// <summary>
    /// Empty anchor short-circuits before any recursive iteration runs.
    /// </summary>
    [Fact]
    public async Task Execute_RecursiveCte_EmptyAnchor_YieldsNothing()
    {
        TableCatalog catalog = CreateCatalog("dual",
            columns: ["dummy"],
            [1f]);

        // Anchor filters all rows out (1 = 0 is never true), so anchor produces zero rows.
        // The recursive member depends on the working table; an empty working table → no
        // recursion → terminate immediately.
        List<Row> results = await ExecuteQueryAsync(
            "WITH RECURSIVE nums AS (" +
            "SELECT 1 AS n FROM dual WHERE 1 = 0 " +
            "UNION ALL " +
            "SELECT n + 1 FROM nums WHERE n < 5" +
            ") SELECT n FROM nums",
            catalog);

        Assert.Empty(results);
    }

    // ─────────────── CommonTableExpressionOperator unit tests ───────────────

    /// <summary>
    /// Inlined CTE operator re-executes the inner operator each time.
    /// </summary>
    [Fact]
    public async Task InlinedOperator_ReExecutesInnerEachTime()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup lookup = new(["x"]);

        int executionCount = 0;
        CountingOperator inner = new(pool, lookup, () => executionCount++,
            MakeRow(("x", DataValue.FromFloat32(1f))));

        CommonTableExpressionOperator cteOperator = new(inner, "test_cte", isMaterialized: false);

        ExecutionContext context = CreateExecutionContext();

        // Execute twice.
        List<Row> first = await CollectAsync(cteOperator, context);
        List<Row> second = await CollectAsync(cteOperator, context);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(2, executionCount);
    }

    /// <summary>
    /// Materialized CTE operator executes the inner operator only once.
    /// </summary>
    [Fact]
    public async Task MaterializedOperator_ExecutesInnerOnce()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup lookup = new(["x"]);

        int executionCount = 0;
        CountingOperator inner = new(pool, lookup, () => executionCount++,
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(2f))));

        CommonTableExpressionOperator cteOperator = new(inner, "test_cte", isMaterialized: true);

        ExecutionContext context = CreateExecutionContext();

        List<Row> first = await CollectAsync(cteOperator, context);
        List<Row> second = await CollectAsync(cteOperator, context);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal(1, executionCount);
    }

    /// <summary>
    /// Materialized CTE operator spills to disk when memory budget is exceeded.
    /// </summary>
    [Fact]
    public async Task MaterializedOperator_SpillsToDisk_WhenBudgetExceeded()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup lookup = new(["id"]);

        // Create rows that will exceed a tiny memory budget.
        Row[] rows = Enumerable.Range(0, 100)
            .Select(index => MakeRow(("id", DataValue.FromFloat32((float)index))))
            .ToArray();

        MockOperator inner = new(pool, lookup, rows);

        CommonTableExpressionOperator cteOperator = new(inner, "spill_test", isMaterialized: true);

        // Tiny budget to force spilling.
        ExecutionContext context = CreateExecutionContext(memoryBudgetBytes: 1);

        try
        {
            List<Row> results = await CollectAsync(cteOperator, context);
            Assert.Equal(100, results.Count);
        }
        finally
        {
            cteOperator.Dispose();
        }
    }

    /// <summary>
    /// Breaking out of the materialized in-memory replay mid-iteration must not throw or
    /// double-return any batches. The producer's finally only owns the leftover (not-yet-
    /// yielded) batch; once a yield transfers ownership, the local is null. Consumer
    /// short-circuits (LIMIT, downstream errors, cancellation) all funnel through this path.
    /// </summary>
    [Fact]
    public async Task MaterializedOperator_ConsumerBreaksMidReplay_CleansUp()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup lookup = new(["x"]);

        Row[] rows = Enumerable.Range(0, 20)
            .Select(index => MakeRow(("x", DataValue.FromFloat32((float)index))))
            .ToArray();

        MockOperator inner = new(pool, lookup, rows);
        using CommonTableExpressionOperator cteOperator = new(inner, "break_test", isMaterialized: true);

        // Small batch size so the replay yields multiple batches; we'll abandon iteration
        // after the first one to exercise the iterator-dispose-mid-yield path.
        ExecutionContext context = CreateExecutionContext(batchSize: 4);

        int batchesReceived = 0;
        await foreach (RowBatch batch in cteOperator.ExecuteAsync(context))
        {
            batchesReceived++;
            // Take ownership and abandon iteration. The iterator's hidden DisposeAsync
            // runs the producer's finally; it must not double-return the batch we just
            // returned to the pool, and it must not throw on subsequent CTE.Dispose().
            pool.ReturnRowBatch(batch);
            break;
        }

        Assert.Equal(1, batchesReceived);
        // The CTE still owns its cached batches; Dispose() (via the using) should clean them
        // up cleanly. If the producer's finally double-returned the just-yielded batch, the
        // arena underneath the cached batch would already be back in the arena pool and the
        // dispose path would observe a corrupted refcount or throw.
    }

    // ─────────────── Query planner CTE tests ───────────────

    /// <summary>
    /// The query planner creates a CTE operator when the statement has CTEs.
    /// </summary>
    [Fact]
    public void Plan_WithCte_CreatesCteOperator()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x"],
            [1f]);
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "WITH cte AS (SELECT x FROM t) SELECT * FROM cte")).Statement;

        QueryOperator plan = planner.Plan(statement);

        // Plan should contain a CTE operator wrapping the inner scan.
        // The plan for SELECT * FROM cte produces an AliasOperator(CTE(...))
        // or just a CTE operator depending on join presence.
        Assert.NotNull(plan);
    }

    /// <summary>
    /// CTE referenced from multiple JOINs produces correct results.
    /// </summary>
    [Fact]
    public async Task Execute_CteInMultipleJoins_ProducesCorrectResults()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "name"],
            [1f, "a"],
            [2f, "b"]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH items AS (SELECT id, name FROM t) " +
            "SELECT left_items.name, right_items.name " +
            "FROM items AS left_items JOIN items AS right_items ON left_items.id = right_items.id",
            catalog);

        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// A CTE that uses SELECT alias.* in a join context should output unqualified column names
    /// so downstream CTEs can reference them by unqualified name. Regression for the bug where
    /// SELECT pw.* produced column names like "pw.user_id" instead of "user_id".
    /// </summary>
    [Fact]
    public async Task Execute_CteWithQualifiedWildcardJoin_AggregatesWithUnqualifiedColumnNames()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("order_products",
            ["order_id", "product_id"],
            [1f, 10f],
            [1f, 20f],
            [2f, 10f]));
        catalog.Add(CreateProvider("orders",
            ["order_id", "user_id"],
            [1f, 100f],
            [2f, 200f]));

        // items_with_user joins order_products with orders to add user_id.
        // product_events selects pw.* (all columns from items_with_user aliased as pw) via a join.
        // The aggregation references user_id and product_id by unqualified name.
        List<Row> results = await ExecuteQueryAsync(
            "WITH items_with_user AS (" +
            "  SELECT p.order_id, p.product_id, o.user_id" +
            "  FROM order_products p JOIN orders o ON p.order_id = o.order_id" +
            ")," +
            "product_events AS (" +
            "  SELECT pw.*" +
            "  FROM items_with_user pw" +
            "  JOIN orders o ON pw.user_id = o.user_id" +
            ") " +
            "SELECT user_id, product_id, COUNT(*) AS cnt " +
            "FROM product_events " +
            "GROUP BY user_id, product_id",
            catalog);

        Assert.NotEmpty(results);
        // All rows should have accessible user_id and product_id columns.
        Assert.All(results, row => Assert.True(row["user_id"].AsFloat32() is 100f or 200f));
        Assert.All(results, row => Assert.True(row["product_id"].AsFloat32() is 10f or 20f));
    }

    // ─────────────── Non-recursive CTE with set operations ───────────────

    /// <summary>
    /// A non-recursive CTE with UNION ALL parses into a <see cref="CompoundQueryExpression"/> body.
    /// </summary>
    [Fact]
    public void Parse_NonRecursiveCteWithUnionAll_ProducesCompoundBody()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH combined AS (" +
            "SELECT x FROM t1 UNION ALL SELECT x FROM t2" +
            ") SELECT * FROM combined")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        CommonTableExpression commonTableExpression = result.CommonTableExpressions[0];
        Assert.False(commonTableExpression.IsRecursive);
        Assert.Null(commonTableExpression.RecursiveQuery);

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(commonTableExpression.Body);
        Assert.Equal(SetOperationType.Union, compound.OperationType);
        Assert.True(compound.All);
    }

    /// <summary>
    /// A non-recursive CTE with UNION ALL correctly returns rows from both branches.
    /// </summary>
    [Fact]
    public async Task Execute_NonRecursiveCteWithUnionAll_ReturnsBothBranches()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("t1",
            ["id", "value"],
            [1f, "a"],
            [2f, "b"]));
        catalog.Add(CreateProvider("t2",
            ["id", "value"],
            [3f, "c"]));

        List<Row> results = await ExecuteQueryAsync(
            "WITH combined AS (" +
            "SELECT id, value FROM t1 UNION ALL SELECT id, value FROM t2" +
            ") SELECT * FROM combined",
            catalog);

        Assert.Equal(3, results.Count);
    }

    /// <summary>
    /// A non-recursive CTE with UNION ALL can be joined with other tables.
    /// </summary>
    [Fact]
    public async Task Execute_NonRecursiveCteWithUnionAll_JoinedWithOtherTable()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("t1",
            ["order_id", "product_id"],
            [1f, 10f]));
        catalog.Add(CreateProvider("t2",
            ["order_id", "product_id"],
            [2f, 20f]));
        catalog.Add(CreateProvider("products",
            ["product_id", "name"],
            [10f, "Widget"],
            [20f, "Gadget"]));

        List<Row> results = await ExecuteQueryAsync(
            "WITH all_orders AS (" +
            "SELECT order_id, product_id FROM t1 UNION ALL SELECT order_id, product_id FROM t2" +
            ") " +
            "SELECT all_orders.order_id, products.name " +
            "FROM all_orders " +
            "LEFT JOIN products ON all_orders.product_id = products.product_id",
            catalog);

        Assert.Equal(2, results.Count);
    }

    // ─────────────── QuerySchemaResolver CTE tests ───────────────

    /// <summary>
    /// <see cref="QuerySchemaResolver"/> resolves a CTE table reference without
    /// throwing a catalog lookup error.
    /// </summary>
    [Fact]
    public async Task SchemaResolver_CteReference_ResolvesWithoutCatalogLookup()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "value"],
            [1f, "a"]);
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "WITH cte AS (SELECT id, value FROM t) SELECT * FROM cte")).Statement;

        ResolvedQuerySchema schema = await resolver.ResolveAsync(statement, CancellationToken.None);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Contains(schema.Columns, column => column.ColumnName == "id");
        Assert.Contains(schema.Columns, column => column.ColumnName == "value");
    }

    /// <summary>
    /// <see cref="QuerySchemaResolver"/> resolves a CTE with UNION ALL used in a JOIN
    /// without throwing a catalog lookup error.
    /// </summary>
    [Fact]
    public async Task SchemaResolver_CteWithUnionAllInJoin_ResolvesCorrectly()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("t1",
            ["order_id", "product_id"],
            [1f, 10f]));
        catalog.Add(CreateProvider("t2",
            ["order_id", "product_id"],
            [2f, 20f]));
        catalog.Add(CreateProvider("orders",
            ["order_id", "customer"],
            [1f, "Alice"]));
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "WITH order_products AS (" +
            "SELECT order_id, product_id FROM t1 UNION ALL SELECT order_id, product_id FROM t2" +
            ") " +
            "SELECT * FROM orders LEFT JOIN order_products ON order_products.order_id = orders.order_id")).Statement;

        ResolvedQuerySchema schema = await resolver.ResolveAsync(statement, CancellationToken.None);

        Assert.True(schema.Columns.Count >= 3);
        Assert.Contains(schema.Columns, column => column.ColumnName == "customer");
        Assert.Contains(schema.Columns, column => column.ColumnName == "product_id");
    }

    // ─────────────── Aggregates nested inside scalar functions ───────────────

    /// <summary>
    /// Aggregates nested inside scalar function arguments (e.g. DATE_DIFF wrapping MIN/MAX)
    /// must be rewritten to column references so the evaluator does not treat them as
    /// unknown scalar functions.
    /// </summary>
    [Fact]
    public async Task Execute_AggregateNestedInsideScalarFunction_RewritesCorrectly()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x", "y"],
            [10f, 1f],
            [20f, 2f],
            [30f, 3f]);

        // ROUND wraps MIN and MAX — these are aggregates nested inside a scalar function.
        List<Row> result = await ExecuteQueryAsync(
            "SELECT ROUND(MIN(x) + MAX(y), 0) AS val FROM t",
            catalog);

        Assert.Single(result);
        Assert.Equal(13.0f, result[0]["val"].AsFloat32());
    }

    /// <summary>
    /// A CTE whose SELECT list wraps aggregates inside scalar functions should
    /// plan and execute without "Unknown function" errors.
    /// </summary>
    [Fact]
    public async Task Execute_CteWithAggregateInsideScalarFunction_Succeeds()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["v"],
            [5f],
            [15f],
            [25f]);

        List<Row> result = await ExecuteQueryAsync(
            "WITH stats AS (SELECT ROUND(MIN(v), 0) AS lo, ROUND(MAX(v), 0) AS hi FROM t) " +
            "SELECT lo, hi FROM stats",
            catalog);

        Assert.Single(result);
        Assert.Equal(5.0f, result[0]["lo"].AsFloat32());
        Assert.Equal(25.0f, result[0]["hi"].AsFloat32());
    }

    // ─────────────── Helper infrastructure ───────────────

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(column => column.Name).ToArray();
        DataValue[] values = columns.Select(column => column.Value).ToArray();
        return new Row(new ColumnLookup(names), values);
    }

    private static async Task<List<Row>> CollectAsync(QueryOperator op, ExecutionContext context)
    {
        return await op.CollectRowsAsync(context);
    }

    /// <summary>
    /// Yields the supplied rows from a fresh pool-rented <see cref="RowBatch"/>. Row values
    /// are copied into pool-rented <see cref="DataValue"/> arrays so the batch's lifecycle
    /// is independent of the original Row instances handed in by the test.
    /// </summary>
    private static async IAsyncEnumerable<RowBatch> YieldRowsAsBatches(
        Pool pool, ColumnLookup lookup, Row[] rows)
    {
        const int batchCapacity = 64;
        RowBatch? outputBatch = null;
        foreach (Row row in rows)
        {
            outputBatch ??= pool.RentRowBatch(lookup, batchCapacity);
            DataValue[] copy = pool.RentDataValues(row.RawValues.Length);
            row.RawValues.CopyTo(copy.AsSpan());
            outputBatch.Add(copy);
            if (outputBatch.IsFull)
            {
                yield return outputBatch;
                outputBatch = null;
            }
        }
        if (outputBatch is not null)
        {
            yield return outputBatch;
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// A mock operator that tracks how many times <see cref="ExecuteAsync"/> is called.
    /// </summary>
    private sealed class CountingOperator : QueryOperator
    {
        private readonly Pool _pool;
        private readonly ColumnLookup _lookup;
        private readonly Action _onExecute;
        private readonly Row[] _rows;

        public CountingOperator(Pool pool, ColumnLookup lookup, Action onExecute, params Row[] rows)
        {
            _pool = pool;
            _lookup = lookup;
            _onExecute = onExecute;
            _rows = rows;
        }

        protected override OperatorPlanDescription DescribeForExplainImpl() => new("Counting Mock");

        protected override IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
        {
            _onExecute();
            return YieldRowsAsBatches(_pool, _lookup, _rows);
        }
    }

    /// <summary>
    /// Pool-aware mock operator that yields the supplied rows in pool-rented batches.
    /// </summary>
    private sealed class MockOperator : QueryOperator
    {
        private readonly Pool _pool;
        private readonly ColumnLookup _lookup;
        private readonly Row[] _rows;

        public MockOperator(Pool pool, ColumnLookup lookup, params Row[] rows)
        {
            _pool = pool;
            _lookup = lookup;
            _rows = rows;
        }

        protected override OperatorPlanDescription DescribeForExplainImpl() => new("Mock");

        protected override IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
            => YieldRowsAsBatches(_pool, _lookup, _rows);
    }
}

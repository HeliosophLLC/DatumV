using System.Text;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="IndexNestedLoopJoinExecutor"/> — both direct executor tests
/// and integration tests through <see cref="JoinOperator"/> dispatch. Indexes are
/// built via <see cref="BPlusTreeBulkLoader"/> and consumed as <see cref="IColumnIndex"/>.
/// </summary>
public sealed class IndexNestedLoopJoinTests : ServiceTestBase
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();
    private static readonly byte[] DummyHash = new byte[32];

    // ───────────────────── Direct executor tests ─────────────────────

    [Fact]
    public async Task InnerJoin_AllKeysMatch_ProducesCorrectCombinedRows()
    {
        Row[] buildRows =
        [
            MakeRow(["id", "name"], DataValue.FromFloat32(1f), DataValue.FromString("Alice")),
            MakeRow(["id", "name"], DataValue.FromFloat32(2f), DataValue.FromString("Bob")),
            MakeRow(["id", "name"], DataValue.FromFloat32(3f), DataValue.FromString("Charlie")),
        ];

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(2f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(3f), ChunkIndex: 0, RowOffsetInChunk: 2),
        ];

        IColumnIndex columnIndex = BuildBPlusTreeColumnIndex("id", entries, DataKind.Float32);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(0, 3)];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);

        MockOperator probe = CreateMockOperator(
            ["p.id", "p.score"],
            [DataValue.FromFloat32(1f), DataValue.FromFloat32(95f)],
            [DataValue.FromFloat32(3f), DataValue.FromFloat32(87f)]);

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        ExpressionEvaluator evaluator = new(context);

        IndexNestedLoopJoinExecutor executor = new(
            buildProvider, JoinType.Inner, extraction, columnIndex, chunks,
            buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, EmptyOperator(), context));

        Assert.Equal(2, results.Count);

        Row first = results[0];
        Assert.Equal(1f, first["p.id"].AsFloat32());
        Assert.Equal(95f, first["p.score"].AsFloat32());
        Assert.Equal(1f, first["id"].AsFloat32());
        Assert.Equal("Alice", first["name"].AsString());

        Row second = results[1];
        Assert.Equal(3f, second["p.id"].AsFloat32());
        Assert.Equal(87f, second["p.score"].AsFloat32());
        Assert.Equal(3f, second["id"].AsFloat32());
        Assert.Equal("Charlie", second["name"].AsString());
    }

    [Fact]
    public async Task InnerJoin_NoMatch_YieldsNoRows()
    {
        Row[] buildRows =
        [
            MakeRow(["id", "val"], DataValue.FromFloat32(10f), DataValue.FromString("X")),
        ];

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(10f), ChunkIndex: 0, RowOffsetInChunk: 0),
        ];

        IColumnIndex columnIndex = BuildBPlusTreeColumnIndex("id", entries, DataKind.Float32);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(0, 1)];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);

        MockOperator probe = CreateMockOperator(
            ["p.id"],
            [DataValue.FromFloat32(1f)],
            [DataValue.FromFloat32(2f)]);

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        ExpressionEvaluator evaluator = new(context);

        IndexNestedLoopJoinExecutor executor = new(
            buildProvider, JoinType.Inner, extraction, columnIndex, chunks,
            buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, EmptyOperator(), context));

        Assert.Empty(results);
    }

    [Fact]
    public async Task InnerJoin_NullProbeKey_SkipsRow()
    {
        Row[] buildRows =
        [
            MakeRow(["id", "val"], DataValue.FromFloat32(1f), DataValue.FromString("A")),
        ];

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
        ];

        IColumnIndex columnIndex = BuildBPlusTreeColumnIndex("id", entries, DataKind.Float32);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(0, 1)];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);

        MockOperator probe = CreateMockOperator(
            ["p.id"],
            [DataValue.Null(DataKind.Float32)],
            [DataValue.FromFloat32(1f)]);

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        ExpressionEvaluator evaluator = new(context);

        IndexNestedLoopJoinExecutor executor = new(
            buildProvider, JoinType.Inner, extraction, columnIndex, chunks,
            buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, EmptyOperator(), context));

        Assert.Single(results);
        Assert.Equal(1f, results[0]["p.id"].AsFloat32());
        Assert.Equal("A", results[0]["val"].AsString());
    }

    [Fact]
    public async Task LeftSemiJoin_MatchExists_YieldsProbeRowOnly()
    {
        Row[] buildRows =
        [
            MakeRow(["id", "extra"], DataValue.FromFloat32(1f), DataValue.FromString("ignored")),
            MakeRow(["id", "extra"], DataValue.FromFloat32(2f), DataValue.FromString("also ignored")),
        ];

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(2f), ChunkIndex: 0, RowOffsetInChunk: 1),
        ];

        IColumnIndex columnIndex = BuildBPlusTreeColumnIndex("id", entries, DataKind.Float32);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(0, 2)];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);

        MockOperator probe = CreateMockOperator(
            ["p.id", "p.val"],
            [DataValue.FromFloat32(1f), DataValue.FromString("keep")],
            [DataValue.FromFloat32(99f), DataValue.FromString("no match")]);

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        ExpressionEvaluator evaluator = new(context);

        IndexNestedLoopJoinExecutor executor = new(
            buildProvider, JoinType.LeftSemi, extraction, columnIndex, chunks,
            buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, EmptyOperator(), context));

        Assert.Single(results);
        Assert.Equal(1f, results[0]["p.id"].AsFloat32());
        Assert.Equal("keep", results[0]["p.val"].AsString());

        // Build-side columns should NOT be present.
        Assert.False(results[0].TryGetValue("extra", out _));
    }

    [Fact]
    public async Task InnerJoin_DuplicateBuildKeys_ProducesMultipleRows()
    {
        Row[] buildRows =
        [
            MakeRow(["id", "val"], DataValue.FromFloat32(1f), DataValue.FromString("A")),
            MakeRow(["id", "val"], DataValue.FromFloat32(1f), DataValue.FromString("B")),
            MakeRow(["id", "val"], DataValue.FromFloat32(2f), DataValue.FromString("C")),
        ];

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(2f), ChunkIndex: 0, RowOffsetInChunk: 2),
        ];

        IColumnIndex columnIndex = BuildBPlusTreeColumnIndex("id", entries, DataKind.Float32);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(0, 3)];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);

        MockOperator probe = CreateMockOperator(
            ["p.id"],
            [DataValue.FromFloat32(1f)]);

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        ExpressionEvaluator evaluator = new(context);

        IndexNestedLoopJoinExecutor executor = new(
            buildProvider, JoinType.Inner, extraction, columnIndex, chunks,
            buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, EmptyOperator(), context));

        Assert.Equal(2, results.Count);
        HashSet<string> vals = results.Select(r => r["val"].AsString()).ToHashSet();
        Assert.Equal(new HashSet<string> { "A", "B" }, vals);
    }

    [Fact]
    public async Task InnerJoin_MultipleChunks_ResolvesCorrectAbsoluteRow()
    {
        Row[] buildRows =
        [
            MakeRow(["id", "val"], DataValue.FromFloat32(10f), DataValue.FromString("chunk0-row0")),
            MakeRow(["id", "val"], DataValue.FromFloat32(20f), DataValue.FromString("chunk0-row1")),
            MakeRow(["id", "val"], DataValue.FromFloat32(30f), DataValue.FromString("chunk0-row2")),
            MakeRow(["id", "val"], DataValue.FromFloat32(40f), DataValue.FromString("chunk1-row0")),
            MakeRow(["id", "val"], DataValue.FromFloat32(50f), DataValue.FromString("chunk1-row1")),
            MakeRow(["id", "val"], DataValue.FromFloat32(60f), DataValue.FromString("chunk1-row2")),
        ];

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(10f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(20f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(30f), ChunkIndex: 0, RowOffsetInChunk: 2),
            new(DataValue.FromFloat32(40f), ChunkIndex: 1, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(50f), ChunkIndex: 1, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(60f), ChunkIndex: 1, RowOffsetInChunk: 2),
        ];

        IColumnIndex columnIndex = BuildBPlusTreeColumnIndex("id", entries, DataKind.Float32);
        IReadOnlyList<IndexChunk> chunks =
        [
            CreateChunk(0, 3),
            CreateChunk(3, 3),
        ];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);

        // Probe for key=50 (chunk 1, offset 1 → absolute row 4).
        MockOperator probe = CreateMockOperator(
            ["p.id"],
            [DataValue.FromFloat32(50f)]);

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        ExpressionEvaluator evaluator = new(context);

        IndexNestedLoopJoinExecutor executor = new(
            buildProvider, JoinType.Inner, extraction, columnIndex, chunks,
            buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, EmptyOperator(), context));

        Assert.Single(results);
        Assert.Equal(50f, results[0]["id"].AsFloat32());
        Assert.Equal("chunk1-row1", results[0]["val"].AsString());
    }

    [Fact]
    public async Task LeftSemiJoin_DuplicateBuildKeys_YieldsProbeRowOnce()
    {
        Row[] buildRows =
        [
            MakeRow(["id", "val"], DataValue.FromFloat32(1f), DataValue.FromString("A")),
            MakeRow(["id", "val"], DataValue.FromFloat32(1f), DataValue.FromString("B")),
        ];

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 1),
        ];

        IColumnIndex columnIndex = BuildBPlusTreeColumnIndex("id", entries, DataKind.Float32);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(0, 2)];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);

        MockOperator probe = CreateMockOperator(
            ["p.id", "p.x"],
            [DataValue.FromFloat32(1f), DataValue.FromFloat32(42f)]);

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        ExpressionEvaluator evaluator = new(context);

        IndexNestedLoopJoinExecutor executor = new(
            buildProvider, JoinType.LeftSemi, extraction, columnIndex, chunks,
            buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, EmptyOperator(), context));

        // SEMI: probe row emitted once even though there are 2 build matches.
        Assert.Single(results);
        Assert.Equal(42f, results[0]["p.x"].AsFloat32());
    }

    // ───────────────────── JoinOperator dispatch integration tests ─────────────────────

    [Fact]
    public async Task JoinOperator_SelectsIndexNlj_WhenSortedIndexAvailable()
    {
        Row[] buildRows =
        [
            MakeRow(["id", "name"], DataValue.FromFloat32(1f), DataValue.FromString("Alice")),
            MakeRow(["id", "name"], DataValue.FromFloat32(2f), DataValue.FromString("Bob")),
            MakeRow(["id", "name"], DataValue.FromFloat32(3f), DataValue.FromString("Charlie")),
        ];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);
        SourceIndex sourceIndex = BuildSourceIndexWithBPlusTree(
            "id", buildRows, DataKind.Float32);
        buildProvider.ProvideSourceIndex(sourceIndex);

        ScanOperator buildScan = new(buildProvider, requiredColumns: null, buildRows.Length);

        MockOperator probeOperator = CreateMockOperator(
            ["p.id", "p.score"],
            [DataValue.FromFloat32(1f), DataValue.FromFloat32(95f)],
            [DataValue.FromFloat32(3f), DataValue.FromFloat32(87f)]);

        JoinOperator join = new(probeOperator, buildScan, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("p", "id"),
                BinaryOperator.Equal,
                new ColumnReference("id")));

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        ExecutionContext context = CreateContextWithRowLimit(catalog, 10);

        List<Row> results = await join.CollectRowsAsync(context);

        Assert.Equal(2, results.Count);

        Row alice = results.First(row => row["name"].AsString() == "Alice");
        Assert.Equal(95f, alice["p.score"].AsFloat32());

        Row charlie = results.First(row => row["name"].AsString() == "Charlie");
        Assert.Equal(87f, charlie["p.score"].AsFloat32());
    }

    [Fact]
    public async Task JoinOperator_IndexNlj_QualifiesBuildColumnsWithAlias()
    {
        Row[] buildRows =
        [
            MakeRow(["id", "name"], DataValue.FromFloat32(1f), DataValue.FromString("Alice")),
            MakeRow(["id", "name"], DataValue.FromFloat32(2f), DataValue.FromString("Bob")),
        ];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);
        SourceIndex sourceIndex = BuildSourceIndexWithBPlusTree(
            "id", buildRows, DataKind.Float32);
        buildProvider.ProvideSourceIndex(sourceIndex);

        ScanOperator buildScan = new(buildProvider, requiredColumns: null, buildRows.Length);
        AliasOperator aliasedBuild = new(buildScan, "right_table");

        MockOperator probeOperator = CreateMockOperator(
            ["left_table.id", "left_table.score"],
            [DataValue.FromFloat32(1f), DataValue.FromFloat32(95f)]);

        JoinOperator join = new(probeOperator, aliasedBuild, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("left_table", "id"),
                BinaryOperator.Equal,
                new ColumnReference("right_table", "id")));

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        ExecutionContext context = CreateContextWithRowLimit(catalog, 10);

        List<Row> results = await join.CollectRowsAsync(context);

        Assert.Single(results);
        Row row = results[0];

        // Build-side columns must be qualified with the alias.
        Assert.True(row.TryGetValue("right_table.id", out _),
            "Expected qualified column 'right_table.id'");
        Assert.True(row.TryGetValue("right_table.name", out _),
            "Expected qualified column 'right_table.name'");

        Assert.Equal(95f, row["left_table.score"].AsFloat32());
        Assert.Equal("Alice", row["right_table.name"].AsString());
    }

    [Fact]
    public async Task JoinOperator_FallsBackToHashJoin_WhenNoSortedIndex()
    {
        Row[] buildRows =
        [
            MakeRow(["r.id", "r.val"], DataValue.FromFloat32(1f), DataValue.FromString("X")),
            MakeRow(["r.id", "r.val"], DataValue.FromFloat32(2f), DataValue.FromString("Y")),
        ];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);
        // Provide a SourceIndex with no column index — planner can't pick INLJ.
        SourceIndex emptyIndex = new(
            new SourceFingerprint(100, DummyHash),
            new IndexSchema(SchemaFromRows(buildRows), buildRows.Length),
            [CreateChunk(0, buildRows.Length)]);
        buildProvider.ProvideSourceIndex(emptyIndex);

        ScanOperator buildScan = new(buildProvider, requiredColumns: null, buildRows.Length);

        MockOperator probeOperator = CreateMockOperator(
            ["l.id", "l.name"],
            [DataValue.FromFloat32(1f), DataValue.FromString("Alice")],
            [DataValue.FromFloat32(2f), DataValue.FromString("Bob")]);

        JoinOperator join = new(probeOperator, buildScan, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);

        List<Row> results = await join.CollectRowsAsync(context);

        // Hash join still produces correct results.
        Assert.Equal(2, results.Count);
        Assert.Contains(results, row => row["l.name"].AsString() == "Alice");
        Assert.Contains(results, row => row["l.name"].AsString() == "Bob");
    }

    [Fact]
    public async Task JoinOperator_DoesNotSelectIndexNlj_ForLeftJoin()
    {
        Row[] buildRows =
        [
            MakeRow(["r.id", "r.val"], DataValue.FromFloat32(1f), DataValue.FromString("X")),
        ];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);
        SourceIndex sourceIndex = BuildSourceIndexWithBPlusTree(
            "r.id", buildRows, DataKind.Float32);
        buildProvider.ProvideSourceIndex(sourceIndex);

        ScanOperator buildScan = new(buildProvider, requiredColumns: null, buildRows.Length);

        MockOperator probeOperator = CreateMockOperator(
            ["l.id", "l.name"],
            [DataValue.FromFloat32(1f), DataValue.FromString("Alice")],
            [DataValue.FromFloat32(999f), DataValue.FromString("NoMatch")]);

        JoinOperator join = new(probeOperator, buildScan, JoinType.Left,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        ExecutionContext context = CreateContextWithRowLimit(catalog, 10);

        List<Row> results = await join.CollectRowsAsync(context);

        // LEFT JOIN falls back to hash join: matched + unmatched-with-null-pad.
        Assert.Equal(2, results.Count);
    }

    // ───────────────────── PreferIndexNestedLoop flag tests ─────────────────────

    [Fact]
    public async Task JoinOperator_PreferIndexNestedLoop_ActivatesNljWithoutRowLimit()
    {
        Row[] buildRows =
        [
            MakeRow(["id", "name"], DataValue.FromFloat32(1f), DataValue.FromString("Alice")),
            MakeRow(["id", "name"], DataValue.FromFloat32(2f), DataValue.FromString("Bob")),
            MakeRow(["id", "name"], DataValue.FromFloat32(3f), DataValue.FromString("Charlie")),
        ];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);
        SourceIndex sourceIndex = BuildSourceIndexWithBPlusTree(
            "id", buildRows, DataKind.Float32);
        buildProvider.ProvideSourceIndex(sourceIndex);

        ScanOperator buildScan = new(buildProvider, requiredColumns: null, buildRows.Length);

        MockOperator probeOperator = CreateMockOperator(
            ["p.id", "p.score"],
            [DataValue.FromFloat32(1f), DataValue.FromFloat32(95f)],
            [DataValue.FromFloat32(3f), DataValue.FromFloat32(87f)]);

        JoinOperator join = new(
            probeOperator,
            buildScan,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("p", "id"),
                BinaryOperator.Equal,
                new ColumnReference("id")),
            preferIndexNestedLoop: true);

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        // No RowLimit set — NLJ activates via the PreferIndexNestedLoop flag alone.
        ExecutionContext context = CreateExecutionContext(catalog: catalog);

        List<Row> results = await join.CollectRowsAsync(context);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, row => row["name"].AsString() == "Alice");
        Assert.Contains(results, row => row["name"].AsString() == "Charlie");
    }

    [Fact]
    public async Task JoinOperator_PreferIndexNestedLoopFalse_RequiresRowLimitToActivateNlj()
    {
        Row[] buildRows =
        [
            MakeRow(["id", "name"], DataValue.FromFloat32(1f), DataValue.FromString("Alice")),
        ];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);
        SourceIndex sourceIndex = BuildSourceIndexWithBPlusTree(
            "id", buildRows, DataKind.Float32);
        buildProvider.ProvideSourceIndex(sourceIndex);

        ScanOperator buildScan = new(buildProvider, requiredColumns: null, buildRows.Length);

        MockOperator probeOperator = CreateMockOperator(
            ["p.id", "p.score"],
            [DataValue.FromFloat32(1f), DataValue.FromFloat32(99f)]);

        JoinOperator join = new(
            probeOperator,
            buildScan,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("p", "id"),
                BinaryOperator.Equal,
                new ColumnReference("id")));

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        // No RowLimit — hash join path is taken (NLJ not activated by flag).
        ExecutionContext context = CreateExecutionContext(catalog: catalog);

        List<Row> results = await join.CollectRowsAsync(context);

        Assert.Single(results);
        Assert.Equal("Alice", results[0]["name"].AsString());

        // Re-run with RowLimit set — NLJ activates and result is still correct.
        MockOperator probeOperator2 = CreateMockOperator(
            ["p.id", "p.score"],
            [DataValue.FromFloat32(1f), DataValue.FromFloat32(99f)]);

        JoinOperator joinWithLimit = new(
            probeOperator2,
            buildScan,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("p", "id"),
                BinaryOperator.Equal,
                new ColumnReference("id")));

        ExecutionContext contextWithLimit = CreateContextWithRowLimit(catalog, 10);

        List<Row> resultsWithLimit = await joinWithLimit.CollectRowsAsync(contextWithLimit);
        Assert.Single(resultsWithLimit);
        Assert.Equal("Alice", resultsWithLimit[0]["name"].AsString());
    }

    // ───────────────────── Circuit breaker tests ─────────────────────

    [Fact]
    public async Task CircuitBreaker_ExceedsTrialBudget_TripsAndYieldsNothing()
    {
        Row[] buildRows =
        [
            MakeRow(["id", "name"], DataValue.FromFloat32(1f), DataValue.FromString("Alice")),
        ];

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
        ];

        IColumnIndex columnIndex = BuildBPlusTreeColumnIndex("id", entries, DataKind.Float32);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(0, 1)];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);

        // 200 probe rows, all matching key=1. With RowLimit=10, trial budget = 100.
        object?[][] probeCells = Enumerable.Range(0, 200)
            .Select(i => new object?[] { DataValue.FromFloat32(1f), DataValue.FromFloat32(i) })
            .ToArray();

        MockOperator probe = CreateMockOperator(["p.id", "p.score"], probeCells);

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        ExecutionContext context = CreateContextWithRowLimit(catalog, 10);

        ExpressionEvaluator evaluator = new(context);

        IndexNestedLoopJoinExecutor executor = new(
            buildProvider, JoinType.Inner, extraction, columnIndex, chunks,
            buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, EmptyOperator(), context));

        Assert.Empty(results);
        Assert.True(executor.CircuitBreakerTripped,
            "Circuit breaker should trip when probe row count exceeds trial budget.");
    }

    [Fact]
    public async Task CircuitBreaker_WithinTrialBudget_CompletesNormally()
    {
        Row[] buildRows =
        [
            MakeRow(["id", "name"], DataValue.FromFloat32(1f), DataValue.FromString("Alice")),
        ];

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
        ];

        IColumnIndex columnIndex = BuildBPlusTreeColumnIndex("id", entries, DataKind.Float32);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(0, 1)];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);

        // 50 probe rows, all matching. With RowLimit=10, budget = 100. 50 < 100.
        object?[][] probeCells = Enumerable.Range(0, 50)
            .Select(i => new object?[] { DataValue.FromFloat32(1f), DataValue.FromFloat32(i) })
            .ToArray();

        MockOperator probe = CreateMockOperator(["p.id", "p.score"], probeCells);

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        ExecutionContext context = CreateContextWithRowLimit(catalog, 10);

        ExpressionEvaluator evaluator = new(context);

        IndexNestedLoopJoinExecutor executor = new(
            buildProvider, JoinType.Inner, extraction, columnIndex, chunks,
            buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, EmptyOperator(), context));

        Assert.Equal(50, results.Count);
        Assert.False(executor.CircuitBreakerTripped,
            "Circuit breaker should not trip when probe rows are within budget.");
    }

    [Fact]
    public async Task JoinOperator_CircuitBreakerTrips_FallsBackToHashJoinWithCorrectResults()
    {
        Row[] buildRows =
        [
            MakeRow(["r.id", "r.val"], DataValue.FromFloat32(1f), DataValue.FromString("A")),
            MakeRow(["r.id", "r.val"], DataValue.FromFloat32(2f), DataValue.FromString("B")),
            MakeRow(["r.id", "r.val"], DataValue.FromFloat32(3f), DataValue.FromString("C")),
        ];

        InMemoryTableProvider buildProvider = CreateInMemoryProvider("build", buildRows);
        SourceIndex sourceIndex = BuildSourceIndexWithBPlusTree(
            "r.id", buildRows, DataKind.Float32);
        buildProvider.ProvideSourceIndex(sourceIndex);

        ScanOperator buildScan = new(buildProvider, requiredColumns: null, buildRows.Length);

        // 200 probe rows cycling through ids 1-3. With RowLimit=10, trial budget = 100.
        object?[][] probeCells = Enumerable.Range(0, 200)
            .Select(i => new object?[]
            {
                DataValue.FromFloat32((i % 3) + 1f),
                DataValue.FromString($"Row{i}"),
            })
            .ToArray();

        MockOperator probeOperator = CreateMockOperator(["l.id", "l.name"], probeCells);

        JoinOperator join = new(
            probeOperator,
            buildScan,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        TableCatalog catalog = CreateCatalog();
        catalog.Add(buildProvider);

        ExecutionContext context = CreateContextWithRowLimit(catalog, 10);

        List<Row> results = await join.CollectRowsAsync(context);

        // All 200 probe rows match one of the 3 build rows → 200 result rows after fallback.
        Assert.Equal(200, results.Count);

        foreach (Row row in results)
        {
            float probeId = row["l.id"].AsFloat32();
            string buildVal = row["r.val"].AsString();
            string expected = probeId switch
            {
                1f => "A",
                2f => "B",
                3f => "C",
                _ => throw new Exception($"Unexpected probe id: {probeId}"),
            };
            Assert.Equal(expected, buildVal);
        }
    }

    // ───────────────────── Helpers ─────────────────────

    /// <summary>
    /// Builds an <see cref="ExecutionContext"/> with a non-null
    /// <see cref="ExecutionContext.RowLimit"/>. The base-class
    /// <c>CreateExecutionContext</c> can't set <c>RowLimit</c> because it's
    /// <c>init</c>-only, so this overload runs the full constructor with the
    /// initializer that sets it.
    /// </summary>
    private ExecutionContext CreateContextWithRowLimit(TableCatalog catalog, int rowLimit)
    {
        return new ExecutionContext(
            CancellationToken.None,
            DefaultFunctions,
            catalog,
            new LocalBufferPool(),
            GetService<Pool>())
        {
            RowLimit = rowLimit,
        };
    }

    private static IndexChunk CreateChunk(long rowOffset, long rowCount)
    {
        return new IndexChunk(rowOffset, rowCount, new Dictionary<string, ChunkColumnStatistics>());
    }

    private static IQueryOperator EmptyOperator() => new InertOperator();

    /// <summary>
    /// Standalone no-row operator for the executor's <c>buildOperator</c> parameter.
    /// The executor doesn't iterate it — index lookups + seek-session reads replace
    /// build-side materialization — so any <see cref="IQueryOperator"/> suffices.
    /// </summary>
    private sealed class InertOperator : IQueryOperator
    {
        public OperatorPlanDescription DescribeForExplain() => new("Inert");

#pragma warning disable CS1998
        public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
        {
            yield break;
        }
#pragma warning restore CS1998
    }

    private static Schema SchemaFromRows(Row[] rows)
    {
        Row first = rows[0];
        ColumnInfo[] columns = new ColumnInfo[first.FieldCount];
        for (int i = 0; i < first.FieldCount; i++)
        {
            columns[i] = new ColumnInfo(first.ColumnNames[i], first[i].Kind, false);
        }

        return new Schema(columns);
    }

    /// <summary>
    /// Builds a single-column <see cref="IColumnIndex"/> backed by a B+Tree from
    /// the given entries. Entries are sorted in-place before bulk-loading.
    /// </summary>
    private static IColumnIndex BuildBPlusTreeColumnIndex(
        string columnName, ValueIndexEntry[] entries, DataKind keyKind)
    {
        BPlusTreeIndexSet indexSet = BuildBPlusTreeIndexSet(columnName, entries, keyKind);
        if (!indexSet.TryGetIndex(columnName, out BPlusTreeColumnIndex? index))
        {
            throw new InvalidOperationException($"Column index for '{columnName}' was not built.");
        }

        return index;
    }

    private static BPlusTreeIndexSet BuildBPlusTreeIndexSet(
        string columnName, ValueIndexEntry[] entries, DataKind keyKind)
    {
        ValueIndexEntry[] sorted = (ValueIndexEntry[])entries.Clone();
        Array.Sort(sorted, (a, b) => StatisticsPredicateEvaluator.CompareValues(a.Key, b.Key));

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        BPlusTreeSectionHeader? header = BPlusTreeBulkLoader.Build(
            sorted, columnName, keyKind, writer);

        if (header is null)
        {
            throw new InvalidOperationException("BPlusTreeBulkLoader.Build returned null for non-empty entries.");
        }

        stream.Position = 0;
        using BinaryReader binaryReader = new(stream, Encoding.UTF8, leaveOpen: true);
        BPlusTreeSectionHeader readHeader = BPlusTreeBulkLoader.ReadSectionHeader(binaryReader);

        byte[][] pages = new byte[readHeader.PageCount][];
        for (uint pageIndex = 0; pageIndex < readHeader.PageCount; pageIndex++)
        {
            pages[pageIndex] = binaryReader.ReadBytes(BPlusTreeConstants.PageSize);
        }

        BPlusTreeReader treeReader = new(readHeader, pages);
        Dictionary<string, BPlusTreeColumnIndex> indexes = new(StringComparer.OrdinalIgnoreCase)
        {
            [columnName] = new BPlusTreeColumnIndex(treeReader),
        };

        return new BPlusTreeIndexSet(indexes);
    }

    /// <summary>
    /// Builds a <see cref="SourceIndex"/> with a B+Tree index on
    /// <paramref name="columnName"/>, deriving entries directly from
    /// <paramref name="rows"/>. Single chunk covering all rows.
    /// </summary>
    private static SourceIndex BuildSourceIndexWithBPlusTree(
        string columnName, Row[] rows, DataKind keyKind)
    {
        ValueIndexEntry[] entries = new ValueIndexEntry[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            entries[i] = new ValueIndexEntry(
                rows[i][columnName], ChunkIndex: 0, RowOffsetInChunk: i);
        }

        BPlusTreeIndexSet bTreeSet = BuildBPlusTreeIndexSet(columnName, entries, keyKind);

        return new SourceIndex(
            new SourceFingerprint(100, DummyHash),
            new IndexSchema(SchemaFromRows(rows), rows.Length),
            [CreateChunk(0, rows.Length)],
            bloomFilters: null,
            bPlusTreeIndexes: bTreeSet);
    }

    private static async Task<List<Row>> CollectAsync(IAsyncEnumerable<RowBatch> source)
    {
        return await source.CollectRowsAsync();
    }
}

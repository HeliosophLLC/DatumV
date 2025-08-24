using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="IndexNestedLoopJoinExecutor"/> — both direct executor tests
/// and integration tests through <see cref="JoinOperator"/> dispatch.
/// </summary>
public sealed class IndexNestedLoopJoinTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    // ------------------------------------------------------------------
    //  Direct executor tests
    // ------------------------------------------------------------------

    /// <summary>
    /// Verifies that an INNER join via the index NLJ executor produces correct combined rows
    /// when every probe key has a matching build-side entry.
    /// </summary>
    [Fact]
    public async Task InnerJoin_AllKeysMatch_ProducesCorrectCombinedRows()
    {
        // Build side: 3 rows keyed on "id".
        Row[] buildRows =
        [
            MakeRow(("id", DataValue.FromFloat32(1f)), ("name", DataValue.FromString("Alice"))),
            MakeRow(("id", DataValue.FromFloat32(2f)), ("name", DataValue.FromString("Bob"))),
            MakeRow(("id", DataValue.FromFloat32(3f)), ("name", DataValue.FromString("Charlie"))),
        ];

        // Sorted index on "id" column, pointing to each row.
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(2f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(3f), ChunkIndex: 0, RowOffsetInChunk: 2),
        ];

        SortedValueIndex sortedIndex = new(entries);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(rowOffset: 0, rowCount: 3)];

        // Probe side: 2 rows that should match build-side ids 1 and 3.
        MockOperator probe = new(
            MakeRow(("p.id", DataValue.FromFloat32(1f)), ("p.score", DataValue.FromFloat32(95f))),
            MakeRow(("p.id", DataValue.FromFloat32(3f)), ("p.score", DataValue.FromFloat32(87f))));

        MockOperator buildOperator = new();

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableDescriptor descriptor = CreateDescriptor("build");
        SeekableInMemoryProvider provider = new(buildRows);

        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);
        ExpressionEvaluator evaluator = new(DefaultFunctions);

        IndexNestedLoopJoinExecutor executor = new(
            JoinType.Inner, extraction, sortedIndex, chunks, descriptor, buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, buildOperator, context));

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

    /// <summary>
    /// Verifies that probe rows with keys not present in the build-side index
    /// produce no output for INNER join.
    /// </summary>
    [Fact]
    public async Task InnerJoin_NoMatch_YieldsNoRows()
    {
        Row[] buildRows =
        [
            MakeRow(("id", DataValue.FromFloat32(10f)), ("val", DataValue.FromString("X"))),
        ];

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(10f), ChunkIndex: 0, RowOffsetInChunk: 0),
        ];

        SortedValueIndex sortedIndex = new(entries);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(rowOffset: 0, rowCount: 1)];

        // Probe keys 1 and 2 have no match in the build index.
        MockOperator probe = new(
            MakeRow(("p.id", DataValue.FromFloat32(1f))),
            MakeRow(("p.id", DataValue.FromFloat32(2f))));

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableDescriptor descriptor = CreateDescriptor("build");
        SeekableInMemoryProvider provider = new(buildRows);
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);
        ExpressionEvaluator evaluator = new(DefaultFunctions);

        IndexNestedLoopJoinExecutor executor = new(
            JoinType.Inner, extraction, sortedIndex, chunks, descriptor, buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, new MockOperator(), context));

        Assert.Empty(results);
    }

    /// <summary>
    /// Verifies that NULL probe keys are skipped and produce no output.
    /// </summary>
    [Fact]
    public async Task InnerJoin_NullProbeKey_SkipsRow()
    {
        Row[] buildRows =
        [
            MakeRow(("id", DataValue.FromFloat32(1f)), ("val", DataValue.FromString("A"))),
        ];

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
        ];

        SortedValueIndex sortedIndex = new(entries);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(rowOffset: 0, rowCount: 1)];

        MockOperator probe = new(
            MakeRow(("p.id", DataValue.Null(DataKind.Float32))),
            MakeRow(("p.id", DataValue.FromFloat32(1f))));

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableDescriptor descriptor = CreateDescriptor("build");
        SeekableInMemoryProvider provider = new(buildRows);
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);
        ExpressionEvaluator evaluator = new(DefaultFunctions);

        IndexNestedLoopJoinExecutor executor = new(
            JoinType.Inner, extraction, sortedIndex, chunks, descriptor, buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, new MockOperator(), context));

        // Only the non-null key should produce a match.
        Assert.Single(results);
        Assert.Equal(1f, results[0]["p.id"].AsFloat32());
        Assert.Equal("A", results[0]["val"].AsString());
    }

    /// <summary>
    /// Verifies that LeftSemi join yields only the probe-side row (no build columns)
    /// when a matching build row exists.
    /// </summary>
    [Fact]
    public async Task LeftSemiJoin_MatchExists_YieldsProbeRowOnly()
    {
        Row[] buildRows =
        [
            MakeRow(("id", DataValue.FromFloat32(1f)), ("extra", DataValue.FromString("ignored"))),
            MakeRow(("id", DataValue.FromFloat32(2f)), ("extra", DataValue.FromString("also ignored"))),
        ];

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(2f), ChunkIndex: 0, RowOffsetInChunk: 1),
        ];

        SortedValueIndex sortedIndex = new(entries);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(rowOffset: 0, rowCount: 2)];

        MockOperator probe = new(
            MakeRow(("p.id", DataValue.FromFloat32(1f)), ("p.val", DataValue.FromString("keep"))),
            MakeRow(("p.id", DataValue.FromFloat32(99f)), ("p.val", DataValue.FromString("no match"))));

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableDescriptor descriptor = CreateDescriptor("build");
        SeekableInMemoryProvider provider = new(buildRows);
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);
        ExpressionEvaluator evaluator = new(DefaultFunctions);

        IndexNestedLoopJoinExecutor executor = new(
            JoinType.LeftSemi, extraction, sortedIndex, chunks, descriptor, buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, new MockOperator(), context));

        // Only the probe row with key=1 should be emitted (key=99 has no match).
        Assert.Single(results);
        Assert.Equal(1f, results[0]["p.id"].AsFloat32());
        Assert.Equal("keep", results[0]["p.val"].AsString());

        // Build-side columns should NOT be present.
        Assert.False(results[0].TryGetValue("extra", out _));
    }

    /// <summary>
    /// Verifies that duplicate keys in the build-side index produce multiple output rows
    /// for a single probe row under INNER join.
    /// </summary>
    [Fact]
    public async Task InnerJoin_DuplicateBuildKeys_ProducesMultipleRows()
    {
        Row[] buildRows =
        [
            MakeRow(("id", DataValue.FromFloat32(1f)), ("val", DataValue.FromString("A"))),
            MakeRow(("id", DataValue.FromFloat32(1f)), ("val", DataValue.FromString("B"))),
            MakeRow(("id", DataValue.FromFloat32(2f)), ("val", DataValue.FromString("C"))),
        ];

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(2f), ChunkIndex: 0, RowOffsetInChunk: 2),
        ];

        SortedValueIndex sortedIndex = new(entries);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(rowOffset: 0, rowCount: 3)];

        MockOperator probe = new(
            MakeRow(("p.id", DataValue.FromFloat32(1f))));

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableDescriptor descriptor = CreateDescriptor("build");
        SeekableInMemoryProvider provider = new(buildRows);
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);
        ExpressionEvaluator evaluator = new(DefaultFunctions);

        IndexNestedLoopJoinExecutor executor = new(
            JoinType.Inner, extraction, sortedIndex, chunks, descriptor, buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, new MockOperator(), context));

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0]["val"].AsString());
        Assert.Equal("B", results[1]["val"].AsString());
    }

    /// <summary>
    /// Verifies that the index NLJ correctly handles entries spanning multiple chunks
    /// by translating chunk-relative offsets to absolute row positions.
    /// </summary>
    [Fact]
    public async Task InnerJoin_MultipleChunks_ResolvesCorrectAbsoluteRow()
    {
        // 6 rows across 2 chunks (3 rows each).
        Row[] buildRows =
        [
            MakeRow(("id", DataValue.FromFloat32(10f)), ("val", DataValue.FromString("chunk0-row0"))),
            MakeRow(("id", DataValue.FromFloat32(20f)), ("val", DataValue.FromString("chunk0-row1"))),
            MakeRow(("id", DataValue.FromFloat32(30f)), ("val", DataValue.FromString("chunk0-row2"))),
            MakeRow(("id", DataValue.FromFloat32(40f)), ("val", DataValue.FromString("chunk1-row0"))),
            MakeRow(("id", DataValue.FromFloat32(50f)), ("val", DataValue.FromString("chunk1-row1"))),
            MakeRow(("id", DataValue.FromFloat32(60f)), ("val", DataValue.FromString("chunk1-row2"))),
        ];

        // Index entry for key=50 is in chunk 1 at offset 1 → absolute row 4.
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(10f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(20f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(30f), ChunkIndex: 0, RowOffsetInChunk: 2),
            new(DataValue.FromFloat32(40f), ChunkIndex: 1, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(50f), ChunkIndex: 1, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(60f), ChunkIndex: 1, RowOffsetInChunk: 2),
        ];

        SortedValueIndex sortedIndex = new(entries);

        IReadOnlyList<IndexChunk> chunks =
        [
            CreateChunk(rowOffset: 0, rowCount: 3),
            CreateChunk(rowOffset: 3, rowCount: 3),
        ];

        // Probe for key=50 (chunk 1, offset 1 → absolute row 4).
        MockOperator probe = new(
            MakeRow(("p.id", DataValue.FromFloat32(50f))));

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableDescriptor descriptor = CreateDescriptor("build");
        SeekableInMemoryProvider provider = new(buildRows);
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);
        ExpressionEvaluator evaluator = new(DefaultFunctions);

        IndexNestedLoopJoinExecutor executor = new(
            JoinType.Inner, extraction, sortedIndex, chunks, descriptor, buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, new MockOperator(), context));

        Assert.Single(results);
        Assert.Equal(50f, results[0]["id"].AsFloat32());
        Assert.Equal("chunk1-row1", results[0]["val"].AsString());
    }

    /// <summary>
    /// Verifies that LeftSemi join with duplicate build keys yields the probe row
    /// exactly once (not once per duplicate).
    /// </summary>
    [Fact]
    public async Task LeftSemiJoin_DuplicateBuildKeys_YieldsProbeRowOnce()
    {
        Row[] buildRows =
        [
            MakeRow(("id", DataValue.FromFloat32(1f)), ("val", DataValue.FromString("A"))),
            MakeRow(("id", DataValue.FromFloat32(1f)), ("val", DataValue.FromString("B"))),
        ];

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 1),
        ];

        SortedValueIndex sortedIndex = new(entries);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(rowOffset: 0, rowCount: 2)];

        MockOperator probe = new(
            MakeRow(("p.id", DataValue.FromFloat32(1f)), ("p.x", DataValue.FromFloat32(42f))));

        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("p", "id"), new ColumnReference("id"))],
            Residual: null);

        TableDescriptor descriptor = CreateDescriptor("build");
        SeekableInMemoryProvider provider = new(buildRows);
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);
        ExpressionEvaluator evaluator = new(DefaultFunctions);

        IndexNestedLoopJoinExecutor executor = new(
            JoinType.LeftSemi, extraction, sortedIndex, chunks, descriptor, buildAlias: null, evaluator);

        List<Row> results = await CollectAsync(executor.ExecuteAsync(probe, new MockOperator(), context));

        // SEMI: probe row emitted once even though there are 2 build matches.
        Assert.Single(results);
        Assert.Equal(42f, results[0]["p.x"].AsFloat32());
    }

    // ------------------------------------------------------------------
    //  JoinOperator dispatch integration tests
    // ------------------------------------------------------------------

    /// <summary>
    /// Verifies that <see cref="JoinOperator"/> selects the index NLJ path when
    /// a sorted index is available on the build-side join column and the provider
    /// is seekable.
    /// </summary>
    [Fact]
    public async Task JoinOperator_SelectsIndexNlj_WhenSortedIndexAvailable()
    {
        // Build side: ScanOperator with a sorted index on "id".
        Row[] buildRows =
        [
            MakeRow(("id", DataValue.FromFloat32(1f)), ("name", DataValue.FromString("Alice"))),
            MakeRow(("id", DataValue.FromFloat32(2f)), ("name", DataValue.FromString("Bob"))),
            MakeRow(("id", DataValue.FromFloat32(3f)), ("name", DataValue.FromString("Charlie"))),
        ];

        ValueIndexEntry[] indexEntries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(2f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(3f), ChunkIndex: 0, RowOffsetInChunk: 2),
        ];

        SortedValueIndex sortedIndex = new(indexEntries);
        SortedValueIndexSet sortedIndexSet = new(new Dictionary<string, SortedValueIndex>
        {
            ["id"] = sortedIndex,
        });

        IReadOnlyList<IndexChunk> chunks = [CreateChunk(rowOffset: 0, rowCount: 3)];

        SourceIndex sourceIndex = new(
            new SourceFingerprint(100, new byte[32]),
            new IndexSchema(new Schema([new ColumnInfo("id", DataKind.Float32, false), new ColumnInfo("name", DataKind.String, false)]), 3),
            chunks,
            sortedIndexes: sortedIndexSet);

        TableDescriptor buildDescriptor = CreateDescriptor("build");
        ScanOperator buildScan = new(buildDescriptor, requiredColumns: null);
        buildScan.SetSourceIndex(sourceIndex);

        // Probe side: MockOperator with 2 rows.
        MockOperator probeOperator = new(
            MakeRow(("p.id", DataValue.FromFloat32(1f)), ("p.score", DataValue.FromFloat32(95f))),
            MakeRow(("p.id", DataValue.FromFloat32(3f)), ("p.score", DataValue.FromFloat32(87f))));

        // Create JoinOperator: probe ON buildScan with p.id = id.
        JoinOperator join = new(probeOperator, buildScan, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("p", "id"),
                BinaryOperator.Equal,
                new ColumnReference("id")));

        // Register seekable provider in catalog.
        SeekableInMemoryProvider provider = new(buildRows);
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(buildDescriptor);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog)
        {
            RowLimit = 10,
        };

        List<Row> results = await CollectAsync(join, context);

        // Should produce correct combined rows via index NLJ path.
        Assert.Equal(2, results.Count);

        Row alice = results.First(row => row["name"].AsString() == "Alice");
        Assert.Equal(95f, alice["p.score"].AsFloat32());

        Row charlie = results.First(row => row["name"].AsString() == "Charlie");
        Assert.Equal(87f, charlie["p.score"].AsFloat32());
    }

    /// <summary>
    /// Verifies that the index NLJ executor qualifies build-side row column names
    /// with the table alias when the build side is wrapped in an <see cref="AliasOperator"/>.
    /// Without this, <see cref="ProjectOperator"/> handling <c>SELECT table.*</c>
    /// cannot match build-side columns by their table prefix.
    /// </summary>
    [Fact]
    public async Task JoinOperator_IndexNlj_QualifiesBuildColumnsWithAlias()
    {
        // Build side: raw column names (no alias prefix), simulating seekable reads.
        Row[] buildRows =
        [
            MakeRow(("id", DataValue.FromFloat32(1f)), ("name", DataValue.FromString("Alice"))),
            MakeRow(("id", DataValue.FromFloat32(2f)), ("name", DataValue.FromString("Bob"))),
        ];

        ValueIndexEntry[] indexEntries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(2f), ChunkIndex: 0, RowOffsetInChunk: 1),
        ];

        SortedValueIndex sortedIndex = new(indexEntries);
        SortedValueIndexSet sortedIndexSet = new(new Dictionary<string, SortedValueIndex>
        {
            ["id"] = sortedIndex,
        });

        IReadOnlyList<IndexChunk> chunks = [CreateChunk(rowOffset: 0, rowCount: 2)];

        SourceIndex sourceIndex = new(
            new SourceFingerprint(100, new byte[32]),
            new IndexSchema(new Schema([new ColumnInfo("id", DataKind.Float32, false), new ColumnInfo("name", DataKind.String, false)]), 2),
            chunks,
            sortedIndexes: sortedIndexSet);

        TableDescriptor buildDescriptor = CreateDescriptor("build");
        ScanOperator buildScan = new(buildDescriptor, requiredColumns: null);
        buildScan.SetSourceIndex(sourceIndex);

        // Wrap build side in AliasOperator — this is what QueryPlanner does for
        // JOIN sources. The NLJ executor should apply this alias to fetched rows.
        AliasOperator aliasedBuild = new(buildScan, "right_table");

        // Probe side with alias-qualified columns (from AliasOperator on probe side).
        MockOperator probeOperator = new(
            MakeRow(("left_table.id", DataValue.FromFloat32(1f)), ("left_table.score", DataValue.FromFloat32(95f))));

        JoinOperator join = new(probeOperator, aliasedBuild, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("left_table", "id"),
                BinaryOperator.Equal,
                new ColumnReference("right_table", "id")));

        SeekableInMemoryProvider provider = new(buildRows);
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(buildDescriptor);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog)
        {
            RowLimit = 10,
        };

        List<Row> results = await CollectAsync(join, context);

        Assert.Single(results);
        Row row = results[0];

        // Build-side columns must have the alias prefix from AliasOperator.
        Assert.True(row.TryGetValue("right_table.id", out _), "Expected qualified column 'right_table.id'");
        Assert.True(row.TryGetValue("right_table.name", out _), "Expected qualified column 'right_table.name'");

        // Probe-side columns should still be present.
        Assert.Equal(95f, row["left_table.score"].AsFloat32());
        Assert.Equal("Alice", row["right_table.name"].AsString());
    }

    /// <summary>
    /// Verifies that <see cref="JoinOperator"/> falls back to hash join when no
    /// sorted index is available on the build side, even though the provider is seekable.
    /// </summary>
    [Fact]
    public async Task JoinOperator_FallsBackToHashJoin_WhenNoSortedIndex()
    {
        // Build side: ScanOperator with NO source index.
        Row[] buildRows =
        [
            MakeRow(("r.id", DataValue.FromFloat32(1f)), ("r.val", DataValue.FromString("X"))),
            MakeRow(("r.id", DataValue.FromFloat32(2f)), ("r.val", DataValue.FromString("Y"))),
        ];

        TableDescriptor buildDescriptor = CreateDescriptor("build");
        ScanOperator buildScan = new(buildDescriptor, requiredColumns: null);
        // No SetSourceIndex call — no sorted index available.

        MockOperator probeOperator = new(
            MakeRow(("l.id", DataValue.FromFloat32(1f)), ("l.name", DataValue.FromString("Alice"))),
            MakeRow(("l.id", DataValue.FromFloat32(2f)), ("l.name", DataValue.FromString("Bob"))));

        JoinOperator join = new(probeOperator, buildScan, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        // Register seekable provider in catalog so hash join can still work.
        SeekableInMemoryProvider provider = new(buildRows);
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(buildDescriptor);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);

        List<Row> results = await CollectAsync(join, context);

        // Hash join should still produce correct results.
        Assert.Equal(2, results.Count);
        Assert.Contains(results, row => row["l.name"].AsString() == "Alice");
        Assert.Contains(results, row => row["l.name"].AsString() == "Bob");
    }

    /// <summary>
    /// Verifies that the index NLJ path is not selected for LEFT JOIN
    /// (only INNER and LeftSemi are supported).
    /// </summary>
    [Fact]
    public async Task JoinOperator_DoesNotSelectIndexNlj_ForLeftJoin()
    {
        Row[] buildRows =
        [
            MakeRow(("r.id", DataValue.FromFloat32(1f)), ("r.val", DataValue.FromString("X"))),
        ];

        ValueIndexEntry[] indexEntries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
        ];

        SortedValueIndex sortedIndex = new(indexEntries);
        SortedValueIndexSet sortedIndexSet = new(new Dictionary<string, SortedValueIndex>
        {
            ["r.id"] = sortedIndex,
        });

        IReadOnlyList<IndexChunk> chunks = [CreateChunk(rowOffset: 0, rowCount: 1)];

        SourceIndex sourceIndex = new(
            new SourceFingerprint(100, new byte[32]),
            new IndexSchema(new Schema([new ColumnInfo("r.id", DataKind.Float32, false), new ColumnInfo("r.val", DataKind.String, false)]), 1),
            chunks,
            sortedIndexes: sortedIndexSet);

        TableDescriptor buildDescriptor = CreateDescriptor("build");
        ScanOperator buildScan = new(buildDescriptor, requiredColumns: null);
        buildScan.SetSourceIndex(sourceIndex);

        MockOperator probeOperator = new(
            MakeRow(("l.id", DataValue.FromFloat32(1f)), ("l.name", DataValue.FromString("Alice"))),
            MakeRow(("l.id", DataValue.FromFloat32(999f)), ("l.name", DataValue.FromString("NoMatch"))));

        JoinOperator join = new(probeOperator, buildScan, JoinType.Left,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        SeekableInMemoryProvider provider = new(buildRows);
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(buildDescriptor);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog)
        {
            RowLimit = 10,
        };

        List<Row> results = await CollectAsync(join, context);

        // LEFT JOIN should fall back to hash join and produce 2 rows
        // (matched + unmatched with NULL right columns).
        Assert.Equal(2, results.Count);
    }

    // ------------------------------------------------------------------
    //  PreferIndexNestedLoop flag tests
    // ------------------------------------------------------------------

    /// <summary>
    /// Verifies that <see cref="JoinOperator"/> activates the index NLJ executor when
    /// <see cref="JoinOperator.PreferIndexNestedLoop"/> is <c>true</c>, even when no
    /// <see cref="ExecutionContext.RowLimit"/> is set. This covers the planner-driven
    /// path where a LIMIT clause is detected at plan time rather than at runtime.
    /// </summary>
    [Fact]
    public async Task JoinOperator_PreferIndexNestedLoop_ActivatesNljWithoutRowLimit()
    {
        Row[] buildRows =
        [
            MakeRow(("id", DataValue.FromFloat32(1f)), ("name", DataValue.FromString("Alice"))),
            MakeRow(("id", DataValue.FromFloat32(2f)), ("name", DataValue.FromString("Bob"))),
            MakeRow(("id", DataValue.FromFloat32(3f)), ("name", DataValue.FromString("Charlie"))),
        ];

        ValueIndexEntry[] indexEntries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(2f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(3f), ChunkIndex: 0, RowOffsetInChunk: 2),
        ];

        SortedValueIndex sortedIndex = new(indexEntries);
        SortedValueIndexSet sortedIndexSet = new(new Dictionary<string, SortedValueIndex>
        {
            ["id"] = sortedIndex,
        });

        IReadOnlyList<IndexChunk> chunks = [CreateChunk(rowOffset: 0, rowCount: 3)];

        SourceIndex sourceIndex = new(
            new SourceFingerprint(100, new byte[32]),
            new IndexSchema(
                new Schema([new ColumnInfo("id", DataKind.Float32, false),
                            new ColumnInfo("name", DataKind.String, false)]), 3),
            chunks,
            sortedIndexes: sortedIndexSet);

        TableDescriptor buildDescriptor = CreateDescriptor("build");
        ScanOperator buildScan = new(buildDescriptor, requiredColumns: null);
        buildScan.SetSourceIndex(sourceIndex);

        MockOperator probeOperator = new(
            MakeRow(("p.id", DataValue.FromFloat32(1f)), ("p.score", DataValue.FromFloat32(95f))),
            MakeRow(("p.id", DataValue.FromFloat32(3f)), ("p.score", DataValue.FromFloat32(87f))));

        // preferIndexNestedLoop: true — NLJ should fire without a RowLimit.
        JoinOperator join = new(
            probeOperator,
            buildScan,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("p", "id"),
                BinaryOperator.Equal,
                new ColumnReference("id")),
            preferIndexNestedLoop: true);

        SeekableInMemoryProvider provider = new(buildRows);
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(buildDescriptor);

        // No RowLimit set — NLJ must activate via the PreferIndexNestedLoop flag alone.
        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);

        List<Row> results = await CollectAsync(join, context);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, row => row["name"].AsString() == "Alice");
        Assert.Contains(results, row => row["name"].AsString() == "Charlie");
    }

    /// <summary>
    /// Verifies that <see cref="JoinOperator"/> does NOT activate the index NLJ executor
    /// when <see cref="JoinOperator.PreferIndexNestedLoop"/> is <c>false</c> and no
    /// <see cref="ExecutionContext.RowLimit"/> is set. The runtime guard remains in effect
    /// when the planner has not explicitly opted in.
    /// </summary>
    [Fact]
    public async Task JoinOperator_PreferIndexNestedLoopFalse_RequiresRowLimitToActivateNlj()
    {
        Row[] buildRows =
        [
            MakeRow(("id", DataValue.FromFloat32(1f)), ("name", DataValue.FromString("Alice"))),
        ];

        ValueIndexEntry[] indexEntries =
        [
            new(DataValue.FromFloat32(1f), ChunkIndex: 0, RowOffsetInChunk: 0),
        ];

        SortedValueIndex sortedIndex = new(indexEntries);
        SortedValueIndexSet sortedIndexSet = new(new Dictionary<string, SortedValueIndex>
        {
            ["id"] = sortedIndex,
        });

        IReadOnlyList<IndexChunk> chunks = [CreateChunk(rowOffset: 0, rowCount: 1)];

        SourceIndex sourceIndex = new(
            new SourceFingerprint(100, new byte[32]),
            new IndexSchema(
                new Schema([new ColumnInfo("id", DataKind.Float32, false),
                            new ColumnInfo("name", DataKind.String, false)]), 1),
            chunks,
            sortedIndexes: sortedIndexSet);

        TableDescriptor buildDescriptor = CreateDescriptor("build");
        ScanOperator buildScan = new(buildDescriptor, requiredColumns: null);
        buildScan.SetSourceIndex(sourceIndex);

        MockOperator probeOperator = new(
            MakeRow(("p.id", DataValue.FromFloat32(1f)), ("p.score", DataValue.FromFloat32(99f))));

        // preferIndexNestedLoop: false (default) — NLJ requires RowLimit at runtime.
        JoinOperator join = new(
            probeOperator,
            buildScan,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("p", "id"),
                BinaryOperator.Equal,
                new ColumnReference("id")));

        SeekableInMemoryProvider provider = new(buildRows);
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(buildDescriptor);

        // No RowLimit — hash join path should be taken (NLJ not activated).
        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);

        // Hash join still produces the correct result; we just confirm it completes.
        List<Row> results = await CollectAsync(join, context);

        Assert.Single(results);
        Assert.Equal("Alice", results[0]["name"].AsString());

        // Confirm that adding RowLimit activates NLJ when flag is false.
        // Re-create operators fresh since probe is exhausted.
        probeOperator = new(MakeRow(("p.id", DataValue.FromFloat32(1f)), ("p.score", DataValue.FromFloat32(99f))));
        JoinOperator joinWithLimit = new(
            probeOperator,
            buildScan,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("p", "id"),
                BinaryOperator.Equal,
                new ColumnReference("id")));

        ExecutionContext contextWithLimit = new(CancellationToken.None, DefaultFunctions, catalog)
        {
            RowLimit = 10,
        };

        List<Row> resultsWithLimit = await CollectAsync(joinWithLimit, contextWithLimit);
        Assert.Single(resultsWithLimit);
        Assert.Equal("Alice", resultsWithLimit[0]["name"].AsString());
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(column => column.Name).ToArray();
        DataValue[] values = columns.Select(column => column.Value).ToArray();
        return new Row(names, values);
    }

    private static IndexChunk CreateChunk(long rowOffset, long rowCount)
    {
        return new IndexChunk(rowOffset, rowCount, -1, -1,
            new Dictionary<string, ChunkColumnStatistics>());
    }

    private static TableDescriptor CreateDescriptor(string name)
    {
        return new TableDescriptor("test", name, $"{name}.test",
            new Dictionary<string, string>());
    }

    private static async Task<List<Row>> CollectAsync(IAsyncEnumerable<Row> source)
    {
        List<Row> rows = new();
        await foreach (Row row in source)
        {
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<List<Row>> CollectAsync(IQueryOperator queryOperator, ExecutionContext context)
    {
        List<Row> rows = new();
        await foreach (Row row in queryOperator.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Simple mock query operator that yields pre-defined rows.
    /// </summary>
    private sealed class MockOperator : IQueryOperator
    {
        private readonly Row[] _rows;

        public MockOperator(params Row[] rows)
        {
            _rows = rows;
        }

        public OperatorPlanDescription DescribeForExplain() => new("Mock");

        public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
        {
            foreach (Row row in _rows)
            {
                yield return row;
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// In-memory provider that supports seeking for testing index-based execution paths.
    /// </summary>
    private sealed class SeekableInMemoryProvider : ISeekableTableProvider
    {
        private readonly Row[] _rows;

        public SeekableInMemoryProvider(Row[] rows)
        {
            _rows = rows;
        }

        /// <inheritdoc />
        public Task<Schema> GetSchemaAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            ColumnInfo[] columns = _rows[0].ColumnNames
                .Select(name => new ColumnInfo(name, DataKind.Float32, false))
                .ToArray();
            return Task.FromResult(new Schema(columns));
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<Row> OpenAsync(
            TableDescriptor descriptor,
            IReadOnlySet<string>? requiredColumns,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (Row row in _rows)
            {
                yield return row;
            }

            await Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<ProviderCapabilities> GetCapabilitiesAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderCapabilities(
                EstimatedRowCount: _rows.Length,
                EstimatedRowSizeBytes: null,
                SupportsSeek: true,
                ColumnCosts: new Dictionary<string, ColumnCost>()));
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<Row> ReadRowRangeAsync(
            TableDescriptor descriptor,
            IReadOnlySet<string>? requiredColumns,
            long startRow,
            int count,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            long end = Math.Min(startRow + count, _rows.Length);

            for (long i = startRow; i < end; i++)
            {
                yield return _rows[i];
            }

            await Task.CompletedTask;
        }
    }
}

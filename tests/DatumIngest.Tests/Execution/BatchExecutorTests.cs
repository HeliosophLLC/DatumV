using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Slice 4 end-to-end tests for the procedural batch executor: DECLARE,
/// SET, BEGIN/END, IF/ELSE, WHILE, plus query / EXEC statements that
/// reference declared variables. The substrate (<see cref="VariableScope"/>
/// / <see cref="BatchContext"/>) is verified separately; these tests
/// pin the integrated semantics.
/// </summary>
public sealed class BatchExecutorTests : ServiceTestBase
{
    private async Task<BatchResult> RunAsync(string sql, TableCatalog? catalog = null)
    {
        catalog ??= CreateCatalog();
        IReadOnlyList<Statement> stmts = SqlParser.ParseBatch(sql);
        BatchExecutor executor = new(catalog);
        return await executor.ExecuteAsync(stmts, CancellationToken.None);
    }

    // ───────────────────── DECLARE ─────────────────────

    [Fact]
    public async Task Declare_LiteralInitializer_BindsValue()
    {
        BatchResult result = await RunAsync("DECLARE @x INT32 = 5");
        Assert.Equal(5, Convert.ToInt32(result.FinalBindings["x"]));
    }

    [Fact]
    public async Task Declare_StringInitializer_BindsValue()
    {
        BatchResult result = await RunAsync("DECLARE @greeting STRING = 'hello world from a long-enough literal that lives in an arena'");
        Assert.Equal(
            "hello world from a long-enough literal that lives in an arena",
            result.FinalBindings["greeting"]);
    }

    [Fact]
    public async Task Declare_BooleanInitializer_BindsValue()
    {
        BatchResult result = await RunAsync("DECLARE @flag BOOLEAN = TRUE");
        Assert.Equal(true, result.FinalBindings["flag"]);
    }

    [Fact]
    public async Task Declare_ExpressionInitializer_EvaluatesAndBinds()
    {
        BatchResult result = await RunAsync("DECLARE @sum INT32 = 2 + 3 * 4");
        Assert.Equal(14, Convert.ToInt32(result.FinalBindings["sum"]));
    }

    [Fact]
    public async Task Declare_NoInitializer_BindsNull()
    {
        BatchResult result = await RunAsync("DECLARE @x INT32");
        Assert.True(result.FinalBindings.ContainsKey("x"));
        Assert.Null(result.FinalBindings["x"]);
    }

    [Fact]
    public async Task Declare_AngleBracketArrayType_NoInitializer_BindsTypedNullArray()
    {
        // No initializer + array annotation → null carrier with Kind=String,
        // IsArray=true. Materialize() returns null for any IsNull value, so
        // surface verification is "did not throw"; the parser + resolver +
        // DataValue.NullArrayOf wiring is what we're pinning.
        BatchResult result = await RunAsync("DECLARE @players Array<STRING>");
        Assert.True(result.FinalBindings.ContainsKey("players"));
        Assert.Null(result.FinalBindings["players"]);
    }

    [Fact]
    public async Task Declare_PostfixBracketSugar_NoInitializer_BindsTypedNullArray()
    {
        BatchResult result = await RunAsync("DECLARE @scores FLOAT32[]");
        Assert.True(result.FinalBindings.ContainsKey("scores"));
        Assert.Null(result.FinalBindings["scores"]);
    }

    [Fact]
    public async Task Declare_NestedArrayAnnotation_Throws()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync("DECLARE @bad Array<Array<INT32>>"));
        Assert.Contains("cannot resolve type name", ex.Message);
    }

    [Fact]
    public async Task Declare_ArrayLiteralInitializer_BindsArrayUsableByArrayLength()
    {
        // Reported bug: storing an array literal under a variable and then
        // calling array_length() on the variable threw "argument must be an
        // array" because the IsArray flag wasn't preserved through the path
        // (DECLARE → variable scope → variable read → function arg).
        BatchResult result = await RunAsync(
            "DECLARE @players Array<String> = ['Fighter', 'Wizard', 'Healer']; " +
            "DECLARE @player_count INT32 = ARRAY_LENGTH(@players)");

        Assert.Equal(3, Convert.ToInt32(result.FinalBindings["player_count"]));
    }

    // ───────────────────── LIMIT / OFFSET with expressions ─────────────────────

    [Fact]
    public async Task Offset_WithProceduralVariable_SkipsCorrectRows()
    {
        // Regression for the user-reported issue: OFFSET (and LIMIT) used to
        // accept only NumberLiteral. With expression support, `OFFSET @var`
        // now resolves the variable at execute time against the procedural
        // scope and skips the right rows.
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id"],
            [1], [2], [3], [4], [5]);

        BatchResult result = await RunAsync(
            "DECLARE @offset INT32 = 2; " +
            "DECLARE @sum INT64 = (SELECT sum(id) FROM (SELECT id FROM orders ORDER BY id LIMIT 2 OFFSET @offset) s)",
            catalog);

        // ORDER BY id, OFFSET 2, LIMIT 2 → ids 3, 4 → sum = 7.
        Assert.Equal(7L, Convert.ToInt64(result.FinalBindings["sum"]));
    }

    [Fact]
    public async Task Limit_WithProceduralVariable_BoundsRows()
    {
        // Symmetric to the OFFSET case: LIMIT also accepts a variable.
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id"],
            [1], [2], [3], [4], [5]);

        BatchResult result = await RunAsync(
            "DECLARE @top INT32 = 3; " +
            "DECLARE @sum INT64 = (SELECT sum(id) FROM (SELECT id FROM orders ORDER BY id LIMIT @top) s)",
            catalog);

        // ORDER BY id, LIMIT 3 → ids 1, 2, 3 → sum = 6.
        Assert.Equal(6L, Convert.ToInt64(result.FinalBindings["sum"]));
    }

    [Fact]
    public async Task Limit_WithArithmeticExpression_EvaluatesAtExecuteTime()
    {
        // Arbitrary expression as LIMIT — proves the parser accepts more
        // than just a single VariableExpression and the runtime evaluator
        // handles arithmetic the same way as everywhere else.
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id"],
            [1], [2], [3], [4], [5]);

        BatchResult result = await RunAsync(
            "DECLARE @half INT32 = 1; " +
            "DECLARE @sum INT64 = (SELECT sum(id) FROM (SELECT id FROM orders ORDER BY id LIMIT @half + 2) s)",
            catalog);

        // ORDER BY id, LIMIT 3 → ids 1, 2, 3 → sum = 6.
        Assert.Equal(6L, Convert.ToInt64(result.FinalBindings["sum"]));
    }

    // ───────────────────── DECLARE-with-subquery ─────────────────────

    [Fact]
    public async Task Declare_SubqueryInitializer_AggregateCount()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id"],
            [1], [2], [3], [4], [5]);

        BatchResult result = await RunAsync(
            "DECLARE @count INT64 = (SELECT count(*) FROM orders)",
            catalog);

        Assert.Equal(5L, Convert.ToInt64(result.FinalBindings["count"]));
    }

    [Fact]
    public async Task Declare_SubqueryInitializer_FilteredAggregate()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["amount"],
            [10], [20], [30], [40], [50]);

        BatchResult result = await RunAsync(
            "DECLARE @total INT64 = (SELECT sum(amount) FROM orders WHERE amount > 20)",
            catalog);

        // 30 + 40 + 50 = 120
        Assert.Equal(120L, Convert.ToInt64(result.FinalBindings["total"]));
    }

    [Fact]
    public async Task Declare_SubqueryInitializer_SingleRowSingleColumn()
    {
        // Bare value-pulling subquery — pull max(id) for use as a downstream
        // variable. Common pattern for procedure preambles.
        TableCatalog catalog = CreateCatalog("events",
            columns: ["seq"],
            [10], [20], [30]);

        BatchResult result = await RunAsync(
            "DECLARE @max_seq INT64 = (SELECT max(seq) FROM events)",
            catalog);

        Assert.Equal(30L, Convert.ToInt64(result.FinalBindings["max_seq"]));
    }

    [Fact]
    public async Task Declare_SubqueryInitializer_ReferencesOuterVariable()
    {
        // The subquery's WHERE references an earlier-declared @cap; resolves
        // through the same variable scope.
        TableCatalog catalog = CreateCatalog("amounts",
            columns: ["v"],
            [1], [2], [3], [4], [5]);

        BatchResult result = await RunAsync(
            "DECLARE @cap INT64 = 3; " +
            "DECLARE @kept INT64 = (SELECT count(*) FROM amounts WHERE v <= @cap)",
            catalog);

        Assert.Equal(3L, Convert.ToInt64(result.FinalBindings["kept"]));
    }

    [Fact]
    public async Task Declare_SubqueryInitializer_EmptyAggregateReturnsTypedNull()
    {
        // Aggregates over an empty result return NULL; declaring without a
        // NOT-NULL constraint binds the typed null cleanly.
        TableCatalog catalog = CreateCatalog("amounts",
            columns: ["v"],
            [1], [2], [3]);

        BatchResult result = await RunAsync(
            "DECLARE @max_big INT64 = (SELECT max(v) FROM amounts WHERE v > 100)",
            catalog);

        Assert.Null(result.FinalBindings["max_big"]);
    }

    [Fact]
    public async Task Declare_SubqueryInitializer_InsideArithmetic()
    {
        // Subquery as one operand of a binary expression — the prefolder
        // recurses through BinaryExpression so the result is stitched back
        // in as a literal before the synthesised SELECT runs.
        TableCatalog catalog = CreateCatalog("amounts",
            columns: ["v"],
            [10], [20], [30]);

        BatchResult result = await RunAsync(
            "DECLARE @total INT64 = (SELECT sum(v) FROM amounts) + 100",
            catalog);

        // 60 + 100
        Assert.Equal(160L, Convert.ToInt64(result.FinalBindings["total"]));
    }

    [Fact]
    public async Task Set_SubqueryInitializer_UpdatesVariable()
    {
        // SET goes through the same EvaluateScalarAsync path, so a subquery
        // initializer there should also work without extra wiring.
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id"],
            [1], [2], [3]);

        BatchResult result = await RunAsync(
            "DECLARE @count INT64 = 0; " +
            "SET @count = (SELECT count(*) FROM orders)",
            catalog);

        Assert.Equal(3L, Convert.ToInt64(result.FinalBindings["count"]));
    }

    // ───────────────────── @var resolution ─────────────────────

    [Fact]
    public async Task DeclareThenSelect_QueryReadsVariable()
    {
        // The SELECT references @x; resolution walks the variable scope
        // chain. End state: @x stays at 7. (We can't observe the SELECT's
        // rows in slice 4, so the assertion is on the binding.)
        BatchResult result = await RunAsync(
            "DECLARE @x INT32 = 7; " +
            "SELECT @x + 1");
        Assert.Equal(7, Convert.ToInt32(result.FinalBindings["x"]));
    }

    [Fact]
    public async Task DeclareTwoVariables_LaterReferencesFormer()
    {
        // @b's initialiser references @a; substrate plumbing must allow a
        // child query (the synthetic SELECT inside DECLARE) to resolve
        // @a from the variable scope.
        BatchResult result = await RunAsync(
            "DECLARE @a INT32 = 10; " +
            "DECLARE @b INT32 = @a * 2");
        Assert.Equal(10, Convert.ToInt32(result.FinalBindings["a"]));
        Assert.Equal(20, Convert.ToInt32(result.FinalBindings["b"]));
    }

    // ───────────────────── SET ─────────────────────

    [Fact]
    public async Task Set_OverwritesPriorValue()
    {
        BatchResult result = await RunAsync(
            "DECLARE @x INT32 = 1; " +
            "SET @x = 99");
        Assert.Equal(99, Convert.ToInt32(result.FinalBindings["x"]));
    }

    [Fact]
    public async Task Set_ExpressionReferencesVariableItself()
    {
        BatchResult result = await RunAsync(
            "DECLARE @x INT32 = 5; " +
            "SET @x = @x + 100");
        Assert.Equal(105, Convert.ToInt32(result.FinalBindings["x"]));
    }

    [Fact]
    public async Task Set_UndeclaredVariable_Throws()
    {
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => RunAsync("SET @missing = 1"));
    }

    // ───────────────────── BEGIN/END block scoping ─────────────────────

    [Fact]
    public async Task Block_InnerDeclaration_DoesNotLeakOutside()
    {
        // @inner is declared in the block; after END it's gone. @outer
        // remains accessible. This is the block-scope guarantee.
        BatchResult result = await RunAsync(
            "DECLARE @outer INT32 = 1; " +
            "BEGIN " +
            "  DECLARE @inner INT32 = 2; " +
            "  SET @outer = @inner + 10; " +
            "END");

        Assert.Equal(12, Convert.ToInt32(result.FinalBindings["outer"]));
        Assert.False(result.FinalBindings.ContainsKey("inner"),
            "@inner should not be visible after the block ends");
    }

    [Fact]
    public async Task Block_Nested_BothFramesPushAndPop()
    {
        // Inner declares @z=3; SET @x mutates the outer-most binding via
        // scope-walk. After both blocks pop, only @x survives, value 3.
        BatchResult result = await RunAsync(
            "DECLARE @x INT32 = 0; " +
            "BEGIN " +
            "  BEGIN " +
            "    DECLARE @z INT32 = 3; " +
            "    SET @x = @z; " +
            "  END " +
            "END");

        Assert.Equal(3, Convert.ToInt32(result.FinalBindings["x"]));
        Assert.False(result.FinalBindings.ContainsKey("z"));
    }

    // ───────────────────── IF / ELSE ─────────────────────

    [Fact]
    public async Task If_TrueBranch_RunsThen()
    {
        BatchResult result = await RunAsync(
            "DECLARE @taken INT32 = 0; " +
            "IF TRUE SET @taken = 1");

        Assert.Equal(1, Convert.ToInt32(result.FinalBindings["taken"]));
    }

    [Fact]
    public async Task If_FalseBranchWithElse_RunsElse()
    {
        BatchResult result = await RunAsync(
            "DECLARE @path INT32 = 0; " +
            "IF FALSE SET @path = 1 ELSE SET @path = 2");

        Assert.Equal(2, Convert.ToInt32(result.FinalBindings["path"]));
    }

    [Fact]
    public async Task If_PredicateUsesVariable_BranchesOnRuntimeValue()
    {
        BatchResult result = await RunAsync(
            "DECLARE @x INT32 = 5; " +
            "DECLARE @result INT32 = 0; " +
            "IF @x > 0 SET @result = 1 ELSE SET @result = -1");

        Assert.Equal(1, Convert.ToInt32(result.FinalBindings["result"]));
    }

    [Fact]
    public async Task If_ElseIfChain_TakesMatchingBranch()
    {
        // ELSE IF parses as ELSE { IF ... }. The chain finds the first
        // matching branch and runs only that one's body.
        BatchResult result = await RunAsync(
            "DECLARE @x INT32 = 0; " +
            "DECLARE @label INT32 = 0; " +
            "IF @x > 0 SET @label = 1 " +
            "ELSE IF @x < 0 SET @label = -1 " +
            "ELSE SET @label = 999");

        Assert.Equal(999, Convert.ToInt32(result.FinalBindings["label"]));
    }

    [Fact]
    public async Task If_BlockBody_RunsAllChildren()
    {
        BatchResult result = await RunAsync(
            "DECLARE @a INT32 = 0; " +
            "DECLARE @b INT32 = 0; " +
            "IF TRUE BEGIN " +
            "  SET @a = 10; " +
            "  SET @b = 20; " +
            "END");

        Assert.Equal(10, Convert.ToInt32(result.FinalBindings["a"]));
        Assert.Equal(20, Convert.ToInt32(result.FinalBindings["b"]));
    }

    // ───────────────────── WHILE ─────────────────────

    [Fact]
    public async Task While_LoopsUntilPredicateFalse()
    {
        BatchResult result = await RunAsync(
            "DECLARE @i INT32 = 0; " +
            "DECLARE @sum INT32 = 0; " +
            "WHILE @i < 5 BEGIN " +
            "  SET @sum = @sum + @i; " +
            "  SET @i = @i + 1; " +
            "END");

        // 0 + 1 + 2 + 3 + 4 = 10
        Assert.Equal(10, Convert.ToInt32(result.FinalBindings["sum"]));
        Assert.Equal(5, Convert.ToInt32(result.FinalBindings["i"]));
    }

    [Fact]
    public async Task While_PredicateFalseAtStart_BodyNeverRuns()
    {
        BatchResult result = await RunAsync(
            "DECLARE @ran INT32 = 0; " +
            "WHILE FALSE SET @ran = 1");

        Assert.Equal(0, Convert.ToInt32(result.FinalBindings["ran"]));
    }

    // ───────────────────── EXEC inside batch ─────────────────────

    [Fact]
    public async Task Exec_InBatch_ResolvesVariableFromScope()
    {
        // EXEC of a built-in scalar function with a variable arg — exercises
        // the same wire as a regular EXEC, just with variable resolution.
        // The batch only verifies the execution didn't throw; result rows
        // aren't surfaced in slice 4.
        BatchResult result = await RunAsync(
            "DECLARE @msg STRING = 'hello world from a long-enough literal'; " +
            "EXEC upper(@msg)");

        // The variable's still bound at end-of-batch.
        Assert.Equal(
            "hello world from a long-enough literal",
            result.FinalBindings["msg"]);
    }

    // ───────────────────── Combined integration ─────────────────────

    [Fact]
    public async Task FullProcedure_DeclareIfWhileSet_AccumulatesCorrectly()
    {
        // A small composite: classic counter + conditional accumulator.
        // Builds up @sum across a WHILE loop, but only adds when @i is
        // even (using IF inside the body).
        BatchResult result = await RunAsync(
            "DECLARE @i INT32 = 0; " +
            "DECLARE @sum INT32 = 0; " +
            "WHILE @i < 10 BEGIN " +
            "  IF @i = 0 OR @i = 2 OR @i = 4 OR @i = 6 OR @i = 8 " +
            "    SET @sum = @sum + @i; " +
            "  SET @i = @i + 1; " +
            "END");

        // 0 + 2 + 4 + 6 + 8 = 20
        Assert.Equal(20, Convert.ToInt32(result.FinalBindings["sum"]));
        Assert.Equal(10, Convert.ToInt32(result.FinalBindings["i"]));
    }

    // ───────────────────── FOR-counter ─────────────────────

    [Fact]
    public async Task ForCounter_LoopsInclusivelyFromStartToEnd()
    {
        // Auto-declares @i, runs body 5 times (i = 1..5 inclusive), accumulates
        // the loop var into @sum. Confirms the auto-declare + per-iter SET +
        // body evaluation wiring all hang together.
        BatchResult result = await RunAsync(
            "DECLARE @sum INT32 = 0; " +
            "FOR @i = 1 TO 5 SET @sum = @sum + @i");

        Assert.Equal(15, Convert.ToInt32(result.FinalBindings["sum"]));
        // @i went out of scope when the loop's frame popped — must not survive.
        Assert.False(result.FinalBindings.ContainsKey("i"),
            "@i should not be visible after the FOR loop ends");
    }

    [Fact]
    public async Task ForCounter_StartGreaterThanEnd_BodyNeverRuns()
    {
        BatchResult result = await RunAsync(
            "DECLARE @ran INT32 = 0; " +
            "FOR @i = 5 TO 1 SET @ran = @ran + 1");

        Assert.Equal(0, Convert.ToInt32(result.FinalBindings["ran"]));
    }

    [Fact]
    public async Task ForCounter_BoundsReferenceVariables()
    {
        // The bounds expressions are evaluated once via the synthesise-SELECT
        // path, so they can reference enclosing variables.
        BatchResult result = await RunAsync(
            "DECLARE @lo INT32 = 2; " +
            "DECLARE @hi INT32 = 4; " +
            "DECLARE @count INT32 = 0; " +
            "FOR @i = @lo TO @hi SET @count = @count + 1");

        Assert.Equal(3, Convert.ToInt32(result.FinalBindings["count"]));
    }

    [Fact]
    public async Task ForCounter_BodyBlock_RunsAllChildren()
    {
        BatchResult result = await RunAsync(
            "DECLARE @a INT32 = 0; " +
            "DECLARE @b INT32 = 0; " +
            "FOR @i = 1 TO 3 BEGIN " +
            "  SET @a = @a + 1; " +
            "  SET @b = @b + @i; " +
            "END");

        Assert.Equal(3, Convert.ToInt32(result.FinalBindings["a"]));
        // 1 + 2 + 3 = 6
        Assert.Equal(6, Convert.ToInt32(result.FinalBindings["b"]));
    }

    [Fact]
    public async Task ForCounter_NestedLoops_CartesianAccumulator()
    {
        BatchResult result = await RunAsync(
            "DECLARE @total INT32 = 0; " +
            "FOR @i = 1 TO 3 BEGIN " +
            "  FOR @j = 1 TO 4 SET @total = @total + 1; " +
            "END");

        // 3 outer × 4 inner = 12 increments.
        Assert.Equal(12, Convert.ToInt32(result.FinalBindings["total"]));
    }

    // ───────────────────── FOR-IN ─────────────────────

    [Fact]
    public async Task ForIn_IteratesRowsFromTable_AccumulatesByOrdinal()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id", "amount"],
            [1, 10],
            [2, 20],
            [3, 30]);

        BatchResult result = await RunAsync(
            "DECLARE @sum INT32 = 0; " +
            "FOR @row IN (SELECT id, amount FROM orders) " +
            "  SET @sum = @sum + @row[1]",
            catalog);

        // 10 + 20 + 30 — positional access via ordinal index 1 picks `amount`.
        Assert.Equal(60, Convert.ToInt32(result.FinalBindings["sum"]));
        // @row went out of scope.
        Assert.False(result.FinalBindings.ContainsKey("row"));
    }

    [Fact]
    public async Task ForIn_IteratesRowsFromTable_AccumulatesByName()
    {
        // Same table, but resolve fields by name. Exercises the field-name
        // tracking path: the FOR-IN's source query columns flow into the
        // binding so @row['amount'] resolves at evaluation time.
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id", "amount"],
            [1, 10],
            [2, 20],
            [3, 30]);

        BatchResult result = await RunAsync(
            "DECLARE @sum INT32 = 0; " +
            "FOR @row IN (SELECT id, amount FROM orders) " +
            "  SET @sum = @sum + @row['amount']",
            catalog);

        Assert.Equal(60, Convert.ToInt32(result.FinalBindings["sum"]));
    }

    [Fact]
    public async Task ForIn_EmptySource_BodyNeverRuns()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id", "amount"]);

        BatchResult result = await RunAsync(
            "DECLARE @ran INT32 = 0; " +
            "FOR @row IN (SELECT id, amount FROM orders) " +
            "  SET @ran = @ran + 1",
            catalog);

        Assert.Equal(0, Convert.ToInt32(result.FinalBindings["ran"]));
    }

    [Fact]
    public async Task ForIn_BodyBlock_CombinesFieldsAcrossRows()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id", "amount"],
            [1, 10],
            [2, 20]);

        BatchResult result = await RunAsync(
            "DECLARE @ids INT32 = 0; " +
            "DECLARE @amts INT32 = 0; " +
            "FOR @row IN (SELECT id, amount FROM orders) BEGIN " +
            "  SET @ids = @ids + @row['id']; " +
            "  SET @amts = @amts + @row['amount']; " +
            "END",
            catalog);

        Assert.Equal(3, Convert.ToInt32(result.FinalBindings["ids"]));
        Assert.Equal(30, Convert.ToInt32(result.FinalBindings["amts"]));
    }

    [Fact]
    public async Task ForIn_RowVariable_StampsTypeIdWithSourceColumnNames()
    {
        // The struct DataValue assigned to @row must carry a TypeId that resolves to
        // a TypeDescriptor with the source query's column names. Without this, downstream
        // renderers (DevWeb formatter, anywhere a Struct value is displayed) fall back to
        // "f0..fN" because they have no schema to consult. Pins the registry-stamping
        // contract in ExecuteForInAsync at the place where the row struct is materialised.
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id", "amount"],
            [1, 10],
            [2, 20]);

        IReadOnlyList<Statement> stmts = SqlParser.ParseBatch(
            "FOR @row IN (SELECT id, amount FROM orders) SELECT @row");
        BatchExecutor exec = new(catalog);

        // Snapshot inside the callback because the RowBatch is disposed once the
        // event has been consumed by the host. We grab the descriptor by value;
        // the registry itself outlives the batch (it's per-query, not per-batch).
        List<TypeDescriptor?> capturedDescriptors = [];
        await exec.RunWithEventsAsync(
            stmts,
            evt =>
            {
                if (evt is CellRowBatchEvent r && r.Batch.Count > 0)
                {
                    DataValue cell = r.Batch[0][0];
                    Assert.Equal(DataKind.Struct, cell.Kind);
                    Assert.False(cell.IsArray);

                    TypeRegistry? types = r.Batch.Types;
                    capturedDescriptors.Add(types?.GetDescriptor(cell.TypeId));
                }
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        // One non-empty SELECT @row batch per source row.
        Assert.Equal(2, capturedDescriptors.Count);
        foreach (TypeDescriptor? desc in capturedDescriptors)
        {
            Assert.NotNull(desc);
            Assert.NotNull(desc!.Fields);
            Assert.Equal(["id", "amount"], desc.Fields!.Select(f => f.Name).ToArray());
        }
    }

    [Fact]
    public async Task ForIn_OuterVariable_VisibleInsideBody()
    {
        // Body should still see variables declared in the enclosing scope —
        // the per-iteration frame is on top of the outer frame, not in
        // place of it.
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id"],
            [1],
            [2]);

        BatchResult result = await RunAsync(
            "DECLARE @offset INT32 = 100; " +
            "DECLARE @sum INT32 = 0; " +
            "FOR @row IN (SELECT id FROM orders) " +
            "  SET @sum = @sum + @row['id'] + @offset",
            catalog);

        // (1 + 100) + (2 + 100) = 203
        Assert.Equal(203, Convert.ToInt32(result.FinalBindings["sum"]));
    }

    // ───────────────────── PRINT ─────────────────────

    private async Task<List<CellPrintBatchEvent>> CollectPrintsAsync(string sql, TableCatalog? catalog = null)
    {
        catalog ??= CreateCatalog();
        IReadOnlyList<Statement> stmts = SqlParser.ParseBatch(sql);
        BatchExecutor exec = new(catalog);
        List<CellPrintBatchEvent> prints = [];
        await exec.RunWithEventsAsync(
            stmts,
            evt =>
            {
                if (evt is CellPrintBatchEvent print) prints.Add(print);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);
        return prints;
    }

    [Fact]
    public async Task Print_StringLiteral_EmitsTextEvent()
    {
        List<CellPrintBatchEvent> prints = await CollectPrintsAsync("PRINT 'hello world'");
        Assert.Single(prints);
        Assert.Equal("hello world", prints[0].Text);
    }

    [Fact]
    public async Task Print_IntegerExpression_RendersInvariantCulture()
    {
        List<CellPrintBatchEvent> prints = await CollectPrintsAsync("PRINT 2 + 3 * 4");
        Assert.Single(prints);
        Assert.Equal("14", prints[0].Text);
    }

    [Fact]
    public async Task Print_BooleanLiteral_RendersLowercase()
    {
        // SQL convention is lowercase booleans, even though .NET defaults
        // to "True" / "False". PRINT normalises so debug output looks the
        // same as a SELECT projection of a boolean column.
        List<CellPrintBatchEvent> prints = await CollectPrintsAsync(
            "PRINT TRUE; PRINT FALSE");
        Assert.Equal(["true", "false"], prints.Select(p => p.Text));
    }

    [Fact]
    public async Task Print_VariableReference_EmitsBoundValue()
    {
        List<CellPrintBatchEvent> prints = await CollectPrintsAsync(
            "DECLARE @msg STRING = 'hello world from a long-enough literal'; " +
            "PRINT @msg");
        Assert.Single(prints);
        Assert.Equal("hello world from a long-enough literal", prints[0].Text);
    }

    [Fact]
    public async Task Print_NullValue_EmitsNullText()
    {
        // NULL prints as a null Text so consumers can render however they
        // like. Distinct from the literal string "null" so accidental NULL
        // exposure stays observably different from the rendered literal.
        List<CellPrintBatchEvent> prints = await CollectPrintsAsync(
            "DECLARE @x INT32; PRINT @x");
        Assert.Single(prints);
        Assert.Null(prints[0].Text);
    }

    [Fact]
    public async Task Print_InsideLoop_EmitsOneEventPerIteration()
    {
        List<CellPrintBatchEvent> prints = await CollectPrintsAsync(
            "FOR @i = 1 TO 5 PRINT @i");
        Assert.Equal(["1", "2", "3", "4", "5"], prints.Select(p => p.Text));
    }

    [Fact]
    public async Task Print_FunctionCall_RendersResult()
    {
        List<CellPrintBatchEvent> prints = await CollectPrintsAsync(
            "PRINT upper('hello')");
        Assert.Single(prints);
        Assert.Equal("HELLO", prints[0].Text);
    }

    [Fact]
    public async Task Print_DistinctFromSelect_DoesNotProduceRowEvent()
    {
        // Verify a PRINT cell emits a print event, not a row-batch event.
        TableCatalog catalog = CreateCatalog();
        IReadOnlyList<Statement> stmts = SqlParser.ParseBatch("PRINT 'tracing'");
        BatchExecutor exec = new(catalog);
        List<BatchEvent> events = [];
        await exec.RunWithEventsAsync(
            stmts,
            evt => { events.Add(evt); return ValueTask.CompletedTask; },
            CancellationToken.None);

        Assert.DoesNotContain(events, e => e is CellRowBatchEvent);
        Assert.Contains(events, e => e is CellPrintBatchEvent);
    }

    [Fact]
    public async Task Print_InsideProcedure_EventsFlowThroughExec()
    {
        // The proc body's PRINT should surface in the same event stream
        // the EXEC cell drives.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE trace(@n INT32) AS BEGIN " +
            "  PRINT 'enter trace' " +
            "  FOR @i = 1 TO @n PRINT @i " +
            "  PRINT 'exit trace' " +
            "END");

        List<CellPrintBatchEvent> prints = await CollectPrintsAsync(
            "EXEC proc.trace(3)", catalog);

        Assert.Equal(
            ["enter trace", "1", "2", "3", "exit trace"],
            prints.Select(p => p.Text));
    }

    // ───────────────────── ASSERT / RAISE ─────────────────────

    [Fact]
    public async Task Assert_PredicateTrue_ContinuesNormally()
    {
        BatchResult result = await RunAsync(
            "DECLARE @x INT32 = 5; " +
            "ASSERT @x > 1; " +
            "DECLARE @after INT32 = 99");

        Assert.Equal(99, Convert.ToInt32(result.FinalBindings["after"]));
    }

    [Fact]
    public async Task Assert_PredicateFalse_ThrowsWithMessage()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(
                "DECLARE @x INT32 = 0; " +
                "ASSERT @x > 1 MESSAGE 'x must be positive'"));
        Assert.Equal("x must be positive", ex.Message);
    }

    [Fact]
    public async Task Assert_PredicateFalse_NoMessage_DefaultsToFormattedPredicate()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(
                "DECLARE @x INT32 = 0; " +
                "ASSERT @x > 1"));
        Assert.Contains("Assertion failed", ex.Message);
        Assert.Contains("@x", ex.Message);
    }

    [Fact]
    public async Task Assert_PredicateNull_ThrowsLikeFalse()
    {
        // NULL is "unknown" — same as false for control-flow purposes,
        // matches IF/WHILE three-valued semantics.
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(
                "DECLARE @x INT32; " +
                "ASSERT @x > 1 MESSAGE 'unknown is not safe'"));
        Assert.Equal("unknown is not safe", ex.Message);
    }

    [Fact]
    public async Task Assert_MessageIsExpression_EvaluatesAtCallSite()
    {
        // The MESSAGE clause accepts any expression — including a variable
        // reference. Useful for surfacing the violating value in the error.
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(
                "DECLARE @msg STRING = 'x was too small'; " +
                "DECLARE @x INT32 = 7; " +
                "ASSERT @x > 100 MESSAGE @msg"));
        Assert.Equal("x was too small", ex.Message);
    }

    [Fact]
    public async Task Assert_InsideTry_IsCaught()
    {
        // ASSERT failures route through the standard exception channel,
        // so an enclosing TRY/CATCH catches them like any other error.
        BatchResult result = await RunAsync(
            "DECLARE @msg STRING = ''; " +
            "TRY ASSERT 1 = 2 MESSAGE 'arithmetic broke' " +
            "CATCH @err SET @msg = @err");

        Assert.Equal("arithmetic broke", result.FinalBindings["msg"]);
    }

    [Fact]
    public async Task Raise_StringLiteral_Throws()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync("RAISE 'something went wrong'"));
        Assert.Equal("something went wrong", ex.Message);
    }

    [Fact]
    public async Task Raise_ExpressionRendersToMessage()
    {
        // RAISE accepts any expression; non-strings are rendered with the
        // same rules as PRINT (numbers in invariant culture, booleans
        // lowercase). Here we raise an INT32 directly to confirm the
        // renderer kicks in.
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(
                "DECLARE @code INT32 = 42; " +
                "RAISE @code"));
        Assert.Equal("42", ex.Message);
    }

    [Fact]
    public async Task Raise_InsideCatch_RethrowsViaErrorVariable()
    {
        // Standard pattern: log the failure then propagate. Outer TRY
        // catches the rethrown error; the message survives the round-trip.
        BatchResult result = await RunAsync(
            "DECLARE @outer_msg STRING = ''; " +
            "TRY BEGIN " +
            "  TRY ASSERT 1 = 2 MESSAGE 'inner failure' " +
            "  CATCH @inner RAISE @inner " +
            "END " +
            "CATCH @outer SET @outer_msg = @outer");

        Assert.Equal("inner failure", result.FinalBindings["outer_msg"]);
    }

    [Fact]
    public async Task Raise_InsideLoop_AbortsLoop()
    {
        // RAISE is not control flow — it throws. Without a TRY around the
        // RAISE, the loop terminates and the exception propagates.
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(
                "FOR @i = 1 TO 10 BEGIN " +
                "  IF @i = 5 RAISE 'hit five' " +
                "END"));
        Assert.Equal("hit five", ex.Message);
    }

    [Fact]
    public async Task Raise_InsideTry_FinallyStillRuns()
    {
        // RAISE inside a TRY behaves like any other thrown error: CATCH
        // handles it, FINALLY runs.
        BatchResult result = await RunAsync(
            "DECLARE @caught BOOLEAN = FALSE; " +
            "DECLARE @cleaned BOOLEAN = FALSE; " +
            "TRY RAISE 'oops' " +
            "CATCH @err SET @caught = TRUE " +
            "FINALLY SET @cleaned = TRUE");

        Assert.Equal(true, result.FinalBindings["caught"]);
        Assert.Equal(true, result.FinalBindings["cleaned"]);
    }

    // ───────────────────── TRY / CATCH / FINALLY ─────────────────────

    [Fact]
    public async Task Try_NoError_RunsTryBody_SkipsCatch()
    {
        BatchResult result = await RunAsync(
            "DECLARE @ran_try BOOLEAN = FALSE; " +
            "DECLARE @ran_catch BOOLEAN = FALSE; " +
            "TRY SET @ran_try = TRUE " +
            "CATCH @e SET @ran_catch = TRUE");

        Assert.Equal(true, result.FinalBindings["ran_try"]);
        Assert.Equal(false, result.FinalBindings["ran_catch"]);
    }

    [Fact]
    public async Task Try_ErrorInTry_RunsCatch_BindsErrorMessage()
    {
        // Trigger a runtime error inside TRY (calling a non-existent
        // procedure) so the catch path takes over. The message is bound
        // to @err — capture by writing it into an outer-scope variable.
        TableCatalog catalog = CreateCatalog();
        BatchResult result = await RunAsync(
            "DECLARE @msg STRING = ''; " +
            "TRY EXEC proc.does_not_exist() " +
            "CATCH @err SET @msg = @err",
            catalog);

        Assert.NotEqual(string.Empty, (string?)result.FinalBindings["msg"]);
        Assert.Contains("does_not_exist", (string)result.FinalBindings["msg"]!);
    }

    [Fact]
    public async Task Try_ErrorVariable_ScopedToCatchBlock_NotVisibleAfter()
    {
        // @err disappears after CATCH ends — referencing it later raises.
        TableCatalog catalog = CreateCatalog();
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(
                "DECLARE @msg STRING = ''; " +
                "TRY EXEC proc.does_not_exist() " +
                "CATCH @err SET @msg = @err; " +
                "SET @msg = @err",  // @err out of scope here
                catalog));
        Assert.Contains("err", ex.Message);
    }

    [Fact]
    public async Task Try_BlockBody_RunsAllStatements()
    {
        BatchResult result = await RunAsync(
            "DECLARE @sum INT32 = 0; " +
            "TRY BEGIN " +
            "  SET @sum = @sum + 1 " +
            "  SET @sum = @sum + 2 " +
            "  SET @sum = @sum + 3 " +
            "END " +
            "CATCH @e SET @sum = -1");

        Assert.Equal(6, Convert.ToInt32(result.FinalBindings["sum"]));
    }

    [Fact]
    public async Task Try_Finally_RunsAfterSuccessfulTry()
    {
        BatchResult result = await RunAsync(
            "DECLARE @ran_try BOOLEAN = FALSE; " +
            "DECLARE @ran_finally BOOLEAN = FALSE; " +
            "TRY SET @ran_try = TRUE " +
            "CATCH @e SET @ran_try = FALSE " +
            "FINALLY SET @ran_finally = TRUE");

        Assert.Equal(true, result.FinalBindings["ran_try"]);
        Assert.Equal(true, result.FinalBindings["ran_finally"]);
    }

    [Fact]
    public async Task Try_Finally_RunsAfterCaughtError()
    {
        TableCatalog catalog = CreateCatalog();
        BatchResult result = await RunAsync(
            "DECLARE @ran_catch BOOLEAN = FALSE; " +
            "DECLARE @ran_finally BOOLEAN = FALSE; " +
            "TRY EXEC proc.does_not_exist() " +
            "CATCH @e SET @ran_catch = TRUE " +
            "FINALLY SET @ran_finally = TRUE",
            catalog);

        Assert.Equal(true, result.FinalBindings["ran_catch"]);
        Assert.Equal(true, result.FinalBindings["ran_finally"]);
    }

    [Fact]
    public async Task Try_FinallyOnly_RunsEvenWithoutHandlerNeed()
    {
        // No FINALLY-without-CATCH form — TRY always pairs with CATCH —
        // but FINALLY runs whether CATCH fired or not. Confirm the
        // simple "no error" case fires FINALLY.
        BatchResult result = await RunAsync(
            "DECLARE @cleanup BOOLEAN = FALSE; " +
            "TRY DECLARE @x INT32 = 1 " +
            "CATCH @e SET @cleanup = FALSE " +
            "FINALLY SET @cleanup = TRUE");

        Assert.Equal(true, result.FinalBindings["cleanup"]);
    }

    [Fact]
    public async Task Try_NoFinally_OptionalClause()
    {
        // FINALLY is optional; bare TRY/CATCH parses and runs.
        BatchResult result = await RunAsync(
            "DECLARE @done BOOLEAN = FALSE; " +
            "TRY SET @done = TRUE " +
            "CATCH @e SET @done = FALSE");

        Assert.Equal(true, result.FinalBindings["done"]);
    }

    [Fact]
    public async Task Try_FinallyThrows_SupersedesPendingException()
    {
        // FINALLY raising its own error should win over the original;
        // matches C# / Java try/finally semantics. Use EXEC of a missing
        // procedure inside FINALLY to provoke a fresh throw.
        TableCatalog catalog = CreateCatalog();
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(
                "TRY EXEC proc.original_error() " +
                "CATCH @e EXEC proc.catch_error() " +
                "FINALLY EXEC proc.finally_error()",
                catalog));
        Assert.Contains("finally_error", ex.Message);
    }

    [Fact]
    public async Task Try_CatchThrows_FinallyStillRuns_ExceptionPropagates()
    {
        // If CATCH itself throws and FINALLY is clean, the catch error
        // should propagate after FINALLY runs.
        TableCatalog catalog = CreateCatalog();
        BatchResult? result = null;
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            // Wrap the executor so we can introspect after-the-fact;
            // FinalBindings inside RunAsync would still be unreachable
            // because the exception escapes — instead verify via the message.
            result = await RunAsync(
                "TRY EXEC proc.original_error() " +
                "CATCH @e EXEC proc.catch_error() " +
                "FINALLY DECLARE @cleanup BOOLEAN = TRUE",
                catalog);
        });

        Assert.Contains("catch_error", ex.Message);
    }

    [Fact]
    public async Task Try_BreakInsideTry_RoutesThroughFinallyThenExitsLoop()
    {
        // BREAK inside a TRY inside a loop: FINALLY must run, then the
        // loop must exit. CATCH is bypassed (control-flow signals don't
        // hit CATCH).
        BatchResult result = await RunAsync(
            "DECLARE @sum INT32 = 0; " +
            "DECLARE @cleanup_count INT32 = 0; " +
            "DECLARE @catch_count INT32 = 0; " +
            "FOR @i = 1 TO 10 BEGIN " +
            "  TRY BEGIN " +
            "    IF @i = 4 BREAK " +
            "    SET @sum = @sum + @i " +
            "  END " +
            "  CATCH @e SET @catch_count = @catch_count + 1 " +
            "  FINALLY SET @cleanup_count = @cleanup_count + 1 " +
            "END");

        // Iterations: i=1,2,3 run cleanly; i=4 hits BREAK before sum updates.
        // FINALLY runs all four times (1, 2, 3, 4). CATCH never fires.
        Assert.Equal(6, Convert.ToInt32(result.FinalBindings["sum"]));      // 1+2+3
        Assert.Equal(4, Convert.ToInt32(result.FinalBindings["cleanup_count"]));
        Assert.Equal(0, Convert.ToInt32(result.FinalBindings["catch_count"]));
    }

    [Fact]
    public async Task Try_ContinueInsideTry_RoutesThroughFinallyThenNextIteration()
    {
        // CONTINUE inside a TRY inside a loop: FINALLY runs, then the
        // loop advances to the next iteration. CATCH bypassed.
        BatchResult result = await RunAsync(
            "DECLARE @sum INT32 = 0; " +
            "DECLARE @cleanup_count INT32 = 0; " +
            "FOR @i = 1 TO 5 BEGIN " +
            "  TRY BEGIN " +
            "    IF @i % 2 = 0 CONTINUE " +
            "    SET @sum = @sum + @i " +
            "  END " +
            "  CATCH @e SET @sum = -1 " +
            "  FINALLY SET @cleanup_count = @cleanup_count + 1 " +
            "END");

        // Odd values accumulate: 1+3+5 = 9. FINALLY fires on every iter (5).
        Assert.Equal(9, Convert.ToInt32(result.FinalBindings["sum"]));
        Assert.Equal(5, Convert.ToInt32(result.FinalBindings["cleanup_count"]));
    }

    [Fact]
    public async Task Try_NestedTry_InnerCatchHandlesInnerError()
    {
        // Outer TRY has an inner TRY that catches; outer CATCH should not fire.
        TableCatalog catalog = CreateCatalog();
        BatchResult result = await RunAsync(
            "DECLARE @inner_caught BOOLEAN = FALSE; " +
            "DECLARE @outer_caught BOOLEAN = FALSE; " +
            "TRY BEGIN " +
            "  TRY EXEC proc.does_not_exist() " +
            "  CATCH @inner SET @inner_caught = TRUE " +
            "END " +
            "CATCH @outer SET @outer_caught = TRUE",
            catalog);

        Assert.Equal(true, result.FinalBindings["inner_caught"]);
        Assert.Equal(false, result.FinalBindings["outer_caught"]);
    }

    [Fact]
    public async Task Try_NestedTry_InnerCatchRethrows_OuterCatchFires()
    {
        // Inner CATCH rethrows by triggering a new error; outer CATCH
        // handles. Confirms exceptions propagate through nested TRYs as
        // expected.
        TableCatalog catalog = CreateCatalog();
        BatchResult result = await RunAsync(
            "DECLARE @outer_caught BOOLEAN = FALSE; " +
            "TRY BEGIN " +
            "  TRY EXEC proc.first_error() " +
            "  CATCH @inner EXEC proc.second_error() " +
            "END " +
            "CATCH @outer SET @outer_caught = TRUE",
            catalog);

        Assert.Equal(true, result.FinalBindings["outer_caught"]);
    }

    // ───────────────────── BREAK / CONTINUE ─────────────────────

    [Fact]
    public async Task While_Break_ExitsLoopImmediately()
    {
        // Loop would naturally run i=0..9; BREAK fires when i=5, so the
        // accumulator stops at 0+1+2+3+4=10 and i is left at 5 (BREAK
        // bypasses the SET @i = @i + 1 line).
        BatchResult result = await RunAsync(
            "DECLARE @i INT32 = 0; " +
            "DECLARE @sum INT32 = 0; " +
            "WHILE @i < 10 BEGIN " +
            "  IF @i = 5 BREAK; " +
            "  SET @sum = @sum + @i; " +
            "  SET @i = @i + 1; " +
            "END");

        Assert.Equal(10, Convert.ToInt32(result.FinalBindings["sum"]));
        Assert.Equal(5, Convert.ToInt32(result.FinalBindings["i"]));
    }

    [Fact]
    public async Task While_Continue_SkipsRestOfIterationButReevaluatesPredicate()
    {
        // Predicate is on @i, but @i only advances inside the body before
        // CONTINUE. The classic shape: increment first, then conditionally
        // skip — sums only the odd numbers in 1..10.
        BatchResult result = await RunAsync(
            "DECLARE @i INT32 = 0; " +
            "DECLARE @sum INT32 = 0; " +
            "WHILE @i < 10 BEGIN " +
            "  SET @i = @i + 1; " +
            "  IF @i % 2 = 0 CONTINUE; " +
            "  SET @sum = @sum + @i; " +
            "END");

        // 1 + 3 + 5 + 7 + 9 = 25
        Assert.Equal(25, Convert.ToInt32(result.FinalBindings["sum"]));
        Assert.Equal(10, Convert.ToInt32(result.FinalBindings["i"]));
    }

    [Fact]
    public async Task ForCounter_Break_ExitsLoopImmediately()
    {
        BatchResult result = await RunAsync(
            "DECLARE @sum INT32 = 0; " +
            "FOR @i = 1 TO 100 BEGIN " +
            "  IF @i > 5 BREAK; " +
            "  SET @sum = @sum + @i; " +
            "END");

        // 1 + 2 + 3 + 4 + 5 = 15
        Assert.Equal(15, Convert.ToInt32(result.FinalBindings["sum"]));
    }

    [Fact]
    public async Task ForCounter_Continue_SkipsRestOfIteration()
    {
        BatchResult result = await RunAsync(
            "DECLARE @sum INT32 = 0; " +
            "FOR @i = 1 TO 10 BEGIN " +
            "  IF @i % 2 = 0 CONTINUE; " +
            "  SET @sum = @sum + @i; " +
            "END");

        // 1 + 3 + 5 + 7 + 9 = 25
        Assert.Equal(25, Convert.ToInt32(result.FinalBindings["sum"]));
    }

    [Fact]
    public async Task ForIn_Break_ExitsLoopOnFirstHit()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id"],
            [1], [2], [3], [4], [5]);

        BatchResult result = await RunAsync(
            "DECLARE @first INT32 = 0; " +
            "FOR @row IN (SELECT id FROM orders) BEGIN " +
            "  SET @first = @row['id']; " +
            "  BREAK; " +
            "END",
            catalog);

        Assert.Equal(1, Convert.ToInt32(result.FinalBindings["first"]));
    }

    [Fact]
    public async Task ForIn_Continue_SkipsRestOfIteration()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id"],
            [1], [2], [3], [4], [5]);

        BatchResult result = await RunAsync(
            "DECLARE @sum INT32 = 0; " +
            "FOR @row IN (SELECT id FROM orders) BEGIN " +
            "  IF @row['id'] % 2 = 0 CONTINUE; " +
            "  SET @sum = @sum + @row['id']; " +
            "END",
            catalog);

        // 1 + 3 + 5 = 9
        Assert.Equal(9, Convert.ToInt32(result.FinalBindings["sum"]));
    }

    [Fact]
    public async Task Break_BreaksOnlyInnermostLoop()
    {
        // Nested FOR; inner BREAK fires when j > i. Outer loop continues
        // after each inner BREAK, so @sum collects only j ≤ i for each
        // (i, j) pair: i=1 → j=1; i=2 → j=1,2; i=3 → j=1,2,3.
        BatchResult result = await RunAsync(
            "DECLARE @sum INT32 = 0; " +
            "FOR @i = 1 TO 3 BEGIN " +
            "  FOR @j = 1 TO 10 BEGIN " +
            "    IF @j > @i BREAK; " +
            "    SET @sum = @sum + 1; " +
            "  END " +
            "END");

        // 1 + 2 + 3 = 6 inner-iterations counted.
        Assert.Equal(6, Convert.ToInt32(result.FinalBindings["sum"]));
    }

    [Fact]
    public async Task Break_OutsideLoop_AtBatchTopLevel_Throws()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync("BREAK"));
        Assert.Contains("BREAK", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Continue_OutsideLoop_AtBatchTopLevel_Throws()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync("CONTINUE"));
        Assert.Contains("CONTINUE", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Break_OutsideLoop_InsideIfBranch_Throws()
    {
        // BREAK inside an IF that itself isn't inside a loop — IF doesn't
        // count as a loop, so the signal escapes to the entry point.
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(
                "DECLARE @x INT32 = 1; " +
                "IF @x = 1 BREAK"));
        Assert.Contains("BREAK", ex.Message, StringComparison.Ordinal);
    }

    // ───────────────────── Optional ; between statements ─────────────────────

    [Fact]
    public async Task Batch_NoSemicolonAfterEnd_ParsesNextStatement()
    {
        // The user-reported case: writing `;` after END to separate it from
        // the next statement reads as redundant since END already terminates
        // the block. The batch grammar allows omitting the inter-statement
        // separator.
        BatchResult result = await RunAsync(
            "DECLARE @sum INT64 = 0 " +
            "FOR @i = 1 TO 3 BEGIN " +
            "  SET @sum = @sum + @i " +
            "END " +
            "DECLARE @final INT64 = @sum");

        Assert.Equal(6L, Convert.ToInt64(result.FinalBindings["sum"]));
        Assert.Equal(6L, Convert.ToInt64(result.FinalBindings["final"]));
    }

    [Fact]
    public async Task Batch_NoSemicolonsAtAll_StillParses()
    {
        // Statements anchored on keywords; no separator needed at all when
        // each statement starts with one. This isn't a recommended style,
        // but it should parse.
        BatchResult result = await RunAsync(
            "DECLARE @a INT32 = 1 " +
            "DECLARE @b INT32 = 2 " +
            "SET @a = @a + @b");

        Assert.Equal(3, Convert.ToInt32(result.FinalBindings["a"]));
    }

    [Fact]
    public async Task Block_NoSemicolonBeforeEnd_ParsesAllStatements()
    {
        // BEGIN/END mirrors the top-level grammar: separators between
        // statements are optional.
        BatchResult result = await RunAsync(
            "DECLARE @a INT32 = 0; " +
            "BEGIN " +
            "  SET @a = 1 " +
            "  SET @a = @a + 10 " +
            "END");

        Assert.Equal(11, Convert.ToInt32(result.FinalBindings["a"]));
    }

    // ───────────────────── DECLARE coerces initializer to declared type ─────────────────────

    [Fact]
    public async Task Declare_WithDeclaredType_CoercesInitializerToDeclaredKind()
    {
        // Without coercion, the literal `0` (parsed as the narrowest fitting
        // integer kind) would silently win over the declared INT64 — leaving
        // @sum bound to Int8. Subsequent arithmetic (which currently widens
        // to Float32 anyway) would then be wrong on different ground. The
        // coercion ensures the binding's kind matches the declaration.
        BatchResult result = await RunAsync("DECLARE @sum INT64 = 0");

        // The result is materialised through the AsInt64 path, which would
        // throw if the value were still Int8.
        Assert.Equal(0L, Convert.ToInt64(result.FinalBindings["sum"]));
    }

    // ───────────────────── Multi-variable SELECT assignment ─────────────────────

    [Fact]
    public async Task SelectAssign_NoFrom_SingleAssignment_BindsValue()
    {
        BatchResult result = await RunAsync(
            "DECLARE @x INT64 = 0; " +
            "SELECT @x = 42");
        Assert.Equal(42L, Convert.ToInt64(result.FinalBindings["x"]));
    }

    [Fact]
    public async Task SelectAssign_NoFrom_MultiAssignment_BindsAll()
    {
        // Multiple variables in a single SELECT-assignment all get their
        // values from the single computed row. No FROM, so the query
        // produces exactly one row.
        BatchResult result = await RunAsync(
            "DECLARE @a INT64 = 0; " +
            "DECLARE @b INT64 = 0; " +
            "DECLARE @c INT64 = 0; " +
            "SELECT @a = 1, @b = 2, @c = 3");
        Assert.Equal(1L, Convert.ToInt64(result.FinalBindings["a"]));
        Assert.Equal(2L, Convert.ToInt64(result.FinalBindings["b"]));
        Assert.Equal(3L, Convert.ToInt64(result.FinalBindings["c"]));
    }

    [Fact]
    public async Task SelectAssign_FromTable_LastRowWins()
    {
        // T-SQL semantics: with multiple matching rows, the variable ends
        // up with the value from the last row iterated. ORDER BY pins the
        // outcome deterministically.
        TableCatalog catalog = CreateCatalog("nums",
            columns: ["v"],
            [10],
            [20],
            [30]);

        BatchResult result = await RunAsync(
            "DECLARE @last INT64 = 0; " +
            "SELECT @last = v FROM nums ORDER BY v",
            catalog);

        Assert.Equal(30L, Convert.ToInt64(result.FinalBindings["last"]));
    }

    [Fact]
    public async Task SelectAssign_ZeroRows_VariableUnchanged()
    {
        // If the source produces no rows, the variable retains whatever
        // value it had before the assignment SELECT. Matches T-SQL.
        TableCatalog catalog = CreateCatalog("nums",
            columns: ["v"],
            [10]);

        BatchResult result = await RunAsync(
            "DECLARE @x INT64 = 999; " +
            "SELECT @x = v FROM nums WHERE v > 1000",
            catalog);

        Assert.Equal(999L, Convert.ToInt64(result.FinalBindings["x"]));
    }

    [Fact]
    public async Task SelectAssign_MultipleColumnsOverRows_AllUpdatePerRow()
    {
        // Each row's values bind to all assignment targets in lockstep —
        // but only the last row's values stick.
        TableCatalog catalog = CreateCatalog("pairs",
            columns: ["a", "b"],
            [1, 100],
            [2, 200],
            [3, 300]);

        BatchResult result = await RunAsync(
            "DECLARE @x INT64 = 0; " +
            "DECLARE @y INT64 = 0; " +
            "SELECT @x = a, @y = b FROM pairs ORDER BY a",
            catalog);

        Assert.Equal(3L, Convert.ToInt64(result.FinalBindings["x"]));
        Assert.Equal(300L, Convert.ToInt64(result.FinalBindings["y"]));
    }

    [Fact]
    public async Task SelectAssign_ExpressionRhs_EvaluatesPerRow()
    {
        TableCatalog catalog = CreateCatalog("nums",
            columns: ["v"],
            [10]);

        BatchResult result = await RunAsync(
            "DECLARE @doubled INT64 = 0; " +
            "SELECT @doubled = v + v FROM nums",
            catalog);

        Assert.Equal(20L, Convert.ToInt64(result.FinalBindings["doubled"]));
    }

    [Fact]
    public async Task SelectAssign_AliasedComparison_StaysComparison_NotAssignment()
    {
        // The alias is the explicit "I want a comparison, not an assignment"
        // signal. Confirms the parser DOESN'T lift this case into
        // assignment form — the variable's pre-existing value is intact.
        BatchResult result = await RunAsync(
            "DECLARE @x INT32 = 5; " +
            "SELECT @x = 5 AS isFive");

        // @x is unchanged because the SELECT was a projection, not an
        // assignment. Comparison `@x = 5` evaluated to TRUE for the
        // single synthetic row but the boolean result didn't bind anywhere.
        Assert.Equal(5, Convert.ToInt32(result.FinalBindings["x"]));
    }

    [Fact]
    public async Task SelectAssign_MixedWithProjection_Throws()
    {
        // All-or-nothing: mixing an assignment with a regular projection
        // is rejected with a clear message.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => RunAsync(
                "DECLARE @x INT64 = 0; " +
                "SELECT @x = 1, 'hello'"));
    }

    [Fact]
    public async Task SelectAssign_UndeclaredVariable_Throws()
    {
        // The variable scope's Set call surfaces the missing binding.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => RunAsync("SELECT @undeclared = 1"));
    }

    [Fact]
    public async Task SelectAssign_InsideForLoop_AssignsPerIteration()
    {
        // SELECT-assignment inside a FOR-IN body: each iteration runs the
        // assignment SELECT against the body's enclosing scope. Confirms
        // the assignment routes through the same BatchContext as DECLARE
        // / SET.
        TableCatalog catalog = CreateCatalog("nums",
            columns: ["v"],
            [1],
            [2],
            [3]);

        BatchResult result = await RunAsync(
            "DECLARE @sum INT64 = 0; " +
            "FOR @row IN (SELECT v FROM nums) " +
            "  SELECT @sum = @sum + @row['v']",
            catalog);

        Assert.Equal(6L, Convert.ToInt64(result.FinalBindings["sum"]));
    }

    // ───────────────────── Top-level @var without batch context ─────────────────────

    [Fact]
    public async Task BareSelect_OutsideBatch_VariableReference_Throws()
    {
        // A SELECT @x run via the standard catalog.Plan(...) path (no
        // BatchContext attached) must throw at evaluation time —
        // VariableExpression has no scope to resolve against.
        TableCatalog catalog = CreateCatalog();
        IQueryPlan plan = catalog.Plan("SELECT @x");

        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
        {
            await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
            {
                _ = batch;
            }
        });
    }

    // ───────────────────── Catalog dispatch alignment ─────────────────────

    /// <summary>
    /// Regression: every DDL/DML statement type must flow through BatchExecutor
    /// without hitting the throw-on-unknown-statement default. The streaming
    /// endpoint runs everything via <c>RunWithEventsAsync</c>, so any statement
    /// the parser accepts but BatchExecutor doesn't dispatch lands as an
    /// uncaught exception in the user's browser. The unified catalog-dispatch
    /// arm makes this list growable from <c>TableCatalog.Plan</c> alone.
    /// </summary>
    [Fact]
    public async Task BatchExecutor_DispatchesAllDdlAndDml()
    {
        TableCatalog catalog = CreateCatalog();
        // CREATE TABLE → INSERT → UPDATE → DELETE → REINDEX (skipped on TEMP
        // — we exercise it on a persistent table elsewhere). ALTER TABLE
        // ADD/DROP COLUMN. All run through the same BatchExecutor path the
        // streaming endpoint uses.
        BatchResult result = await RunAsync(
            "CREATE TEMP TABLE t (id Int32, name String); " +
            "INSERT INTO t VALUES (1, 'a'), (2, 'b'); " +
            "UPDATE t SET name = 'X' WHERE id = 1; " +
            "DELETE FROM t WHERE id = 2; " +
            "ALTER TABLE t ADD COLUMN extra String; " +
            "ALTER TABLE t DROP COLUMN extra; " +
            "DROP TABLE t",
            catalog);

        // No throw is the regression signal. Bindings should be empty —
        // these statements bind no variables.
        Assert.Empty(result.FinalBindings);
    }
}

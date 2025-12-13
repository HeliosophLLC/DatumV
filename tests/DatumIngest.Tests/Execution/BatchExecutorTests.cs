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
}

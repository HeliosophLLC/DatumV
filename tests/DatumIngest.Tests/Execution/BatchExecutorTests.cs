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

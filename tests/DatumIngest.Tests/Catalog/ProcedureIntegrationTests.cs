using DatumIngest.Catalog;
using DatumIngest.Catalog.Registries;
using DatumIngest.Execution;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// End-to-end tests for procedure DDL and invocation: registration through
/// <see cref="TableCatalog.Plan(string)"/>, side effects on the
/// <see cref="ProcedureRegistry"/>, and full-batch execution that exercises
/// <c>CALLX(...)</c> through the procedural batch executor.
/// </summary>
public class ProcedureIntegrationTests : ServiceTestBase
{
    private async Task<BatchResult> RunBatchAsync(string sql, TableCatalog catalog)
    {
        IReadOnlyList<Statement> stmts = SqlParser.ParseBatch(sql);
        BatchExecutor exec = new(catalog);
        return await exec.ExecuteAsync(stmts, CancellationToken.None);
    }

    // ───────────────────── Registration ─────────────────────

    [Fact]
    public void CreateProcedure_RegistersInCatalog()
    {
        TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE PROCEDURE noop() AS BEGIN SELECT 1 END");

        Assert.True(catalog.Procedures.TryGet("noop", out ProcedureDescriptor? proc));
        Assert.Equal("noop", proc!.Name);
    }

    [Fact]
    public void CreateProcedure_PersistsOriginalSourceText()
    {
        // Source text is stored verbatim so introspection / persistence
        // round-trip preserves the user's formatting.
        TableCatalog catalog = CreateCatalog();
        const string sql = "CREATE PROCEDURE noop() AS BEGIN SELECT 1 END";

        catalog.Plan(sql);

        Assert.True(catalog.Procedures.TryGet("noop", out ProcedureDescriptor? proc));
        Assert.Equal(sql, proc!.SourceText);
    }

    [Fact]
    public void CreateProcedure_DuplicateName_Throws()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE PROCEDURE foo() AS BEGIN SELECT 1 END");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan("CREATE PROCEDURE foo() AS BEGIN SELECT 2 END"));
        Assert.Contains("already registered", ex.Message);
    }

    [Fact]
    public void CreateProcedure_OrReplace_OverwritesExisting()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE PROCEDURE foo() AS BEGIN SELECT 1 END");
        catalog.Plan("CREATE OR REPLACE PROCEDURE foo() AS BEGIN SELECT 99 END");

        Assert.True(catalog.Procedures.TryGet("foo", out ProcedureDescriptor? proc));
        // Verified via source text since that's what we persist.
        Assert.Contains("99", proc!.SourceText);
    }

    [Fact]
    public void CreateProcedure_OrAlter_OverwritesExisting()
    {
        // OR ALTER is a T-SQL synonym for OR REPLACE — should behave identically.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE PROCEDURE foo() AS BEGIN SELECT 1 END");
        catalog.Plan("CREATE OR ALTER PROCEDURE foo() AS BEGIN SELECT 99 END");

        Assert.True(catalog.Procedures.TryGet("foo", out ProcedureDescriptor? proc));
        Assert.Contains("99", proc!.SourceText);
    }

    [Fact]
    public void CreateProcedure_IfNotExists_NoOpWhenAlreadyRegistered()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE PROCEDURE foo() AS BEGIN SELECT 1 END");
        // Second registration is a no-op; original definition wins.
        catalog.Plan("CREATE PROCEDURE IF NOT EXISTS foo() AS BEGIN SELECT 99 END");

        Assert.True(catalog.Procedures.TryGet("foo", out ProcedureDescriptor? proc));
        Assert.Contains("SELECT 1", proc!.SourceText);
        Assert.DoesNotContain("99", proc.SourceText);
    }

    [Fact]
    public void DropProcedure_RemovesEntry()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE PROCEDURE foo() AS BEGIN SELECT 1 END");
        catalog.Plan("DROP PROCEDURE foo");

        Assert.False(catalog.Procedures.TryGet("foo", out _));
    }

    [Fact]
    public void DropProcedure_NonExistent_Throws()
    {
        TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan("DROP PROCEDURE nonexistent"));
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void DropProcedure_IfExists_NoOpWhenAbsent()
    {
        TableCatalog catalog = CreateCatalog();
        // Should not throw.
        catalog.Plan("DROP PROCEDURE IF EXISTS nonexistent");

        Assert.False(catalog.Procedures.TryGet("nonexistent", out _));
    }

    [Fact]
    public void Procedure_InSelectPosition_Rejected()
    {
        // S7d locks the rule: procedures REQUIRE CALL; using a procedure
        // name in expression position (SELECT, WHERE, etc.) is a user
        // error. The planner surfaces a specific diagnostic instead of
        // falling through to scalar dispatch's opaque "Unknown function".
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE PROCEDURE noop() AS BEGIN SELECT 1 END");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan("SELECT noop()"));
        Assert.Contains("procedure", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CALL", ex.Message);
    }

    [Fact]
    public async Task CallProcedure_BodyReferencingUnknownFunction_ThrowsAtRuntime()
    {
        // Post-S7d the body inliner only inlines registered macros and
        // lets everything else pass through. Unresolved function calls
        // in the body surface at CALL time when the body's SELECT
        // runs through the scalar dispatch path.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE PROCEDURE foo() AS BEGIN SELECT never_defined(1) END");

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(
            () => RunBatchAsync("CALL foo()", catalog));
        Assert.Contains("never_defined", ex.Message);
    }

    // ───────────────────── Invocation ─────────────────────

    [Fact]
    public async Task ExecProc_NoArgs_RunsBody_BindsVariables()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE setone() AS BEGIN " +
            "  DECLARE x INT32 = 7 " +
            "END");

        // The procedure's x lives in its own ExecutionContext and is gone after
        // the procedure ends — so we can't observe it via FinalBindings.
        // We assert that the call doesn't throw and the procedure is
        // registered & callable.
        BatchResult result = await RunBatchAsync(
            "CALL setone()",
            catalog);

        // No outer-scope bindings produced; the test confirms invocation
        // succeeded without surfacing the procedure's private state.
        Assert.Empty(result.FinalBindings);
    }

    [Fact]
    public async Task ExecProc_WithArgs_DeclaresParametersInBodyScope()
    {
        // Arguments evaluate in the caller's scope, then declare into the
        // procedure's frame. The procedure body runs against its own context.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE assign_outer(v INT64) AS BEGIN " +
            "  DECLARE local INT64 = v + 100 " +
            "END");

        BatchResult result = await RunBatchAsync(
            "DECLARE answer INT64 = 0; " +
            "CALL assign_outer(5)",
            catalog);

        // answer untouched by the procedure (procedure has its own scope).
        Assert.Equal(0L, Convert.ToInt64(result.FinalBindings["answer"]));
    }

    [Fact]
    public async Task ExecProc_ProcedureSeeMutationFromCaller_ViaArgsOnly()
    {
        // Args evaluate in caller scope — the procedure receives the
        // computed value of counter, not a reference. Mutations inside
        // the procedure cannot leak out.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE shadow(v INT64) AS BEGIN " +
            "  SET v = 999 " +
            "END");

        BatchResult result = await RunBatchAsync(
            "DECLARE counter INT64 = 5; " +
            "CALL shadow(counter)",
            catalog);

        // counter is 5 still — the procedure's SET on its local v
        // doesn't touch the caller's counter.
        Assert.Equal(5L, Convert.ToInt64(result.FinalBindings["counter"]));
    }

    [Fact]
    public async Task ExecProc_ArgArityMismatch_Throws()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE need_two(a INT32, b INT32) AS BEGIN " +
            "  DECLARE sum INT32 = a + b " +
            "END");

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => RunBatchAsync("CALL need_two(1)", catalog));
    }

    [Fact]
    public async Task ExecProc_Unregistered_Throws()
    {
        // Post-S7d CALL falls through to scalar dispatch when the target
        // isn't a registered procedure, so an unknown name surfaces via
        // <see cref="ExpressionEvaluationException"/> wrapping
        // "Unknown function".
        TableCatalog catalog = CreateCatalog();

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(
            () => RunBatchAsync("CALL never_registered()", catalog));
        Assert.Contains("never_registered", ex.Message);
    }

    [Fact]
    public async Task ExecProc_NotNullParam_NullArg_Throws()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE need_name(name STRING IS NOT NULL) AS BEGIN " +
            "  SELECT name " +
            "END");

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(
            () => RunBatchAsync(
                "DECLARE n STRING = NULL; CALL need_name(n)",
                catalog));

        string fullMessage = ex.Message + (ex.InnerException?.Message ?? "");
        Assert.Contains("name", fullMessage);
        Assert.Contains("must not be null", fullMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecProc_ArgsEvaluateInCallerScope()
    {
        // The argument expression is evaluated against the caller's
        // ExecutionContext, so it can reference the caller's vars.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE noop(v INT64) AS BEGIN " +
            "  DECLARE x INT64 = v " +
            "END");

        BatchResult result = await RunBatchAsync(
            "DECLARE input INT64 = 42; " +
            "CALL noop(input + 8)",
            catalog);

        Assert.Equal(42L, Convert.ToInt64(result.FinalBindings["input"]));
    }

    // ───────────────────── Recursion guard ─────────────────────

    [Fact]
    public async Task ExecProc_DirectRecursion_ThrowsAfterCap()
    {
        // Self-recursive procedure: each call opens a new ExecutionContext at
        // depth+1; the cap fires before the .NET call stack overflows.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE recurse() AS BEGIN " +
            "  CALL recurse() " +
            "END");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunBatchAsync("CALL recurse()", catalog));
        Assert.Contains("call depth", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recurse", ex.Message);
    }

    [Fact]
    public async Task ExecProc_MutualRecursion_ThrowsAfterCap()
    {
        // A → B → A → B → … cap-doesn't-care which routine triggers it.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE PROCEDURE a() AS BEGIN CALL b() END");
        catalog.Plan("CREATE PROCEDURE b() AS BEGIN CALL a() END");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunBatchAsync("CALL a()", catalog));
        Assert.Contains("call depth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecProc_NonRecursiveNesting_BelowCap_RunsFine()
    {
        // Three procedures chained linearly — well under the cap. Final
        // depth is 3 inside `c`; rolls back to 0 when the batch ends.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE PROCEDURE c() AS BEGIN DECLARE inside INT64 = 99 END");
        catalog.Plan("CREATE PROCEDURE b() AS BEGIN CALL c() END");
        catalog.Plan("CREATE PROCEDURE a() AS BEGIN CALL b() END");

        BatchResult result = await RunBatchAsync(
            "DECLARE x INT64 = 1; CALL a()",
            catalog);

        // Caller's variable should still be bound — proves the chain
        // returned cleanly and didn't trip any guard.
        Assert.Equal(1L, Convert.ToInt64(result.FinalBindings["x"]));
    }

    // ───────────────────── Nested DDL rejection ─────────────────────

    [Fact]
    public void CreateProcedure_NestedCreateFunction_Throws()
    {
        TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan(
                "CREATE PROCEDURE outer_proc() AS BEGIN " +
                "  CREATE FUNCTION inner_fn(x INT32) AS x + 1 " +
                "END"));
        Assert.Contains("CREATE FUNCTION", ex.Message);
        Assert.Contains("inner_fn", ex.Message);
    }

    [Fact]
    public void CreateProcedure_NestedCreateProcedure_Throws()
    {
        TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan(
                "CREATE PROCEDURE outer_proc() AS BEGIN " +
                "  CREATE PROCEDURE inner_proc() AS BEGIN SELECT 1 END " +
                "END"));
        Assert.Contains("CREATE PROCEDURE", ex.Message);
        Assert.Contains("inner_proc", ex.Message);
    }

    [Fact]
    public void CreateProcedure_NestedDropFunction_Throws()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE FUNCTION victim(x INT32) AS x");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan(
                "CREATE PROCEDURE outer_proc() AS BEGIN " +
                "  DROP FUNCTION victim " +
                "END"));
        Assert.Contains("DROP FUNCTION", ex.Message);
        Assert.Contains("victim", ex.Message);
    }

    [Fact]
    public void CreateProcedure_NestedDropProcedure_Throws()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE PROCEDURE victim() AS BEGIN SELECT 1 END");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan(
                "CREATE PROCEDURE outer_proc() AS BEGIN " +
                "  DROP PROCEDURE victim " +
                "END"));
        Assert.Contains("DROP PROCEDURE", ex.Message);
        Assert.Contains("victim", ex.Message);
    }

    [Fact]
    public void CreateProcedure_NestedDdlInsideDeepBlock_StillThrows()
    {
        // Validation walks nested control-flow — DDL hidden inside an IF
        // inside a WHILE must still surface at registration time.
        TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan(
                "CREATE PROCEDURE outer_proc() AS BEGIN " +
                "  DECLARE i INT32 = 0 " +
                "  WHILE i < 1 BEGIN " +
                "    IF i = 0 " +
                "      DROP PROCEDURE nonexistent " +
                "    SET i = i + 1 " +
                "  END " +
                "END"));
        Assert.Contains("DROP PROCEDURE", ex.Message);
    }

    // ───────────────────── Default parameters ─────────────────────

    [Fact]
    public void CreateProcedure_NonContiguousDefaults_Rejected()
    {
        TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan(
                "CREATE PROCEDURE foo(a INT32, b INT32 = 0, c INT32) AS BEGIN SELECT 1 END"));
        Assert.Contains("contiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("c", ex.Message);
    }

    [Fact]
    public async Task ExecProc_OmitTrailingArg_FallsBackToDefault()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE add_default(a INT64, b INT64 = 100) AS BEGIN " +
            "  DECLARE sum INT64 = a + b " +
            "END");

        // Caller provides only a → b takes its default.
        // Procedure's sum can't escape, so verify via a caller-side var
        // that the proc completed without an arity error.
        BatchResult result = await RunBatchAsync(
            "DECLARE ok BOOLEAN = FALSE; " +
            "CALL add_default(7); " +
            "SET ok = TRUE",
            catalog);

        Assert.Equal(true, result.FinalBindings["ok"]);
    }

    [Fact]
    public async Task ExecProc_DefaultEvaluatedInCallerScope()
    {
        // Default expressions can reference caller vars — they evaluate
        // in the same scope as user-supplied arguments.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE record(n INT64 = 0) AS BEGIN " +
            "  DECLARE captured INT64 = n " +
            "END");

        BatchResult result = await RunBatchAsync(
            "DECLARE done BOOLEAN = FALSE; " +
            "CALL record(); " +    // omit → default = 0
            "CALL record(42); " +   // explicit
            "SET done = TRUE",
            catalog);

        Assert.Equal(true, result.FinalBindings["done"]);
    }

    [Fact]
    public async Task ExecProc_TooFewArgs_BelowMinimum_Throws()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE need_one(a INT64, b INT64 = 0) AS BEGIN SELECT a END");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunBatchAsync("CALL need_one()", catalog));
        Assert.Contains("need_one", ex.Message);
    }

    [Fact]
    public async Task ExecProc_TooManyArgs_AboveMaximum_Throws()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE one_or_two(a INT64, b INT64 = 0) AS BEGIN SELECT a END");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunBatchAsync("CALL one_or_two(1, 2, 3)", catalog));
        Assert.Contains("one_or_two", ex.Message);
    }

    [Fact]
    public async Task ExecProc_DefaultViolatesIsNotNull_Throws()
    {
        // Default of NULL on an IS NOT NULL parameter — the assertion
        // should still fire when the caller omits the argument.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE need_one(a INT64 IS NOT NULL = NULL) AS BEGIN SELECT a END");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunBatchAsync("CALL need_one()", catalog));
        Assert.Contains("must not be null", ex.Message);
        Assert.Contains("a", ex.Message);
    }
}

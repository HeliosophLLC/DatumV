using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// End-to-end tests for procedure DDL and invocation: registration through
/// <see cref="TableCatalog.Plan(string)"/>, side effects on the
/// <see cref="ProcedureRegistry"/>, and full-batch execution that exercises
/// <c>EXEC proc.X(...)</c> through the procedural batch executor.
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
    public void CreateProcedure_BodyReferencingUnknownUdf_Throws()
    {
        // Body validation walks every statement and inlines the expressions
        // against the current UDF registry — an unresolved udf.X surfaces
        // at CREATE PROCEDURE time, not at the first EXEC.
        TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan(
                "CREATE PROCEDURE foo() AS BEGIN SELECT udf.never_defined(1) END"));
        Assert.Contains("never_defined", ex.Message);
    }

    // ───────────────────── Invocation ─────────────────────

    [Fact]
    public async Task ExecProc_NoArgs_RunsBody_BindsVariables()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE setone() AS BEGIN " +
            "  DECLARE @x INT32 = 7 " +
            "END");

        // The procedure's @x lives in its own BatchContext and is gone after
        // the procedure ends — so we can't observe it via FinalBindings.
        // We assert that the call doesn't throw and the procedure is
        // registered & callable.
        BatchResult result = await RunBatchAsync(
            "EXEC proc.setone()",
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
            "CREATE PROCEDURE assign_outer(@v INT64) AS BEGIN " +
            "  DECLARE @local INT64 = @v + 100 " +
            "END");

        BatchResult result = await RunBatchAsync(
            "DECLARE @answer INT64 = 0; " +
            "EXEC proc.assign_outer(5)",
            catalog);

        // @answer untouched by the procedure (procedure has its own scope).
        Assert.Equal(0L, Convert.ToInt64(result.FinalBindings["answer"]));
    }

    [Fact]
    public async Task ExecProc_ProcedureSeeMutationFromCaller_ViaArgsOnly()
    {
        // Args evaluate in caller scope — the procedure receives the
        // computed value of @counter, not a reference. Mutations inside
        // the procedure cannot leak out.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE shadow(@v INT64) AS BEGIN " +
            "  SET @v = 999 " +
            "END");

        BatchResult result = await RunBatchAsync(
            "DECLARE @counter INT64 = 5; " +
            "EXEC proc.shadow(@counter)",
            catalog);

        // @counter is 5 still — the procedure's SET on its local @v
        // doesn't touch the caller's @counter.
        Assert.Equal(5L, Convert.ToInt64(result.FinalBindings["counter"]));
    }

    [Fact]
    public async Task ExecProc_ArgArityMismatch_Throws()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE need_two(@a INT32, @b INT32) AS BEGIN " +
            "  DECLARE @sum INT32 = @a + @b " +
            "END");

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => RunBatchAsync("EXEC proc.need_two(1)", catalog));
    }

    [Fact]
    public async Task ExecProc_Unregistered_Throws()
    {
        TableCatalog catalog = CreateCatalog();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => RunBatchAsync("EXEC proc.never_registered()", catalog));
    }

    [Fact]
    public async Task ExecProc_NotNullParam_NullArg_Throws()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE need_name(@name STRING IS NOT NULL) AS BEGIN " +
            "  SELECT @name " +
            "END");

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(
            () => RunBatchAsync(
                "DECLARE @n STRING = NULL; EXEC proc.need_name(@n)",
                catalog));

        string fullMessage = ex.Message + (ex.InnerException?.Message ?? "");
        Assert.Contains("@name", fullMessage);
        Assert.Contains("must not be null", fullMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecProc_ArgsEvaluateInCallerScope()
    {
        // The argument expression is evaluated against the caller's
        // BatchContext, so it can reference the caller's @vars.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE PROCEDURE noop(@v INT64) AS BEGIN " +
            "  DECLARE @x INT64 = @v " +
            "END");

        BatchResult result = await RunBatchAsync(
            "DECLARE @input INT64 = 42; " +
            "EXEC proc.noop(@input + 8)",
            catalog);

        Assert.Equal(42L, Convert.ToInt64(result.FinalBindings["input"]));
    }
}

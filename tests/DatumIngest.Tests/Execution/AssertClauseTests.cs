using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Tests for the ASSERT clause — a post-projection row validator that can abort,
/// skip, or warn on predicate failures. Covers parsing, planning, and end-to-end
/// execution with all three failure modes.
/// </summary>
public sealed class AssertClauseTests : ServiceTestBase
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    private static readonly string[] OrderColumns = ["id", "amount"];

    private static SelectStatement ParseStatement(string sql)
    {
        SelectQueryExpression query = Assert.IsType<SelectQueryExpression>(SqlParser.Parse(sql));
        return query.Statement;
    }

    // ─────────────── Parsing ───────────────

    /// <summary>
    /// A simple ASSERT with a column predicate parses into the Assertions list.
    /// </summary>
    [Fact]
    public void Parse_SimpleAssert_PopulatesAssertions()
    {
        SelectStatement statement = ParseStatement(
            "SELECT id, amount FROM orders ASSERT amount > 0");

        Assert.NotNull(statement.Assertions);
        Assert.Single(statement.Assertions);

        AssertClause clause = statement.Assertions[0];
        BinaryExpression predicate = Assert.IsType<BinaryExpression>(clause.Predicate);
        Assert.Equal(BinaryOperator.GreaterThan, predicate.Operator);
        Assert.Null(clause.Message);
        Assert.Equal(AssertFailureMode.Abort, clause.FailureMode);
    }

    /// <summary>
    /// ASSERT with MESSAGE captures the message expression.
    /// </summary>
    [Fact]
    public void Parse_AssertWithMessage_CapturesMessage()
    {
        SelectStatement statement = ParseStatement(
            "SELECT id, amount FROM orders ASSERT amount > 0 MESSAGE 'amount must be positive'");

        Assert.NotNull(statement.Assertions);
        AssertClause clause = statement.Assertions[0];
        Assert.NotNull(clause.Message);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(clause.Message);
        Assert.Equal("amount must be positive", literal.Value);
    }

    /// <summary>
    /// ASSERT ON FAIL SKIP sets FailureMode to Skip.
    /// </summary>
    [Fact]
    public void Parse_AssertOnFailSkip_SetsSkipMode()
    {
        SelectStatement statement = ParseStatement(
            "SELECT id FROM t ASSERT id IS NOT NULL ON FAIL SKIP");

        Assert.NotNull(statement.Assertions);
        Assert.Equal(AssertFailureMode.Skip, statement.Assertions[0].FailureMode);
    }

    /// <summary>
    /// ASSERT ON FAIL WARN sets FailureMode to Warn.
    /// </summary>
    [Fact]
    public void Parse_AssertOnFailWarn_SetsWarnMode()
    {
        SelectStatement statement = ParseStatement(
            "SELECT id FROM t ASSERT id IS NOT NULL ON FAIL WARN");

        Assert.NotNull(statement.Assertions);
        Assert.Equal(AssertFailureMode.Warn, statement.Assertions[0].FailureMode);
    }

    /// <summary>
    /// ASSERT ON FAIL ABORT sets FailureMode to Abort (explicit form).
    /// </summary>
    [Fact]
    public void Parse_AssertOnFailAbort_SetsAbortMode()
    {
        SelectStatement statement = ParseStatement(
            "SELECT id FROM t ASSERT id IS NOT NULL ON FAIL ABORT");

        Assert.NotNull(statement.Assertions);
        Assert.Equal(AssertFailureMode.Abort, statement.Assertions[0].FailureMode);
    }

    /// <summary>
    /// An inline string literal after the mode keyword is parsed as the message.
    /// </summary>
    [Fact]
    public void Parse_AssertOnFailAbort_WithInlineMessage_SetsMessage()
    {
        SelectStatement statement = ParseStatement(
            "SELECT id FROM t ASSERT id IS NOT NULL ON FAIL ABORT 'id must not be null'");

        Assert.NotNull(statement.Assertions);
        AssertClause clause = statement.Assertions[0];
        Assert.Equal(AssertFailureMode.Abort, clause.FailureMode);
        Assert.NotNull(clause.Message);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(clause.Message);
        Assert.Equal("id must not be null", literal.Value);
    }

    /// <summary>
    /// An inline string at WARN position is parsed as the message.
    /// </summary>
    [Fact]
    public void Parse_AssertOnFailWarn_WithInlineMessage_SetsMessage()
    {
        SelectStatement statement = ParseStatement(
            "SELECT id FROM t ASSERT id IS NOT NULL ON FAIL WARN 'id was null'");

        Assert.NotNull(statement.Assertions);
        AssertClause clause = statement.Assertions[0];
        Assert.Equal(AssertFailureMode.Warn, clause.FailureMode);
        Assert.NotNull(clause.Message);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(clause.Message);
        Assert.Equal("id was null", literal.Value);
    }

    /// <summary>
    /// MESSAGE keyword takes precedence over an inline string after the mode keyword.
    /// </summary>
    [Fact]
    public void Parse_AssertMessageKeyword_TakesPrecedenceOverInlineMessage()
    {
        SelectStatement statement = ParseStatement(
            "SELECT id FROM t ASSERT id IS NOT NULL MESSAGE 'keyword msg' ON FAIL ABORT 'inline msg'");

        Assert.NotNull(statement.Assertions);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(statement.Assertions[0].Message);
        Assert.Equal("keyword msg", literal.Value);
    }

    /// <summary>
    /// Multiple ASSERT clauses in sequence are all captured.
    /// </summary>
    [Fact]
    public void Parse_MultipleAsserts_CapturesAll()
    {
        SelectStatement statement = ParseStatement(
            "SELECT id, amount, name FROM t ASSERT amount > 0 ASSERT name IS NOT NULL");

        Assert.NotNull(statement.Assertions);
        Assert.Equal(2, statement.Assertions.Count);
    }

    /// <summary>
    /// ASSERT coexists with WHERE, QUALIFY, ORDER BY, and LIMIT.
    /// </summary>
    [Fact]
    public void Parse_AssertWithAllClauses_ParsesCorrectly()
    {
        SelectStatement statement = ParseStatement(
            "SELECT *, ROW_NUMBER() OVER (ORDER BY id) AS rn FROM t " +
            "WHERE id > 0 " +
            "QUALIFY rn <= 10 " +
            "ASSERT id IS NOT NULL MESSAGE 'null id' ON FAIL SKIP " +
            "ORDER BY id " +
            "LIMIT 5");

        Assert.NotNull(statement.Where);
        Assert.NotNull(statement.Qualify);
        Assert.NotNull(statement.Assertions);
        Assert.Single(statement.Assertions);
        Assert.NotNull(statement.OrderBy);
        Assert.Equal(5, Convert.ToInt32(((LiteralExpression)statement.Limit!).Value));
    }

    /// <summary>
    /// A SELECT without ASSERT produces a null Assertions field.
    /// </summary>
    [Fact]
    public void Parse_WithoutAssert_ReturnsNullAssertions()
    {
        SelectStatement statement = ParseStatement("SELECT * FROM t");

        Assert.Null(statement.Assertions);
    }

    // ─────────────── Execution ───────────────

    /// <summary>
    /// When all rows pass the assertion, the output is unchanged.
    /// </summary>
    [Fact]
    public async Task Execute_AllRowsPass_ReturnsAllRows()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: OrderColumns,
            [1, 10],
            [2, 20]);

        List<Row> result = await ExecuteQueryAsync(
            "SELECT id, amount FROM orders ASSERT amount > 0", catalog);

        Assert.Equal(2, result.Count);
    }

    /// <summary>
    /// ASSERT with ON FAIL ABORT throws <see cref="AssertionAbortException"/>
    /// when the predicate fails for any row.
    /// </summary>
    [Fact]
    public async Task Execute_AbortMode_ThrowsOnFailure()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: OrderColumns,
            [1, 10],
            [2, -5]);

        await Assert.ThrowsAsync<AssertionAbortException>(() =>
            ExecuteQueryAsync("SELECT id, amount FROM orders ASSERT amount > 0", catalog));
    }

    /// <summary>
    /// ASSERT ON FAIL SKIP discards failing rows from the output.
    /// </summary>
    [Fact]
    public async Task Execute_SkipMode_DiscardsFailingRows()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: OrderColumns,
            [1, 10],
            [2, -5],
            [3, 20]);
        AssertionDiagnostics diagnostics = new();

        List<Row> result = await ExecuteQueryAsync(
            "SELECT id, amount FROM orders ASSERT amount > 0 ON FAIL SKIP", catalog, diagnostics);

        Assert.Equal(2, result.Count);
        Assert.Equal(1L, diagnostics.SkippedRowCount);
        Assert.Equal(0L, diagnostics.WarnedRowCount);
    }

    /// <summary>
    /// ASSERT ON FAIL WARN keeps all rows but records the failed count on diagnostics.
    /// </summary>
    [Fact]
    public async Task Execute_WarnMode_KeepsAllRowsAndRecordsDiagnostics()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: OrderColumns,
            [1, 10],
            [2, -5]);
        AssertionDiagnostics diagnostics = new();

        List<Row> result = await ExecuteQueryAsync(
            "SELECT id, amount FROM orders ASSERT amount > 0 ON FAIL WARN", catalog, diagnostics);

        Assert.Equal(2, result.Count);
        Assert.Equal(0L, diagnostics.SkippedRowCount);
        Assert.Equal(1L, diagnostics.WarnedRowCount);
    }

    /// <summary>
    /// MESSAGE expression is evaluated and passed to diagnostics on failure.
    /// </summary>
    [Fact]
    public async Task Execute_SkipWithMessage_RecordsSampleMessage()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: OrderColumns,
            [1, -5]);
        AssertionDiagnostics diagnostics = new();

        await ExecuteQueryAsync(
            "SELECT id, amount FROM orders ASSERT amount > 0 MESSAGE 'bad amount' ON FAIL SKIP",
            catalog, diagnostics);

        Assert.Single(diagnostics.SampleMessages);
        Assert.Equal("bad amount", diagnostics.SampleMessages[0]);
    }

    /// <summary>
    /// ASSERT ON FAIL WARN records diagnostics when the predicate references a LET binding.
    /// </summary>
    [Fact]
    public async Task Execute_WarnMode_LetBinding_RecordsDiagnostics()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: OrderColumns,
            [1, 10],
            [2, -5],
            [3, 20]);
        AssertionDiagnostics diagnostics = new();

        List<Row> result = await ExecuteQueryAsync(
            "SELECT LET a = amount, id, amount FROM orders ASSERT a > 0 ON FAIL WARN",
            catalog, diagnostics);

        // All 3 rows pass through (WARN keeps rows), but 1 row records a diagnostic.
        Assert.Equal(3, result.Count);
        Assert.Equal(1L, diagnostics.WarnedRowCount);
        Assert.Equal(0L, diagnostics.SkippedRowCount);
    }

    /// <summary>
    /// ASSERT ON FAIL WARN records diagnostics when the predicate references a DEFINE-block
    /// LET binding. Mirrors <see cref="Execute_WarnMode_LetBinding_RecordsDiagnostics"/> but
    /// exercises the <c>DEFINE { let … }</c> syntax instead of the inline <c>LET</c> form.
    /// </summary>
    [Fact]
    public async Task Execute_WarnMode_DefineBlockLet_RecordsDiagnostics()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: OrderColumns,
            [1, 10],
            [2, -5],
            [3, 20]);
        AssertionDiagnostics diagnostics = new();

        List<Row> result = await ExecuteQueryAsync(
            "SELECT DEFINE { let a = amount } id, amount FROM orders ASSERT a > 0 ON FAIL WARN",
            catalog, diagnostics);

        // All 3 rows pass through (WARN keeps rows), but 1 row records a diagnostic.
        Assert.Equal(3, result.Count);
        Assert.Equal(1L, diagnostics.WarnedRowCount);
        Assert.Equal(0L, diagnostics.SkippedRowCount);
    }

    /// <summary>
    /// ASSERT ON FAIL WARN fires for a boolean column via a DEFINE-block LET alias.
    /// Mirrors the user's real-world pattern: <c>DEFINE { let x = reordered } … ASSERT x = FALSE</c>.
    /// </summary>
    [Fact]
    public async Task Execute_WarnMode_DefineBlockLet_BooleanColumn_RecordsDiagnostics()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["order_id", "reordered"],
            [1, false],
            [2, true]);
        AssertionDiagnostics diagnostics = new();

        List<Row> result = await ExecuteQueryAsync(
            "SELECT DEFINE { let x = reordered } order_id, reordered FROM orders ASSERT x = FALSE ON FAIL WARN",
            catalog, diagnostics);

        // Both rows pass through; row 2 (reordered = true) fails ASSERT x = FALSE → WARN.
        Assert.Equal(2, result.Count);
        Assert.Equal(1L, diagnostics.WarnedRowCount);
        Assert.Equal(0L, diagnostics.SkippedRowCount);
    }

    /// <summary>
    /// ASSERT ON FAIL WARN fires for a boolean column via a DEFINE-block LET alias when
    /// the plan is built via <see cref="QueryPlanner.PlanWithSubqueriesAsync"/> — the exact
    /// code path used by <c>CommandDispatcher</c> for every interactive REPL query.
    /// Regression test: ensures the CLI code path (async planner) does not silently drop assertions.
    /// </summary>
    [Fact]
    public async Task Execute_WarnMode_DefineBlockLet_ColumnarProvider_CliPath_RecordsDiagnostics()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["order_id", "product_id", "reordered"],
            [1, 10, false],
            [2, 20, true],
            [3, 30, true]);
        AssertionDiagnostics diagnostics = new();

        // Replicate the exact CLI execution path: PlanWithSubqueriesAsync + shared ExecutionContext.
        QueryExpression query = SqlParser.Parse(
            "SELECT DEFINE { let x = reordered } order_id, product_id, reordered FROM orders ASSERT x = FALSE MESSAGE 'test' ON FAIL WARN");
        QueryPlanner planner = new(catalog, DefaultFunctions);
        ExecutionContext context = CreateExecutionContext(catalog: catalog, diagnostics: diagnostics);
        QueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);

        List<Row> result = await plan.CollectRowsAsync(context);

        // All 3 rows pass through (WARN keeps rows); rows 2 and 3 (reordered = true) WARN.
        Assert.Equal(3, result.Count);
        Assert.Equal(2L, diagnostics.WarnedRowCount);
        Assert.Equal(0L, diagnostics.SkippedRowCount);
    }

    /// <summary>
    /// ASSERT can reference a LET binding by name.
    /// </summary>
    [Fact]
    public async Task Execute_AssertReferencesLetBinding_EvaluatesCorrectly()
    {
        TableCatalog catalog = CreateCatalog("sales",
            columns: ["price", "qty"],
            [100, 2],
            [0, 5]);
        AssertionDiagnostics diagnostics = new();

        // total = price * qty; ASSERT total > 0 should skip the row where price = 0.
        List<Row> result = await ExecuteQueryAsync(
            "SELECT LET total = price * qty, price, qty, total FROM sales ASSERT total > 0 ON FAIL SKIP",
            catalog, diagnostics);

        Assert.Single(result);
        Assert.Equal(1L, diagnostics.SkippedRowCount);
    }

    /// <summary>
    /// Multiple ASSERT clauses: a row that fails any clause is handled by that clause's mode.
    /// </summary>
    [Fact]
    public async Task Execute_MultipleAsserts_EachEnforcedIndependently()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: OrderColumns,
            [1, 10],
            [2, -5],
            [null, 50]);
        AssertionDiagnostics diagnostics = new();

        // First assertion: id IS NOT NULL (SKIP mode)
        // Second assertion: amount > 0 (WARN mode)
        List<Row> result = await ExecuteQueryAsync(
            "SELECT id, amount FROM t " +
            "ASSERT id IS NOT NULL ON FAIL SKIP " +
            "ASSERT amount > 0 ON FAIL WARN",
            catalog, diagnostics);

        // Row 2 (amount = -5) passes id check but fails amount → WARN, stays in output.
        // Row 3 (id = null) fails id check → SKIP, removed from output.
        Assert.Equal(2, result.Count);
        Assert.Equal(1L, diagnostics.SkippedRowCount);
        Assert.Equal(1L, diagnostics.WarnedRowCount);
    }

    /// <summary>
    /// ASSERT ON FAIL ABORT fires for queries built against an in-memory provider.
    /// Regression test: the former columnar fast path used to silently drop ASSERT
    /// clauses; this test keeps that coverage in place even though the fast path
    /// has been removed.
    /// </summary>
    [Fact]
    public async Task Execute_AbortMode_ColumnarProvider_Fires()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: OrderColumns,
            [1, 10],
            [2, -5]);

        await Assert.ThrowsAsync<AssertionAbortException>(
            () => ExecuteQueryAsync(
                "SELECT id, amount FROM orders ASSERT amount > 0 ON FAIL ABORT",
                catalog));
    }

    /// <summary>    /// Inline message after ON FAIL ABORT is carried by <see cref="AssertionAbortException"/>.
    /// </summary>
    [Fact]
    public async Task Execute_AbortMode_InlineMessage_CarriedByException()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: OrderColumns,
            [1, -5]);

        AssertionAbortException exception = await Assert.ThrowsAsync<AssertionAbortException>(
            () => ExecuteQueryAsync(
                "SELECT id, amount FROM orders ASSERT amount > 0 ON FAIL ABORT 'amount must be positive'",
                catalog));

        Assert.Equal("amount must be positive", exception.Message);
    }

    /// <summary>
    /// Regression for the ProjectOperator try-finally refactor: an ASSERT … ON FAIL
    /// ABORT that trips mid-batch must propagate <see cref="AssertionAbortException"/>
    /// without leaking or double-returning the in-flight outputBatch. Prior to the
    /// refactor a per-row catch block performed the cleanup and rethrew; the new
    /// code relies on an outer finally instead. This test exercises the path with
    /// enough rows preceding the failing one that outputBatch has definitely
    /// accumulated entries when the exception unwinds, then issues a follow-up query
    /// against the same DI-resolved <see cref="Heliosoph.DatumV.Pooling.Pool"/> — a
    /// double-return would corrupt the pool's per-length DataValue[] queues and the
    /// follow-up would either throw <see cref="ObjectDisposedException"/> or yield
    /// stale data.
    /// </summary>
    [Fact]
    public async Task Execute_AbortMode_TripsMidBatch_DoesNotLeakOutputBatch()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: OrderColumns,
            [1, 10],
            [2, 20],
            [3, 30],
            [4, 40],
            [5, -5]); // assertion trips here, after 4 successful projections

        await Assert.ThrowsAsync<AssertionAbortException>(
            () => ExecuteQueryAsync(
                "SELECT id, amount FROM orders ASSERT amount > 0 ON FAIL ABORT",
                catalog));

        // Follow-up query against a fresh table but the same Pool. If the previous
        // query's outputBatch was double-returned, this query's RentRowBatch /
        // RentDataValues calls would fish a disposed buffer out of the pool and
        // either throw or produce wrong results.
        TableCatalog followUp = CreateCatalog("orders2",
            columns: OrderColumns,
            [10, 100],
            [11, 200]);

        List<Row> result = await ExecuteQueryAsync(
            "SELECT id, amount FROM orders2", followUp);

        Assert.Equal(2, result.Count);
        Assert.Equal(10, result[0]["id"].AsInt32());
        Assert.Equal(100, result[0]["amount"].AsInt32());
        Assert.Equal(11, result[1]["id"].AsInt32());
        Assert.Equal(200, result[1]["amount"].AsInt32());
    }

    /// <summary>
    /// ASSERT ON FAIL SKIP fires for queries built against an in-memory provider.
    /// </summary>
    [Fact]
    public async Task Execute_SkipMode_ColumnarProvider_Fires()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: OrderColumns,
            [1, 10],
            [2, -5],
            [3, 20]);
        AssertionDiagnostics diagnostics = new();

        List<Row> result = await ExecuteQueryAsync(
            "SELECT id, amount FROM orders ASSERT amount > 0 ON FAIL SKIP",
            catalog, diagnostics);

        Assert.Equal(2, result.Count);
        Assert.Equal(1L, diagnostics.SkippedRowCount);
    }

}

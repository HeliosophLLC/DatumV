using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for the ASSERT clause — a post-projection row validator that can abort,
/// skip, or warn on predicate failures. Covers parsing, planning, and end-to-end
/// execution with all three failure modes.
/// </summary>
public sealed class AssertClauseTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    private static TableCatalog CreateCatalog(params (string Name, Row[] Rows)[] tables)
    {
        TableCatalog catalog = new();
        foreach ((string name, Row[] rows) in tables)
        {
            InMemoryTableProvider provider = new(rows);
            catalog.RegisterProvider(name, () => provider);
            catalog.Register(new TableDescriptor(name, name, "", new Dictionary<string, string>()));
        }

        return catalog;
    }

    /// <summary>
    /// Creates a catalog whose providers implement <see cref="IColumnBatchProvider"/>,
    /// triggering the columnar fast path in <see cref="QueryPlanner"/>.
    /// </summary>
    private static TableCatalog CreateColumnarCatalog(params (string Name, Row[] Rows)[] tables)
    {
        TableCatalog catalog = new();
        foreach ((string name, Row[] rows) in tables)
        {
            InMemoryColumnarProvider provider = new(rows);
            catalog.RegisterProvider(name, () => provider);
            catalog.Register(new TableDescriptor(name, name, "", new Dictionary<string, string>()));
        }

        return catalog;
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    private static async Task<List<Row>> ExecuteQueryAsync(
        string sql, TableCatalog catalog, AssertionDiagnostics? diagnostics = null)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, DefaultFunctions);
        ExecutionContext context = new(
            CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool())
        {
            AssertionDiagnostics = diagnostics,
        };
        IQueryOperator plan = planner.Plan(query);

        return await plan.CollectRowsAsync(context);
    }

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
        Assert.Equal(5, statement.Limit);
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
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromInt32(1)), ("amount", DataValue.FromInt32(10))),
            MakeRow(("id", DataValue.FromInt32(2)), ("amount", DataValue.FromInt32(20))),
        ];
        TableCatalog catalog = CreateCatalog(("orders", rows));

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
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromInt32(1)), ("amount", DataValue.FromInt32(10))),
            MakeRow(("id", DataValue.FromInt32(2)), ("amount", DataValue.FromInt32(-5))),
        ];
        TableCatalog catalog = CreateCatalog(("orders", rows));

        await Assert.ThrowsAsync<AssertionAbortException>(() =>
            ExecuteQueryAsync("SELECT id, amount FROM orders ASSERT amount > 0", catalog));
    }

    /// <summary>
    /// ASSERT ON FAIL SKIP discards failing rows from the output.
    /// </summary>
    [Fact]
    public async Task Execute_SkipMode_DiscardsFailingRows()
    {
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromInt32(1)), ("amount", DataValue.FromInt32(10))),
            MakeRow(("id", DataValue.FromInt32(2)), ("amount", DataValue.FromInt32(-5))),
            MakeRow(("id", DataValue.FromInt32(3)), ("amount", DataValue.FromInt32(20))),
        ];
        TableCatalog catalog = CreateCatalog(("orders", rows));
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
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromInt32(1)), ("amount", DataValue.FromInt32(10))),
            MakeRow(("id", DataValue.FromInt32(2)), ("amount", DataValue.FromInt32(-5))),
        ];
        TableCatalog catalog = CreateCatalog(("orders", rows));
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
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromInt32(1)), ("amount", DataValue.FromInt32(-5))),
        ];
        TableCatalog catalog = CreateCatalog(("orders", rows));
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
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromInt32(1)), ("amount", DataValue.FromInt32(10))),
            MakeRow(("id", DataValue.FromInt32(2)), ("amount", DataValue.FromInt32(-5))),
            MakeRow(("id", DataValue.FromInt32(3)), ("amount", DataValue.FromInt32(20))),
        ];
        TableCatalog catalog = CreateCatalog(("orders", rows));
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
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromInt32(1)), ("amount", DataValue.FromInt32(10))),
            MakeRow(("id", DataValue.FromInt32(2)), ("amount", DataValue.FromInt32(-5))),
            MakeRow(("id", DataValue.FromInt32(3)), ("amount", DataValue.FromInt32(20))),
        ];
        TableCatalog catalog = CreateCatalog(("orders", rows));
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
        Row[] rows =
        [
            MakeRow(("order_id", DataValue.FromInt32(1)), ("reordered", DataValue.FromBoolean(false))),
            MakeRow(("order_id", DataValue.FromInt32(2)), ("reordered", DataValue.FromBoolean(true))),
        ];
        TableCatalog catalog = CreateCatalog(("orders", rows));
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
    /// the provider implements <see cref="IColumnBatchProvider"/> and the plan is built
    /// via <see cref="QueryPlanner.PlanWithSubqueriesAsync"/> — the exact code path used
    /// by <c>CommandDispatcher</c> for every interactive REPL query.
    /// Regression test: ensures the CLI code path (columnar scan + async planner) does
    /// not silently drop assertions.
    /// </summary>
    [Fact]
    public async Task Execute_WarnMode_DefineBlockLet_ColumnarProvider_CliPath_RecordsDiagnostics()
    {
        Row[] rows =
        [
            MakeRow(("order_id", DataValue.FromInt32(1)), ("product_id", DataValue.FromInt32(10)), ("reordered", DataValue.FromBoolean(false))),
            MakeRow(("order_id", DataValue.FromInt32(2)), ("product_id", DataValue.FromInt32(20)), ("reordered", DataValue.FromBoolean(true))),
            MakeRow(("order_id", DataValue.FromInt32(3)), ("product_id", DataValue.FromInt32(30)), ("reordered", DataValue.FromBoolean(true))),
        ];
        TableCatalog catalog = CreateColumnarCatalog(("orders", rows));
        AssertionDiagnostics diagnostics = new();

        // Replicate the exact CLI execution path: PlanWithSubqueriesAsync + shared ExecutionContext.
        QueryExpression query = SqlParser.Parse(
            "SELECT DEFINE { let x = reordered } order_id, product_id, reordered FROM orders ASSERT x = FALSE MESSAGE 'test' ON FAIL WARN");
        QueryPlanner planner = new(catalog, DefaultFunctions);
        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool())
        {
            AssertionDiagnostics = diagnostics,
        };
        IQueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);

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
        Row[] rows =
        [
            MakeRow(("price", DataValue.FromInt32(100)), ("qty", DataValue.FromInt32(2))),
            MakeRow(("price", DataValue.FromInt32(0)),   ("qty", DataValue.FromInt32(5))),
        ];
        TableCatalog catalog = CreateCatalog(("sales", rows));
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
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromInt32(1)), ("amount", DataValue.FromInt32(10))),
            MakeRow(("id", DataValue.FromInt32(2)), ("amount", DataValue.FromInt32(-5))),
            MakeRow(("id", DataValue.Null(DataKind.Int32)), ("amount", DataValue.FromInt32(50))),
        ];
        TableCatalog catalog = CreateCatalog(("t", rows));
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
    /// ASSERT ON FAIL ABORT fires when the provider implements
    /// <see cref="IColumnBatchProvider"/> (the columnar fast path).
    /// Regression test: <see cref="QueryPlanner.TryPlanColumnar"/> previously
    /// routed queries with ASSERT into the columnar pipeline, which has no
    /// assertion evaluation, silently ignoring all ASSERT clauses.
    /// </summary>
    [Fact]
    public async Task Execute_AbortMode_ColumnarProvider_Fires()
    {
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromInt32(1)), ("amount", DataValue.FromInt32(10))),
            MakeRow(("id", DataValue.FromInt32(2)), ("amount", DataValue.FromInt32(-5))),
        ];
        TableCatalog catalog = CreateColumnarCatalog(("orders", rows));

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
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromInt32(1)), ("amount", DataValue.FromInt32(-5))),
        ];
        TableCatalog catalog = CreateCatalog((("orders", rows)));

        AssertionAbortException exception = await Assert.ThrowsAsync<AssertionAbortException>(
            () => ExecuteQueryAsync(
                "SELECT id, amount FROM orders ASSERT amount > 0 ON FAIL ABORT 'amount must be positive'",
                catalog));

        Assert.Equal("amount must be positive", exception.Message);
    }

    /// <summary>    /// ASSERT ON FAIL SKIP fires when the provider implements
    /// <see cref="IColumnBatchProvider"/> (the columnar fast path).
    /// </summary>
    [Fact]
    public async Task Execute_SkipMode_ColumnarProvider_Fires()
    {
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromInt32(1)), ("amount", DataValue.FromInt32(10))),
            MakeRow(("id", DataValue.FromInt32(2)), ("amount", DataValue.FromInt32(-5))),
            MakeRow(("id", DataValue.FromInt32(3)), ("amount", DataValue.FromInt32(20))),
        ];
        TableCatalog catalog = CreateColumnarCatalog(("orders", rows));
        AssertionDiagnostics diagnostics = new();

        List<Row> result = await ExecuteQueryAsync(
            "SELECT id, amount FROM orders ASSERT amount > 0 ON FAIL SKIP",
            catalog, diagnostics);

        Assert.Equal(2, result.Count);
        Assert.Equal(1L, diagnostics.SkippedRowCount);
    }

    // ───────────────── Helpers ─────────────────

    /// <summary>
    /// In-memory table provider that also implements <see cref="IColumnBatchProvider"/>,
    /// causing the <see cref="QueryPlanner"/> to route simple queries through the
    /// columnar fast path. Used to verify that ASSERT clauses are not silently skipped
    /// when the columnar pipeline is active.
    /// </summary>
    private sealed class InMemoryColumnarProvider : ITableProvider, IColumnBatchProvider
    {
        private readonly Row[] _rows;

        public InMemoryColumnarProvider(Row[] rows)
        {
            _rows = rows;
        }

        /// <inheritdoc/>
        public Task<Schema> GetSchemaAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            if (_rows.Length == 0)
            {
                return Task.FromResult(
                    new Schema([new ColumnInfo("empty", DataKind.String, nullable: true)]));
            }

            List<ColumnInfo> columns = [];
            foreach (string name in _rows[0].ColumnNames)
            {
                columns.Add(new ColumnInfo(name, _rows[0][name].Kind, nullable: true));
            }

            return Task.FromResult(new Schema(columns));
        }

        public long GetRowCount(TableDescriptor descriptor)
        {
            return _rows.Length;
        }

        /// <inheritdoc/>
        public async IAsyncEnumerable<RowBatch> OpenAsync(
            TableDescriptor descriptor,
            IReadOnlySet<string>? requiredColumns,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RowBatch batch = RowBatch.Rent(64);

            foreach (Row row in _rows)
            {
                batch.Add(row);

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = RowBatch.Rent(64);
                }
            }

            if (batch.Count > 0)
                yield return batch;

            await Task.CompletedTask;
        }

        /// <inheritdoc/>
        public async IAsyncEnumerable<ColumnBatch> OpenColumnBatchAsync(
            TableDescriptor descriptor,
            IReadOnlySet<string>? requiredColumns,
            Expression? filterHint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_rows.Length == 0) yield break;

            string[] allNames = _rows[0].ColumnNames.ToArray();
            string[] names = requiredColumns is null
                ? allNames
                : allNames.Where(n => requiredColumns.Contains(n)).ToArray();

            ColumnBatch batch = ColumnBatch.Create(names, _rows.Length);

            for (int rowIndex = 0; rowIndex < _rows.Length; rowIndex++)
            {
                for (int colIndex = 0; colIndex < names.Length; colIndex++)
                {
                    batch.SetValue(colIndex, rowIndex, _rows[rowIndex][names[colIndex]]);
                }
            }

            batch.SetRowCount(_rows.Length);
            yield return batch;

            await Task.CompletedTask;
        }
    }
}

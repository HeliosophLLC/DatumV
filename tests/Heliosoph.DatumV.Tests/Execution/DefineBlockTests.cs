using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Tests for the DEFINE block — syntactic sugar that groups LET bindings and ASSERT
/// clauses inside a brace-delimited block directly after SELECT. Covers parsing
/// equivalence with inline LET/ASSERT, execution correctness, and edge cases.
/// </summary>
public sealed class DefineBlockTests : ServiceTestBase
{
    private static SelectStatement ParseStatement(string sql)
    {
        SelectQueryExpression query = Assert.IsType<SelectQueryExpression>(SqlParser.Parse(sql));
        return query.Statement;
    }

    // ─────────────── Parsing ───────────────

    /// <summary>
    /// A DEFINE block containing a single LET binding flattens into LetBindings.
    /// </summary>
    [Fact]
    public void Parse_SingleLet_PopulatesLetBindings()
    {
        SelectStatement statement = ParseStatement(
            "SELECT DEFINE { LET total = price * qty; } total FROM sales");

        Assert.NotNull(statement.LetBindings);
        Assert.Single(statement.LetBindings);
        Assert.Equal("total", statement.LetBindings[0].Name);
        Assert.Null(statement.Assertions);
    }

    /// <summary>
    /// Multiple LET bindings with semicolon separators all land in LetBindings.
    /// </summary>
    [Fact]
    public void Parse_MultipleLets_AllPopulateLetBindings()
    {
        SelectStatement statement = ParseStatement(
            "SELECT DEFINE { LET a = x + 1; LET b = y * 2; } a, b FROM t");

        Assert.NotNull(statement.LetBindings);
        Assert.Equal(2, statement.LetBindings.Count);
        Assert.Equal("a", statement.LetBindings[0].Name);
        Assert.Equal("b", statement.LetBindings[1].Name);
        Assert.Null(statement.Assertions);
    }

    /// <summary>
    /// A DEFINE block containing an ASSERT flattens into Assertions.
    /// </summary>
    [Fact]
    public void Parse_AssertInDefine_PopulatesAssertions()
    {
        SelectStatement statement = ParseStatement(
            "SELECT DEFINE { ASSERT amount > 0; } id FROM t");

        Assert.Null(statement.LetBindings);
        Assert.NotNull(statement.Assertions);
        Assert.Single(statement.Assertions);
        Assert.Equal(AssertFailureMode.Abort, statement.Assertions[0].FailureMode);
    }

    /// <summary>
    /// Mixed LET and ASSERT declarations both flatten to their respective fields.
    /// </summary>
    [Fact]
    public void Parse_MixedLetAndAssert_BothFieldsPopulated()
    {
        SelectStatement statement = ParseStatement(
            "SELECT DEFINE { LET tax = amount * 0.1; ASSERT amount > 0 ON FAIL SKIP; } amount, tax FROM t");

        Assert.NotNull(statement.LetBindings);
        Assert.Single(statement.LetBindings);
        Assert.Equal("tax", statement.LetBindings[0].Name);

        Assert.NotNull(statement.Assertions);
        Assert.Single(statement.Assertions);
        Assert.Equal(AssertFailureMode.Skip, statement.Assertions[0].FailureMode);
    }

    /// <summary>
    /// Trailing semicolons before the closing brace are optional and do not affect
    /// the number of declarations parsed.
    /// </summary>
    [Fact]
    public void Parse_TrailingSemicolonBeforeClosingBrace_Accepted()
    {
        SelectStatement withTrailing = ParseStatement(
            "SELECT DEFINE { LET x = 1; } x FROM t");
        SelectStatement withoutTrailing = ParseStatement(
            "SELECT DEFINE { LET x = 1 } x FROM t");

        Assert.NotNull(withTrailing.LetBindings);
        Assert.NotNull(withoutTrailing.LetBindings);
        Assert.Single(withTrailing.LetBindings);
        Assert.Single(withoutTrailing.LetBindings);
    }

    /// <summary>
    /// An empty DEFINE block — <c>DEFINE {}</c> — produces no LetBindings and no Assertions.
    /// </summary>
    [Fact]
    public void Parse_EmptyDefineBlock_ProducesNoBindingsOrAssertions()
    {
        SelectStatement statement = ParseStatement(
            "SELECT DEFINE {} id FROM t");

        Assert.Null(statement.LetBindings);
        Assert.Null(statement.Assertions);
    }

    /// <summary>
    /// A DEFINE block ASSERT with MESSAGE and ON FAIL SKIP captures all fields correctly.
    /// </summary>
    [Fact]
    public void Parse_AssertInDefineWithMessageAndMode_CapturesFields()
    {
        SelectStatement statement = ParseStatement(
            "SELECT DEFINE { ASSERT amount > 0 MESSAGE 'must be positive' ON FAIL WARN; } amount FROM t");

        Assert.NotNull(statement.Assertions);
        AssertClause clause = statement.Assertions[0];
        Assert.NotNull(clause.Message);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(clause.Message);
        Assert.Equal("must be positive", literal.Value);
        Assert.Equal(AssertFailureMode.Warn, clause.FailureMode);
    }

    /// <summary>
    /// ASSERT clauses from a DEFINE block and trailing ASSERT clauses after the column list
    /// are both collected into Assertions, with DEFINE-sourced ones appearing first.
    /// </summary>
    [Fact]
    public void Parse_DefineAssertAndTrailingAssert_BothCollected()
    {
        SelectStatement statement = ParseStatement(
            "SELECT DEFINE { ASSERT a > 0; } id FROM t ASSERT b > 0");

        Assert.NotNull(statement.Assertions);
        Assert.Equal(2, statement.Assertions.Count);
    }

    // ─────────────── Execution ───────────────

    /// <summary>
    /// A DEFINE block with a LET binding produces the same output as the equivalent
    /// inline LET in the column list — the bound value is computed and emitted.
    /// </summary>
    [Fact]
    public async Task Execute_DefineLet_EquivalentToInlineLet()
    {
        TableCatalog catalog = CreateCatalog("sales",
            columns: ["price", "qty"],
            [10, 3],
            [5, 4]);

        List<Row> defineRows = await ExecuteQueryAsync(
            "SELECT DEFINE { LET total = price * qty; } total FROM sales",
            catalog);

        List<Row> inlineRows = await ExecuteQueryAsync(
            "SELECT LET total = price * qty, total FROM sales",
            catalog);

        Assert.Equal(inlineRows.Count, defineRows.Count);
        for (int index = 0; index < defineRows.Count; index++)
        {
            Assert.Equal(inlineRows[index]["total"], defineRows[index]["total"]);
        }
    }

    /// <summary>
    /// A DEFINE block ASSERT with ON FAIL ABORT throws when the predicate fails.
    /// </summary>
    [Fact]
    public async Task Execute_DefineAssertAbort_ThrowsOnFailure()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x"],
            [5],
            [-1]);

        await Assert.ThrowsAsync<AssertionAbortException>(() =>
            ExecuteQueryAsync("SELECT DEFINE { ASSERT x > 0; } x FROM t", catalog));
    }

    /// <summary>
    /// A DEFINE block ASSERT with ON FAIL SKIP filters failing rows silently.
    /// </summary>
    [Fact]
    public async Task Execute_DefineAssertSkip_FiltersFailingRows()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x"],
            [1],
            [-2],
            [3]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT DEFINE { ASSERT x > 0 ON FAIL SKIP; } x FROM t",
            catalog);

        Assert.Equal(2, rows.Count);
        Assert.Equal(DataValue.FromInt32(1), rows[0]["x"]);
        Assert.Equal(DataValue.FromInt32(3), rows[1]["x"]);
    }

    /// <summary>
    /// A LET binding declared in DEFINE can be referenced by an ASSERT in the same block.
    /// </summary>
    [Fact]
    public async Task Execute_AssertReferencesLetFromSameDefineBlock_Works()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["price", "qty"],
            [10, 5],
            [10, 0]);

        AssertionDiagnostics diagnostics = new();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT DEFINE { LET total = price * qty; ASSERT total > 0 ON FAIL SKIP; } total FROM t",
            catalog, diagnostics);

        Assert.Single(rows);
        Assert.Equal(1, diagnostics.SkippedRowCount);
    }

    /// <summary>
    /// DEFINE block and trailing ASSERT clause both execute — rows must pass all assertions.
    /// </summary>
    [Fact]
    public async Task Execute_DefineAssertAndTrailingAssert_BothApplied()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["a", "b"],
            [1, 1],
            [-1, 1],
            [1, -1]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT DEFINE { ASSERT a > 0 ON FAIL SKIP; } a, b FROM t ASSERT b > 0 ON FAIL SKIP",
            catalog);

        // Only the first row passes both assertions.
        Assert.Single(rows);
    }

    // ─────────────── Destructuring in DEFINE ───────────────

    /// <summary>
    /// A positional destructuring LET inside a DEFINE block parses to a single LetBinding
    /// with a Destructure pattern carrying all extracted names.
    /// </summary>
    [Fact]
    public void Parse_PositionalDestructuringInDefine_PopulatesLetBindings()
    {
        SelectStatement statement = ParseStatement(
            "SELECT DEFINE { LET (a, b) = pair; } a, b FROM t");

        Assert.NotNull(statement.LetBindings);
        Assert.Single(statement.LetBindings);
        LetBinding binding = statement.LetBindings[0];
        Assert.NotNull(binding.Destructure);
        Assert.Equal(DestructureMode.Positional, binding.Destructure.Mode);
        Assert.Equal(["a", "b"], binding.Destructure.Names);
    }

    /// <summary>
    /// A named destructuring LET inside a DEFINE block parses to a LetBinding with
    /// a Named destructure pattern carrying the field names.
    /// </summary>
    [Fact]
    public void Parse_NamedDestructuringInDefine_PopulatesLetBindings()
    {
        SelectStatement statement = ParseStatement(
            "SELECT DEFINE { LET {x, y} = pair; } x, y FROM t");

        Assert.NotNull(statement.LetBindings);
        Assert.Single(statement.LetBindings);
        LetBinding binding = statement.LetBindings[0];
        Assert.NotNull(binding.Destructure);
        Assert.Equal(DestructureMode.Named, binding.Destructure.Mode);
        Assert.Equal(["x", "y"], binding.Destructure.Names);
    }

    /// <summary>
    /// Positional destructuring inside a DEFINE block correctly unpacks a float array
    /// into named scalar columns accessible in the output.
    /// </summary>
    [Fact]
    public async Task Execute_DefinePositionalDestructuring_ProducesComponents()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["arr"],
            [DataValue.FromInlineArray<float>([1f, 2f], DataKind.Float32)],
            [DataValue.FromInlineArray<float>([3f, 4f], DataKind.Float32)]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT DEFINE { LET (x, y) = arr; } x AS x_out, y AS y_out FROM t",
            catalog);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1f, rows[0]["x_out"].AsFloat32());
        Assert.Equal(2f, rows[0]["y_out"].AsFloat32());
        Assert.Equal(3f, rows[1]["x_out"].AsFloat32());
        Assert.Equal(4f, rows[1]["y_out"].AsFloat32());
    }

    /// <summary>
    /// Named destructuring inside a DEFINE block correctly extracts struct fields
    /// by name into individual output columns.
    /// </summary>
    [Fact]
    public async Task Execute_DefineNamedDestructuring_ProducesNamedFields()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["dummy"],
            [0f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT DEFINE { LET {alpha, beta} = {alpha: 10.0, beta: 20.0}; } alpha AS av, beta AS bv FROM t",
            catalog);

        Assert.Single(rows);
        // Struct literal fields may be narrower integer types after literal narrowing.
        Assert.Equal(10.0, rows[0]["av"].ToDouble(), precision: 4);
        Assert.Equal(20.0, rows[0]["bv"].ToDouble(), precision: 4);
    }

    /// <summary>
    /// An ASSERT inside the same DEFINE block can reference positional destructuring
    /// names — failing rows are skipped correctly.
    /// </summary>
    [Fact]
    public async Task Execute_AssertReferencesPositionalDestructuringNames_Works()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["arr"],
            [DataValue.FromInlineArray<float>([5f, 10f], DataKind.Float32)],
            [DataValue.FromInlineArray<float>([-1f, 10f], DataKind.Float32)]);

        AssertionDiagnostics diagnostics = new();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT DEFINE { LET (x, y) = arr; ASSERT x > 0 ON FAIL SKIP; } x AS x_out, y AS y_out FROM t",
            catalog, diagnostics);

        Assert.Single(rows);
        Assert.Equal(1, diagnostics.SkippedRowCount);
        Assert.Equal(5f, rows[0]["x_out"].AsFloat32());
    }

    /// <summary>
    /// Mixed positional destructuring and a scalar LET inside a DEFINE block
    /// both execute correctly and produce output columns.
    /// </summary>
    [Fact]
    public async Task Execute_DefineMixedDestructuringAndScalarLet_AllColumnsAvailable()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["arr", "scale"],
            [
                DataValue.FromInlineArray<float>([3f, 4f], DataKind.Float32),
                10f,
            ]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT DEFINE { LET (dx, dy) = arr; LET mag = dx * dx + dy * dy; } mag AS m, dx AS x FROM t",
            catalog);

        Assert.Single(rows);
        // dx=3, dy=4 → mag = 9+16 = 25
        Assert.Equal(25f, rows[0]["m"].AsFloat32(), precision: 3);
        Assert.Equal(3f, rows[0]["x"].AsFloat32());
    }

    // ───────────────── Helpers ─────────────────

}

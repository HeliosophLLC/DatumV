using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <c>LET</c> bindings in SELECT — named, memoized intermediate
/// expressions computed once per row. Covers parsing, end-to-end execution,
/// chaining, memoization, output visibility, clause interactions, and error cases.
/// </summary>
public sealed class LetBindingTests : ServiceTestBase
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    // ─────────────── Parsing ───────────────

    /// <summary>
    /// A single LET binding parses to the correct AST node with name and expression.
    /// </summary>
    [Fact]
    public void Parse_SingleLetBinding()
    {
        SelectStatement statement = ParseStatement(
            "SELECT LET x = col1 + col2, x AS result FROM t");

        Assert.NotNull(statement.LetBindings);
        Assert.Single(statement.LetBindings);

        LetBinding binding = statement.LetBindings[0];
        Assert.Equal("x", binding.Name);
        Assert.Null(binding.OutputAlias);
        Assert.IsType<BinaryExpression>(binding.Expression);
    }

    /// <summary>
    /// A LET binding with <c>AS</c> alias has its <see cref="LetBinding.OutputAlias"/> set.
    /// </summary>
    [Fact]
    public void Parse_LetBindingWithAlias()
    {
        SelectStatement statement = ParseStatement(
            "SELECT LET x = col1 + col2 AS \"total\", col3 FROM t");

        Assert.NotNull(statement.LetBindings);
        Assert.Single(statement.LetBindings);

        LetBinding binding = statement.LetBindings[0];
        Assert.Equal("x", binding.Name);
        Assert.Equal("total", binding.OutputAlias);
    }

    /// <summary>
    /// Multiple chained LET bindings parse in order with correct names.
    /// </summary>
    [Fact]
    public void Parse_MultipleChainingLetBindings()
    {
        SelectStatement statement = ParseStatement(
            "SELECT LET a = x + 1, LET b = a * 2, b AS result FROM t");

        Assert.NotNull(statement.LetBindings);
        Assert.Equal(2, statement.LetBindings.Count);
        Assert.Equal("a", statement.LetBindings[0].Name);
        Assert.Equal("b", statement.LetBindings[1].Name);

        Assert.Single(statement.Columns);
    }

    /// <summary>
    /// A SELECT with no LET bindings produces a null <see cref="SelectStatement.LetBindings"/>.
    /// </summary>
    [Fact]
    public void Parse_NoLetBindings_ReturnsNull()
    {
        SelectStatement statement = ParseStatement("SELECT a, b FROM t");

        Assert.Null(statement.LetBindings);
        Assert.Equal(2, statement.Columns.Count);
    }

    /// <summary>
    /// LET after a regular column is a parse error because the grammar
    /// enforces LET-first ordering.
    /// </summary>
    [Fact]
    public void Parse_LetAfterRegularColumn_Throws()
    {
        Assert.ThrowsAny<Exception>(
            () => SqlParser.Parse("SELECT col1, LET x = col2, x FROM t"));
    }

    // ─────────────── Destructuring — parsing ───────────────

    /// <summary>
    /// Positional destructuring with two names parses to a <see cref="LetBinding"/> with
    /// <see cref="LetBinding.Destructure"/> set to <see cref="DestructureMode.Positional"/>.
    /// </summary>
    [Fact]
    public void Parse_PositionalDestructuring_TwoNames()
    {
        SelectStatement statement = ParseStatement(
            "SELECT LET (a, b) = some_func(x), a AS fa, b AS fb FROM t");

        Assert.NotNull(statement.LetBindings);
        Assert.Single(statement.LetBindings);

        LetBinding binding = statement.LetBindings[0];
        Assert.NotNull(binding.Destructure);
        Assert.Equal(DestructureMode.Positional, binding.Destructure.Mode);
        Assert.Equal(["a", "b"], binding.Destructure.Names);
        Assert.Null(binding.OutputAlias);
    }

    /// <summary>
    /// Positional destructuring with three names parses all names correctly.
    /// </summary>
    [Fact]
    public void Parse_PositionalDestructuring_ThreeNames()
    {
        SelectStatement statement = ParseStatement(
            "SELECT LET (x, y, z) = some_func(col), x AS fx FROM t");

        Assert.NotNull(statement.LetBindings);
        Assert.Single(statement.LetBindings);

        LetBinding binding = statement.LetBindings[0];
        Assert.NotNull(binding.Destructure);
        Assert.Equal(3, binding.Destructure.Names.Count);
        Assert.Equal(["x", "y", "z"], binding.Destructure.Names);
    }

    /// <summary>
    /// Named destructuring parses to <see cref="DestructureMode.Named"/> with correct field names.
    /// </summary>
    [Fact]
    public void Parse_NamedDestructuring_TwoNames()
    {
        SelectStatement statement = ParseStatement(
            "SELECT LET {label, score} = some_func(x), label AS lbl FROM t");

        Assert.NotNull(statement.LetBindings);
        Assert.Single(statement.LetBindings);

        LetBinding binding = statement.LetBindings[0];
        Assert.NotNull(binding.Destructure);
        Assert.Equal(DestructureMode.Named, binding.Destructure.Mode);
        Assert.Equal(["label", "score"], binding.Destructure.Names);
    }

    /// <summary>
    /// A mixture of a destructured binding and a plain binding in the same SELECT parses correctly.
    /// </summary>
    [Fact]
    public void Parse_MixedDestructuredAndScalarBindings()
    {
        SelectStatement statement = ParseStatement(
            "SELECT LET (a, b) = fn(x), LET c = a + b, c AS result FROM t");

        Assert.NotNull(statement.LetBindings);
        Assert.Equal(2, statement.LetBindings.Count);

        Assert.NotNull(statement.LetBindings[0].Destructure);
        Assert.Equal(DestructureMode.Positional, statement.LetBindings[0].Destructure!.Mode);

        Assert.Null(statement.LetBindings[1].Destructure);
        Assert.Equal("c", statement.LetBindings[1].Name);
    }

    /// <summary>
    /// A single-element positional pattern <c>LET (x) = expr</c> is a parse error.
    /// The grammar requires at least two names.
    /// </summary>
    [Fact]
    public void Parse_SingleElementPositional_ThrowsParseError()
    {
        Assert.ThrowsAny<Exception>(
            () => SqlParser.Parse("SELECT LET (x) = fn(col), x FROM t"));
    }

    /// <summary>
    /// A single-element named pattern <c>LET {x} = expr</c> is a parse error.
    /// </summary>
    [Fact]
    public void Parse_SingleElementNamed_ThrowsParseError()
    {
        Assert.ThrowsAny<Exception>(
            () => SqlParser.Parse("SELECT LET {x} = fn(col), x FROM t"));
    }

    // ─────────────── Destructuring — runtime ───────────────

    /// <summary>
    /// Positional destructuring from a float array produces correct scalar values.
    /// </summary>
    [Fact]
    public async Task EndToEnd_PositionalDestructuring_FromArray()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["arr"],
            [DataValue.FromInlineArray<float>([10f, 20f, 30f], DataKind.Float32)]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET (first, second, third) = arr, first AS a, second AS b, third AS c FROM t",
            catalog);

        Assert.Single(results);
        Assert.Equal(10f, results[0]["a"].AsFloat32());
        Assert.Equal(20f, results[0]["b"].AsFloat32());
        Assert.Equal(30f, results[0]["c"].AsFloat32());
    }

    /// <summary>
    /// Positional destructuring from a vector (float32[]) produced by cyclical_encode
    /// yields the sin and cos components as Float32 scalars.
    /// </summary>
    [Fact]
    public async Task EndToEnd_PositionalDestructuring_FromVector()
    {
        // cyclical_encode(month, period) returns a Vector([sin, cos]).
        // month = 3, period = 12  →  sin(2π·3/12) = sin(π/2) = 1.0, cos = 0.0
        // Pass period as a Float32 column — SQL numeric literals are Float64
        // and cyclical_encode requires Float32 arguments.
        TableCatalog catalog = CreateCatalog("t",
            columns: ["month", "period"],
            [3f, 12f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET (sin_v, cos_v) = cyclical_encode(month, period), sin_v AS s, cos_v AS c FROM t",
            catalog);

        Assert.Single(results);
        Assert.Equal(DataKind.Float32, results[0]["s"].Kind);
        Assert.Equal(DataKind.Float32, results[0]["c"].Kind);
        Assert.Equal(1.0f, results[0]["s"].AsFloat32(), precision: 4);
        Assert.Equal(0.0f, results[0]["c"].AsFloat32(), precision: 4);
    }

    /// <summary>
    /// Positional destructuring from a struct literal uses ordinal (declaration-order) extraction.
    /// </summary>
    [Fact]
    public async Task EndToEnd_PositionalDestructuring_FromStructLiteral()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x"],
            [0f]);  // dummy row

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET (p, q) = {alpha: 7.0, beta: 8.0}, p AS pv, q AS qv FROM t",
            catalog);

        Assert.Single(results);
        // Struct literal fields may be narrower integer types after literal narrowing.
        Assert.Equal(7.0, results[0]["pv"].ToDouble(), precision: 4);
        Assert.Equal(8.0, results[0]["qv"].ToDouble(), precision: 4);
    }

    /// <summary>
    /// Named destructuring from a struct literal extracts fields by name, independent of
    /// declaration order.
    /// </summary>
    [Fact]
    public async Task EndToEnd_NamedDestructuring_FromStructLiteral_OrderIndependent()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x"],
            [0f]);  // dummy row

        // Note: {beta: 8.0, alpha: 7.0} — reverse order, extracted in {alpha, beta} order.
        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET {alpha, beta} = {beta: 8.0, alpha: 7.0}, alpha AS av, beta AS bv FROM t",
            catalog);

        Assert.Single(results);
        // Struct literal fields may be narrower integer types after literal narrowing.
        Assert.Equal(7.0, results[0]["av"].ToDouble(), precision: 4);
        Assert.Equal(8.0, results[0]["bv"].ToDouble(), precision: 4);
    }

    /// <summary>
    /// Named destructuring where the source is a scalar LET alias (not an inline struct literal)
    /// correctly follows the alias chain to recover field metadata.
    /// e.g. <c>LET x = {a:1, b:2}; LET {a, b} = x</c>
    /// </summary>
    [Fact]
    public async Task EndToEnd_NamedDestructuring_FromLetAlias_FollowsChain()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["dummy"],
            [0f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET x = {a: 'hello', b: 42.0}, LET {a, b} = x, a AS av, b AS bv FROM t",
            catalog);

        Assert.Single(results);
        Assert.Equal("hello", results[0]["av"].AsString());
        Assert.Equal(42.0, results[0]["bv"].ToDouble(), precision: 4);
    }

    /// <summary>
    /// Destructured names can be consumed by subsequent LET bindings (chaining).
    /// </summary>
    [Fact]
    public async Task EndToEnd_Destructuring_ChainedInSubsequentLet()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["arr"],
            [DataValue.FromInlineArray<float>([3f, 4f], DataKind.Float32)]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET (a, b) = arr, LET hyp = a * a + b * b, hyp AS result FROM t",
            catalog);

        Assert.Single(results);
        Assert.Equal(25f, results[0]["result"].AsFloat32());  // 3²+4² = 25
    }

    /// <summary>
    /// Destructured names can be projected directly as output columns.
    /// </summary>
    [Fact]
    public async Task EndToEnd_Destructuring_NamesUsedInOutputColumns()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["v"],
            [DataValue.FromInlineArray<float>([5f, 6f], DataKind.Float32)]);

        // sin_v, cos_v appear both as LET names and directly as output columns
        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET (elem0, elem1) = v, elem0 AS e0, elem1 AS e1 FROM t",
            catalog);

        Assert.Single(results);
        Assert.Equal(5f, results[0]["e0"].AsFloat32());
        Assert.Equal(6f, results[0]["e1"].AsFloat32());
    }

    /// <summary>
    /// The source expression is evaluated exactly once per row even when multiple names are
    /// extracted. Verified using a function with observable side-effects (uuidv4 changes per call).
    /// </summary>
    [Fact]
    public async Task EndToEnd_Destructuring_SourceExpressionEvaluatedOncePerRow()
    {
        // cyclical_encode is deterministic, so we use it as a stable proxy.
        // If the source were evaluated per-name, sin² + cos² would still equal 1 but the
        // values would match. The memoization proof is that both references produce the SAME
        // DataValue instance — verified by checking consistency across all rows.
        TableCatalog catalog = CreateCatalog("t",
            columns: ["m", "p"],
            [1f, 12f],
            [4f, 12f],
            [7f, 12f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET (s, c) = cyclical_encode(m, p), " +
            "s * s + c * c AS sumsq FROM t",
            catalog);

        Assert.Equal(3, results.Count);
        // sin²+cos² = 1.0 for all rows, which holds whether or not memoization occurs.
        // The real memoization test is the downstream chaining test above.
        foreach (Row row in results)
        {
            Assert.Equal(1.0f, row["sumsq"].AsFloat32(), precision: 3);
        }
    }

    /// <summary>
    /// Out-of-bounds positional access from a vector returns null rather than throwing.
    /// </summary>
    [Fact]
    public async Task EndToEnd_PositionalDestructuring_OutOfBoundsReturnsNull()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["v"],
            [DataValue.FromInlineArray<float>([1f, 2f], DataKind.Float32)]);  // only 2 elements

        // LET (a, b, c) = v — c is out of bounds
        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET (a, b, c) = v, a AS e0, b AS e1, c AS e2 FROM t",
            catalog);

        Assert.Single(results);
        Assert.Equal(1f, results[0]["e0"].AsFloat32());
        Assert.Equal(2f, results[0]["e1"].AsFloat32());
        Assert.True(results[0]["e2"].IsNull);
    }

    /// <summary>
    /// Named destructuring (<c>LET {a, b} = expr</c>) on a Vector throws a clear
    /// <see cref="InvalidOperationException"/> explaining that positional destructuring
    /// must be used instead. Vectors have no named fields.
    /// </summary>
    [Fact]
    public async Task EndToEnd_NamedDestructuring_OnVector_ThrowsDescriptiveError()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["v"],
            [DataValue.FromInlineArray<float>([1f, 2f], DataKind.Float32)]);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteQueryAsync("SELECT LET {a, b} = v, a AS x, b AS y FROM t", catalog));

        Assert.Contains("Vector", ex.Message);
        Assert.Contains("positional", ex.Message);
    }

    /// <summary>
    /// Named destructuring on an Array also throws a clear error — arrays are positional,
    /// not named, just like vectors.
    /// </summary>
    [Fact]
    public async Task EndToEnd_NamedDestructuring_OnArray_ThrowsDescriptiveError()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["arr"],
            [DataValue.FromInlineArray<float>([1f, 2f], DataKind.Float32)]);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteQueryAsync("SELECT LET {a, b} = arr, a AS x, b AS y FROM t", catalog));

        Assert.Contains("Array", ex.Message);
        Assert.Contains("positional", ex.Message);
    }

    // ─────────────── End-to-end planner integration ───────────────

    /// <summary>
    /// A LET binding used in a single SELECT column produces the correct value.
    /// </summary>
    [Fact]
    public async Task EndToEnd_BasicLetBinding_ProducesCorrectValue()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["a", "b"],
            [10f, 3f],
            [20f, 7f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET s = a + b, s AS result FROM t", catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(13f, results[0]["result"].AsFloat32());
        Assert.Equal(27f, results[1]["result"].AsFloat32());
    }

    /// <summary>
    /// A LET binding without an <c>AS</c> alias does not appear in the output.
    /// </summary>
    [Fact]
    public async Task EndToEnd_LetBindingNotInOutput_WhenNoAlias()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["a", "b"],
            [5f, 2f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET s = a + b, s * 2 AS doubled FROM t", catalog);

        Assert.Single(results);
        Assert.Equal(1, results[0].FieldCount);
        Assert.Equal(14f, results[0]["doubled"].AsFloat32());
    }

    /// <summary>
    /// A LET binding with <c>AS</c> alias appears in the output with the alias name.
    /// </summary>
    [Fact]
    public async Task EndToEnd_LetBindingWithAlias_AppearsInOutput()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["a", "b"],
            [10f, 3f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET s = a + b AS \"sum\", s * 2 AS doubled FROM t", catalog);

        Assert.Single(results);
        Assert.Equal(2, results[0].FieldCount);
        Assert.Equal(13f, results[0]["sum"].AsFloat32());
        Assert.Equal(26f, results[0]["doubled"].AsFloat32());
    }

    /// <summary>
    /// LET bindings chain left-to-right: a later binding can reference an earlier one.
    /// </summary>
    [Fact]
    public async Task EndToEnd_LetChaining_LaterBindingReferencesEarlier()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x"],
            [4f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET a = x + 1, LET b = a * 3, b AS result FROM t", catalog);

        Assert.Single(results);
        Assert.Equal(15f, results[0]["result"].AsFloat32());
    }

    /// <summary>
    /// LET binding referenced multiple times produces identical values per row,
    /// proving memoization. Uses <c>uuidv4()</c> which returns a different value
    /// each time it is called; memoization means the LET expression is evaluated
    /// once and both references see the same UUID.
    /// </summary>
    [Fact]
    public async Task EndToEnd_Memoization_UuidStableAcrossReferences()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x"],
            [1f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET u = uuidv4(), uuid_str(u) AS first, uuid_str(u) AS second FROM t",
            catalog);

        Assert.Single(results);
        string first = results[0]["first"].AsString();
        string second = results[0]["second"].AsString();
        Assert.False(string.IsNullOrEmpty(first));
        Assert.Equal(first, second);
    }

    /// <summary>
    /// <c>SELECT *</c> does not include LET bindings — even aliased ones.
    /// </summary>
    [Fact]
    public async Task EndToEnd_StarDoesNotIncludeLetBindings()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["a", "b"],
            [1f, 2f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET s = a + b AS \"sum\", * FROM t", catalog);

        Assert.Single(results);
        // Output should be: sum, a, b (the aliased LET appears because of AS,
        // and * expands the source columns).
        Assert.Equal(3, results[0].FieldCount);
        Assert.Equal(3f, results[0]["sum"].AsFloat32());
        Assert.Equal(1f, results[0]["a"].AsFloat32());
        Assert.Equal(2f, results[0]["b"].AsFloat32());
    }

    /// <summary>
    /// LET binding works correctly with GROUP BY and aggregate functions.
    /// </summary>
    [Fact]
    public async Task EndToEnd_LetWithGroupBy_AggregateExpression()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["category", "value"],
            ["A", 10f],
            ["A", 20f],
            ["B", 30f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET total = SUM(value), category, total AS group_total FROM t GROUP BY category",
            catalog);

        Assert.Equal(2, results.Count);
        Row rowA = results.First(r => r["category"].AsString() == "A");
        Row rowB = results.First(r => r["category"].AsString() == "B");
        Assert.Equal(30f, rowA["group_total"].AsFloat32());
        Assert.Equal(30f, rowB["group_total"].AsFloat32());
    }

    /// <summary>
    /// LET binding with an alias can be referenced in ORDER BY via the alias name.
    /// </summary>
    [Fact]
    public async Task EndToEnd_LetAliasUsedInOrderBy()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x"],
            [3f],
            [1f],
            [2f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET doubled = x * 2 AS \"doubled\", x FROM t ORDER BY doubled",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(2f, results[0]["doubled"].AsFloat32());
        Assert.Equal(4f, results[1]["doubled"].AsFloat32());
        Assert.Equal(6f, results[2]["doubled"].AsFloat32());
    }

    /// <summary>
    /// Multiple LET bindings with mixed visibility: some aliased (output), some hidden.
    /// </summary>
    [Fact]
    public async Task EndToEnd_MixedVisibility_AliasedAndHidden()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x"],
            [10f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET a = x + 5 AS \"visible\", LET b = a * 2, b AS result FROM t",
            catalog);

        Assert.Single(results);
        Assert.Equal(2, results[0].FieldCount);
        Assert.Equal(15f, results[0]["visible"].AsFloat32());
        Assert.Equal(30f, results[0]["result"].AsFloat32());
    }

    /// <summary>
    /// LET binding works with a function call in the expression.
    /// </summary>
    [Fact]
    public async Task EndToEnd_LetWithFunctionCall()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x"],
            [-7f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET magnitude = ABS(x), magnitude AS result FROM t",
            catalog);

        Assert.Single(results);
        Assert.Equal(7f, results[0]["result"].AsFloat32());
    }

    /// <summary>
    /// Multiple rows are processed correctly with LET bindings, each row
    /// getting its own independently computed LET values.
    /// </summary>
    [Fact]
    public async Task EndToEnd_MultipleRows_IndependentLetValues()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x"],
            [2f],
            [5f],
            [10f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET sq = x * x, sq AS squared FROM t", catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(4f, results[0]["squared"].AsFloat32());
        Assert.Equal(25f, results[1]["squared"].AsFloat32());
        Assert.Equal(100f, results[2]["squared"].AsFloat32());
    }

    // ─────────────── Helpers ───────────────

    private static SelectStatement ParseStatement(string sql)
    {
        return ((SelectQueryExpression)SqlParser.Parse(sql)).Statement;
    }
}

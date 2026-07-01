using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// PostgreSQL allows an output-column alias (or an ordinal position) to be used
/// in <c>GROUP BY</c> and <c>ORDER BY</c>. These tests pin that behaviour, plus
/// the two precedence asymmetries the SQL standard mandates:
/// <list type="bullet">
///   <item>Alias resolution applies only when the whole clause item is a bare
///   name (or ordinal) — a name buried inside a larger expression refers to an
///   input column, never an alias.</item>
///   <item>On an ambiguous simple name (matches both an input column and an
///   output alias), <c>GROUP BY</c> picks the input column while <c>ORDER BY</c>
///   picks the alias.</item>
/// </list>
/// Note: these cover the <em>name-resolution</em> layer. Grouping by a bare
/// <em>expression</em> and projecting it back (e.g. the alias maps to
/// <c>CAST(f(x) AS ...)</c>) additionally depends on the grouped-expression
/// projection path, tracked separately.
/// </summary>
public sealed class GroupByOrderByAliasResolutionTests : ServiceTestBase
{
    // letters(ch): four rows, two 'a's — for GROUP BY ch → a=2, b=1, c=1.
    private TableCatalog LettersCatalog() => CreateCatalog(
        "letters",
        columns: ["ch"],
        new object?[] { "b" },
        new object?[] { "a" },
        new object?[] { "c" },
        new object?[] { "a" });

    // ─────────────── GROUP BY by projection alias ───────────────

    /// <summary>
    /// The reported bug: <c>GROUP BY</c> referencing a SELECT-list alias fails
    /// because the planner never substitutes the alias with its underlying
    /// expression before building the GroupByOperator (which runs pre-projection).
    /// </summary>
    [Fact]
    public async Task GroupBy_ByProjectionAlias_Resolves()
    {
        TableCatalog catalog = LettersCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT ch AS letter, COUNT(*) AS c FROM letters GROUP BY letter ORDER BY letter",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal("a", results[0]["letter"].AsString());
        Assert.Equal(2L, results[0]["c"].AsInt64());
        Assert.Equal("b", results[1]["letter"].AsString());
        Assert.Equal(1L, results[1]["c"].AsInt64());
        Assert.Equal("c", results[2]["letter"].AsString());
        Assert.Equal(1L, results[2]["c"].AsInt64());
    }

    /// <summary>
    /// Alias used in both <c>GROUP BY</c> and <c>ORDER BY</c>, here descending.
    /// </summary>
    [Fact]
    public async Task GroupBy_AndOrderBy_ByProjectionAlias_Descending()
    {
        TableCatalog catalog = LettersCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT ch AS letter, COUNT(*) AS c FROM letters GROUP BY letter ORDER BY letter DESC",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal("c", results[0]["letter"].AsString());
        Assert.Equal("b", results[1]["letter"].AsString());
        Assert.Equal("a", results[2]["letter"].AsString());
        Assert.Equal(2L, results[2]["c"].AsInt64());
    }

    // ─────────────── GROUP BY / ORDER BY by ordinal position ───────────────

    /// <summary>
    /// <c>GROUP BY 1</c> groups by the first SELECT item. Today a bare integer
    /// is treated as a constant grouping key, collapsing every row into one
    /// group — this asserts the PG behaviour instead.
    /// </summary>
    [Fact]
    public async Task GroupBy_ByOrdinalPosition_Resolves()
    {
        TableCatalog catalog = LettersCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT ch, COUNT(*) AS c FROM letters GROUP BY 1 ORDER BY 1",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal("a", results[0]["ch"].AsString());
        Assert.Equal(2L, results[0]["c"].AsInt64());
        Assert.Equal("b", results[1]["ch"].AsString());
        Assert.Equal("c", results[2]["ch"].AsString());
    }

    /// <summary>
    /// <c>ORDER BY 1</c> sorts by the first SELECT item. Today a bare integer is
    /// a constant sort key (no-op) — this asserts a real descending sort.
    /// </summary>
    [Fact]
    public async Task OrderBy_ByOrdinalPosition_Resolves()
    {
        TableCatalog catalog = LettersCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT ch, COUNT(*) AS c FROM letters GROUP BY ch ORDER BY 1 DESC",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal("c", results[0]["ch"].AsString());
        Assert.Equal("b", results[1]["ch"].AsString());
        Assert.Equal("a", results[2]["ch"].AsString());
    }

    /// <summary>
    /// Ordinal in <c>ORDER BY</c> pointing at an aggregate output column
    /// (<c>COUNT(*)</c> is the 2nd SELECT item). Descending by count, ties broken
    /// ascending by the 1st item.
    /// </summary>
    [Fact]
    public async Task OrderBy_ByOrdinalPosition_ReferencingAggregate()
    {
        TableCatalog catalog = LettersCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT ch, COUNT(*) AS c FROM letters GROUP BY ch ORDER BY 2 DESC, 1",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal("a", results[0]["ch"].AsString());
        Assert.Equal(2L, results[0]["c"].AsInt64());
        Assert.Equal("b", results[1]["ch"].AsString());
        Assert.Equal("c", results[2]["ch"].AsString());
    }

    /// <summary>
    /// Ordinal past the end of the select list is a plan-time error, matching
    /// PostgreSQL's "position N is not in select list".
    /// </summary>
    [Fact]
    public async Task OrderBy_OrdinalOutOfRange_Throws()
    {
        TableCatalog catalog = LettersCatalog();

        await Assert.ThrowsAnyAsync<Exception>(() => ExecuteQueryAsync(
            "SELECT ch, COUNT(*) AS c FROM letters GROUP BY ch ORDER BY 5",
            catalog));
    }

    // ─────────────── ORDER BY by alias (already post-projection) ───────────────

    /// <summary>
    /// <c>ORDER BY</c> by a plain alias already works because ORDER BY runs after
    /// projection, where the alias exists as a real output column. Guards against
    /// a regression from the new resolution path.
    /// </summary>
    [Fact]
    public async Task OrderBy_ByProjectionAlias_StillResolves()
    {
        TableCatalog catalog = LettersCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT ch, COUNT(*) AS cnt FROM letters GROUP BY ch ORDER BY cnt DESC, ch",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(2L, results[0]["cnt"].AsInt64());
        Assert.Equal("a", results[0]["ch"].AsString());
    }

    // ─────────────── Precedence asymmetry (SQL standard) ───────────────

    /// <summary>
    /// Shadow case for <c>GROUP BY</c>: a name that is both an input column and
    /// an output alias resolves to the <em>input column</em>. Here alias
    /// <c>k = k % 2</c> would collapse four rows into two groups, but PG groups
    /// by the input column <c>k</c> → four singleton groups.
    /// </summary>
    [Fact]
    public async Task GroupBy_ShadowedName_PrefersInputColumn()
    {
        TableCatalog catalog = CreateCatalog(
            "shg",
            columns: ["k"],
            new object?[] { 0 },
            new object?[] { 1 },
            new object?[] { 2 },
            new object?[] { 3 });

        List<Row> results = await ExecuteQueryAsync(
            "SELECT k % 2 AS k, COUNT(*) AS c FROM shg GROUP BY k",
            catalog);

        // Input-column precedence → four groups of one; alias precedence would
        // give two groups of two.
        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal(1L, r["c"].AsInt64()));
    }

    /// <summary>
    /// Shadow case for <c>ORDER BY</c>: the output alias wins. Alias
    /// <c>k = k * -1</c> means ordering ascending by the alias reverses the
    /// original <c>k</c> order — verified via a stable companion column.
    /// </summary>
    [Fact]
    public async Task OrderBy_ShadowedName_PrefersAlias()
    {
        TableCatalog catalog = CreateCatalog(
            "sho",
            columns: ["k", "tag"],
            new object?[] { 0, "z0" },
            new object?[] { 1, "z1" },
            new object?[] { 2, "z2" },
            new object?[] { 3, "z3" });

        List<Row> results = await ExecuteQueryAsync(
            "SELECT k * -1 AS k, tag FROM sho ORDER BY k",
            catalog);

        // Ascending by alias (-k): -3, -2, -1, 0 → tags z3, z2, z1, z0.
        Assert.Equal(4, results.Count);
        Assert.Equal("z3", results[0]["tag"].AsString());
        Assert.Equal("z2", results[1]["tag"].AsString());
        Assert.Equal("z1", results[2]["tag"].AsString());
        Assert.Equal("z0", results[3]["tag"].AsString());
    }

    // ─────────────── Table-valued function sources ───────────────

    /// <summary>
    /// GROUP BY an alias whose FROM source is a table-valued function. The
    /// planner discovers the TVF's output columns (here <c>value</c>) so the
    /// alias <c>parity</c> — not a TVF column — is substituted with its
    /// expression, and the grouped expression projects back correctly.
    /// </summary>
    [Fact]
    public async Task GroupBy_ByAlias_OverTableValuedFunction()
    {
        TableCatalog catalog = CreateCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT value % 2 AS parity, COUNT(*) AS c FROM generate_series(1, 6) GROUP BY parity ORDER BY parity",
            catalog);

        // generate_series(1,6) → parity 0 for {2,4,6}, parity 1 for {1,3,5}.
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(3L, r["c"].AsInt64()));
    }

    /// <summary>
    /// Precedence over a TVF source: a GROUP BY name that is also a TVF output
    /// column (<c>value</c>) binds to that column, not the like-named alias.
    /// Relies on the planner resolving the TVF's column set.
    /// </summary>
    [Fact]
    public async Task GroupBy_ShadowedName_OverTableValuedFunction_PrefersInputColumn()
    {
        TableCatalog catalog = CreateCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT value % 3 AS value, COUNT(*) AS c FROM generate_series(0, 5) GROUP BY value",
            catalog);

        // Input-column precedence → six singleton groups; alias precedence would
        // collapse to three groups of two.
        Assert.Equal(6, results.Count);
        Assert.All(results, r => Assert.Equal(1L, r["c"].AsInt64()));
    }

    // ─────────────── Alias must not resolve inside a larger expression ───────────────

    /// <summary>
    /// A name inside a compound <c>GROUP BY</c> expression is <em>not</em> an
    /// output-alias reference — it must resolve as an input column. Since no
    /// input column <c>letter</c> exists, the query errors rather than silently
    /// grouping by the alias's expression.
    /// </summary>
    [Fact]
    public async Task GroupBy_AliasInsideCompoundExpression_DoesNotResolve()
    {
        TableCatalog catalog = LettersCatalog();

        await Assert.ThrowsAnyAsync<Exception>(() => ExecuteQueryAsync(
            "SELECT ch AS letter FROM letters GROUP BY letter || 'x'",
            catalog));
    }
}

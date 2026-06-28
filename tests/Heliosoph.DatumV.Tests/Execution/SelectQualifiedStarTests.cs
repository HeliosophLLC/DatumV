using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Tests for qualified-star (<c>SELECT alias.*</c>) expansion in the
/// projection operator. <see cref="Operators.ProjectOperator"/> matches
/// the qualified-star arm by string-prefixing the input batch's column
/// names with <c>"alias."</c>, so when the source schema carries
/// unqualified names (e.g. a subquery whose inner SELECT produced a bare
/// column like <c>test</c>) the prefix match fails and zero columns
/// project. Bare <c>SELECT *</c> takes a separate arm that just walks
/// every input column and is not affected.
/// </summary>
public sealed class SelectQualifiedStarTests : ServiceTestBase
{
    [Fact]
    public async Task SelectStar_OverAliasedScalarSubquery_ProjectsInnerColumn()
    {
        TableCatalog catalog = CreateCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT * FROM (SELECT 'hello' AS test) t",
            catalog);

        Assert.Single(results);
        Assert.Equal(1, results[0].FieldCount);
        Assert.Equal("test", results[0].ColumnNames[0]);
        Assert.Equal("hello", results[0]["test"].AsString());
    }

    [Fact]
    public async Task SelectQualifiedStar_OverAliasedScalarSubquery_ProjectsInnerColumn()
    {
        TableCatalog catalog = CreateCatalog();

        // Same shape as the SELECT * case above. SELECT t.* should expand to
        // the subquery's projected columns (here: a single "test" column),
        // mirroring PostgreSQL semantics where alias.* names every column
        // exposed by the aliased range-variable.
        List<Row> results = await ExecuteQueryAsync(
            "SELECT t.* FROM (SELECT 'hello' AS test) t",
            catalog);

        Assert.Single(results);
        Assert.Equal(1, results[0].FieldCount);
        Assert.Equal("test", results[0].ColumnNames[0]);
        Assert.Equal("hello", results[0]["test"].AsString());
    }

    [Fact]
    public async Task SelectQualifiedStar_OverAliasedMultiColumnSubquery_ProjectsAllInnerColumns()
    {
        TableCatalog catalog = CreateCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT t.* FROM (SELECT 'a' AS first_col, 'b' AS second_col) t",
            catalog);

        Assert.Single(results);
        Assert.Equal(2, results[0].FieldCount);
        Assert.Equal("first_col", results[0].ColumnNames[0]);
        Assert.Equal("second_col", results[0].ColumnNames[1]);
        Assert.Equal("a", results[0]["first_col"].AsString());
        Assert.Equal("b", results[0]["second_col"].AsString());
    }

    [Fact]
    public async Task SelectQualifiedStar_OverAliasedSubqueryFromTable_ProjectsRows()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id", "amount"],
            [1f, 100f],
            [2f, 200f],
            [3f, 300f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT t.* FROM (SELECT id, amount FROM orders) t",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.All(results, row => Assert.Equal(2, row.FieldCount));
        Assert.Equal(1f, results[0]["id"].AsFloat32());
        Assert.Equal(100f, results[0]["amount"].AsFloat32());
    }

    [Fact]
    public async Task SelectQualifiedStar_OverAliasedSubqueryCrossJoinUnnest_ProjectsAndExpands()
    {
        // FROM (subquery) t, UNNEST(t.col) — a lateral cross join. The
        // subquery exposes an array column whose elements drive the
        // expansion; `SELECT t.*` should emit the subquery's columns once
        // per UNNEST element. This is the join-context analogue of the
        // bare `SELECT t.* FROM (subquery) t` case.
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("items",
            columns: ["id", "tags"],
            new object?[] { 1f, new[] { "red", "blue" } },
            new object?[] { 2f, new[] { "green" } }));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT t.* FROM (SELECT id, tags FROM items) t, UNNEST(t.tags)",
            catalog);

        // 2 elements for id=1 + 1 element for id=2 = 3 rows.
        Assert.Equal(3, results.Count);
        Assert.All(results, row => Assert.Equal(2, row.FieldCount));
        Assert.Equal(1f, results[0]["id"].AsFloat32());
        Assert.Equal(1f, results[1]["id"].AsFloat32());
        Assert.Equal(2f, results[2]["id"].AsFloat32());
    }

    [Fact]
    public async Task SelectStar_OverSubqueryCrossJoinUnaliasedUnnest_IncludesUnnestColumn()
    {
        // PostgreSQL convention: an unaliased function source defaults to
        // the function name as its alias for wildcard expansion. The
        // planner already wraps the FunctionSourceOperator with an
        // AliasOperator using that fallback (so the runtime batch carries
        // `unnest.value`); previously the star expander's GetSourceAlias
        // helper didn't mirror the fallback, so `SELECT *` silently
        // dropped the unaliased function's column.
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("items",
            columns: ["id", "tags"],
            new object?[] { 1f, new[] { "red", "blue" } },
            new object?[] { 2f, new[] { "green" } }));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT * FROM (SELECT id, tags FROM items) t, UNNEST(t.tags)",
            catalog);

        Assert.Equal(3, results.Count);
        // Subquery's two columns plus the UNNEST value column — three in total.
        Assert.All(results, row => Assert.True(row.FieldCount >= 3,
            $"expected ≥ 3 columns, got {row.FieldCount}: [{string.Join(", ", row.ColumnNames)}]"));
        // The unnest column lands under the function-name fallback alias.
        Assert.Equal("red", results[0]["unnest.value"].AsString());
        Assert.Equal("blue", results[1]["unnest.value"].AsString());
        Assert.Equal("green", results[2]["unnest.value"].AsString());
    }

    [Fact]
    public async Task SelectStar_OverAliasedSubqueryCrossJoinAliasedUnnest_EmitsAllSourceColumns()
    {
        // Bare `SELECT *` in a comma-join with UNNEST. The runtime's
        // wildcard expansion only contributes per-source columns for
        // sources that carry an alias (matches the existing behaviour
        // exercised by SelectStarPlusUnnest_OmitsSyntheticSourceFromWildcard),
        // so this test aliases the UNNEST as `u`. Previously the
        // subquery's columns came back empty because the SubqueryOperator's
        // unprefixed pass-through didn't satisfy the `t.` prefix-match in
        // the runtime expansion of the rewritten `t.*`.
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("items",
            columns: ["id", "tags"],
            new object?[] { 1f, new[] { "red", "blue" } },
            new object?[] { 2f, new[] { "green" } }));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT * FROM (SELECT id, tags FROM items) t, UNNEST(t.tags) AS u",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.All(results, row => Assert.True(row.FieldCount >= 3,
            $"expected ≥ 3 columns, got {row.FieldCount}: [{string.Join(", ", row.ColumnNames)}]"));
        // Output names are qualified (`t.id`, `u.value`) — SelectStarExpander
        // sets QualifyOutput:true for the join case to avoid collisions.
        Assert.Equal(1f, results[0]["t.id"].AsFloat32());
        Assert.Equal("red", results[0]["u.value"].AsString());
    }
}

using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Exercises <see cref="QueryScopeValidator"/> through
/// <see cref="TableCatalog.PlanQuery"/> — the validator wires in at the
/// PlanQuery boundary so end-to-end tests reflect the real plan-time
/// gate. The headline coverage: a bare FROM / JOIN alias used as a
/// scalar value (the user-reported pattern <c>image_draw_bounding_boxes(file, c)</c>)
/// throws <see cref="ExecutionException"/> at plan time instead of
/// surfacing as a row-evaluator error after upstream operators (often
/// model invocations) have already done expensive work.
/// </summary>
public sealed class QueryScopeValidatorTests : ServiceTestBase
{
    private TableCatalog BuildCatalogWithItemsTable()
    {
        // CreateCatalog overload that takes (tableName, columns, rows…)
        // registers an in-memory table on a fresh catalog. Empty rows is
        // fine — the validator only inspects the schema, never executes.
        return CreateCatalog("items", columns: ["id", "file"]);
    }

    private static QueryExpression Parse(string sql)
    {
        Statement stmt = SqlParser.ParseStatement(sql);
        return stmt is QueryStatement qs
            ? qs.Query
            : throw new InvalidOperationException("Test SQL must parse as a QueryStatement.");
    }

    [Fact]
    public void Plan_BareFunctionSourceAlias_AsValue_Throws()
    {
        // The reported shape: `c` referenced as a value inside a
        // function call after `CROSS JOIN unnest(...) c` binds `c` as
        // an alias. Previously this would surface seconds-to-minutes
        // into execution; the plan-time validator should catch it
        // before any operator runs.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse(
            "SELECT c FROM items a CROSS JOIN unnest(a.id) c");

        ExecutionException ex = Assert.Throws<ExecutionException>(() => catalog.PlanQuery(query));
        Assert.Contains("'c' is a table or subquery alias", ex.Message);
    }

    [Fact]
    public void Plan_BareTableAlias_AsValue_Throws()
    {
        // Same shape with a plain table alias. `SELECT a FROM items a`
        // references `a` as a value; the engine doesn't support
        // row-as-composite, so the runtime would throw — plan-time
        // catches it instead.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse("SELECT a FROM items a");

        ExecutionException ex = Assert.Throws<ExecutionException>(() => catalog.PlanQuery(query));
        Assert.Contains("'a' is a table or subquery alias", ex.Message);
    }

    [Fact]
    public void Plan_QualifiedAliasReference_DoesNotThrow()
    {
        // `a.id` is the correct way to reference the alias's column —
        // the validator must not false-positive on legitimate qualified
        // references.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse("SELECT a.id FROM items a");

        // Should plan without throwing. We don't execute here — just
        // confirm the plan-time gate accepts the shape.
        catalog.PlanQuery(query);
    }

    [Fact]
    public void Plan_BareColumnReference_DoesNotThrow()
    {
        // Bare unqualified column references against a known table
        // resolve at runtime via the row-lookup path; the validator
        // intentionally skips column-existence checks (procedural
        // variables, LET bindings, lambda params, aggregate results,
        // and projection aliases all aren't tracked here) and only
        // flags the alias-as-value case.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse("SELECT id, file FROM items a");

        catalog.PlanQuery(query);
    }

    [Fact]
    public void Plan_AliasInWhereClause_AsValue_Throws()
    {
        // The misuse can surface in WHERE just as well as SELECT — the
        // validator walks every expression slot.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse(
            "SELECT a.id FROM items a WHERE a = 1");

        ExecutionException ex = Assert.Throws<ExecutionException>(() => catalog.PlanQuery(query));
        Assert.Contains("'a' is a table or subquery alias", ex.Message);
    }

    [Fact]
    public void Plan_UnknownUnqualifiedColumn_Throws()
    {
        // A bare reference to a name that's not a column on any source,
        // not a procedural variable, LET binding, lambda parameter, or
        // projection alias — and where the scope chain contains no
        // opaque source — fails at plan time instead of waiting for
        // the per-row evaluator to throw.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse("SELECT typo FROM items");

        ExecutionException ex = Assert.Throws<ExecutionException>(() => catalog.PlanQuery(query));
        Assert.Contains("Unknown column 'typo'", ex.Message);
    }

    [Fact]
    public void Plan_UnknownQualifierInTwoPartReference_Throws()
    {
        // `bogus.id` where `bogus` matches no FROM/JOIN alias, no
        // CTE name, no procedural variable, and no lambda parameter.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse("SELECT bogus.id FROM items a");

        ExecutionException ex = Assert.Throws<ExecutionException>(() => catalog.PlanQuery(query));
        Assert.Contains("Unknown table or alias 'bogus'", ex.Message);
    }

    [Fact]
    public void Plan_LetBindingReference_DoesNotThrow()
    {
        // LET bindings are valid bare references throughout the
        // statement — referencing one is the intended use, not a
        // misuse like alias-as-value.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse(
            "SELECT LET k = id, k FROM items");

        catalog.PlanQuery(query);
    }

    [Fact]
    public void Plan_LetBindingReferencedInLateralTvfArg_DoesNotThrow()
    {
        // `unnest(classes)` where `classes` is a LET in the same SELECT.
        // FROM/JOIN sources are walked left-to-right, and the TVF arg is
        // validated as part of the JOIN — but the LET is declared in the
        // same statement's SELECT clause. The validator must register
        // LET names BEFORE walking sources so the lateral arg resolves.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse(
            "SELECT LET classes = id, c.value "
            + "FROM items a CROSS JOIN unnest(classes) c");

        catalog.PlanQuery(query);
    }

    [Fact]
    public void Plan_ProjectionAliasReferencedInOrderBy_DoesNotThrow()
    {
        // PG-style: a SELECT alias is visible to ORDER BY. The
        // validator must accept the alias even though it's not a
        // source column.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse(
            "SELECT id AS thing FROM items ORDER BY thing DESC");

        catalog.PlanQuery(query);
    }

    [Fact]
    public void Plan_TvfSourceResolvesOutputColumnNames_Throws_OnNameNotProduced()
    {
        // Regression: when a TVF source like `unnest(...)` is in
        // scope, the validator must still flag refs to names that
        // neither the base table nor the TVF produces. Previously a
        // blanket opaque-source suppression let typos like `filex`
        // through; now the TVF's output column NAMES are resolved
        // up-front (best-effort `ValidateArguments` with placeholder
        // arg kinds) so `filex` fails alongside the unnest output
        // column `value` being correctly accepted.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse(
            "SELECT filex FROM items a CROSS JOIN unnest(a.id) c");

        ExecutionException ex = Assert.Throws<ExecutionException>(() => catalog.PlanQuery(query));
        Assert.Contains("Unknown column 'filex'", ex.Message);
    }

    [Fact]
    public void Plan_TvfSourceResolvesOutputColumnNames_DoesNotThrow_OnRealOutputName()
    {
        // Companion to the above: refs to a TVF's actual output
        // column NAME (here `value` from unnest) succeed.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse(
            "SELECT value FROM items a CROSS JOIN unnest(a.id) c");

        catalog.PlanQuery(query);
    }

    [Fact]
    public void Plan_LambdaParameter_InScopeForLambdaBody()
    {
        // Lambda parameters bind names locally for the body — bare
        // references in the body must resolve through the parameter
        // scope without false-positiving as "Unknown column".
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse(
            "SELECT array_transform([1, 2, 3], n -> n + 1) FROM items");

        catalog.PlanQuery(query);
    }

    [Fact]
    public void Plan_AliasInsideFunctionCall_AsValue_Throws()
    {
        // Reproduction of the exact failing call shape — the alias
        // sits as an argument inside a function call, several levels
        // deep into the expression tree. The validator walks function
        // arguments recursively.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse(
            "SELECT abs(c) FROM items a CROSS JOIN unnest(a.id) c");

        ExecutionException ex = Assert.Throws<ExecutionException>(() => catalog.PlanQuery(query));
        Assert.Contains("'c' is a table or subquery alias", ex.Message);
    }
}

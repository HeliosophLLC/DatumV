using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

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

    /// <summary>
    /// A model catalog with one entry <c>classify</c> whose declared output is
    /// a struct of <c>{ label, score }</c>. The loader throws — plan-time
    /// validation reads only the declared <c>OutputStructFields</c> metadata
    /// and never instantiates the model.
    /// </summary>
    private static ModelCatalog BuildModelCatalogWithClassify()
    {
        ModelCatalog models = new(modelDirectory: System.IO.Path.GetTempPath());
        models.Register(new ModelCatalogEntry(
            Name: "classify",
            Backend: "test",
            RelativePath: null,
            InputKinds: [DataKind.Image],
            OutputKind: DataKind.Struct,
            IsDeterministic: true,
            Loader: _ => throw new NotSupportedException("Plan-time validation must not load the model."),
            OutputStructFields:
            [
                new ModelStructFieldInfo("label", DataKind.String, IsArray: false, KindLabel: "String"),
                new ModelStructFieldInfo("score", DataKind.Float32, IsArray: false, KindLabel: "Float32"),
            ]));
        return models;
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
    public void Plan_LetBindingUsedAsStructQualifier_DoesNotThrow()
    {
        // A LET binding can hold a struct (e.g. `LET d = models.depth_anything(img)`)
        // and downstream refs project struct fields via `d.depth`. The
        // 2-part `name.field` path must check LET bindings alongside
        // FROM/JOIN aliases — otherwise legitimate struct-field access
        // on a LET trips "Unknown table or alias".
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse(
            "SELECT LET k = id, k.field FROM items");

        catalog.PlanQuery(query);
    }

    [Fact]
    public void Plan_ProjectionAliasUsedAsStructQualifier_DoesNotThrow()
    {
        // Symmetric case for projection aliases: `SELECT expr AS s`
        // followed by `s.field` in ORDER BY / HAVING / QUALIFY. The
        // 2-part path must consult the projection-alias set so a
        // struct-valued projection can be field-accessed downstream.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse(
            "SELECT id AS s FROM items ORDER BY s.field");

        catalog.PlanQuery(query);
    }

    [Fact]
    public void Plan_StructColumnFromSubquery_UsedAsQualifier_DoesNotThrow()
    {
        // A subquery projects a struct-valued column `s`; the outer query
        // accesses its fields via `s.label` / `s.score`. The subquery is an
        // opaque source — the outer scope can't see that it emits `s` — so
        // the 2-part qualifier path must defer to the opaque source in scope
        // rather than throw "Unknown table or alias 's'". The runtime
        // row-evaluator resolves `s` as a column and `.label` as a struct
        // field. Mirrors the bare-name path, which already defers to opaque
        // sources via ScopeChainContainsOpaqueSource.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse(
            "SELECT s.label, s.score FROM ("
            + "SELECT { label: 'test', score: 0.9 } s) t");

        catalog.PlanQuery(query);
    }

    /// <summary>
    /// A model catalog with one detector <c>detect</c> whose declared output is
    /// an <em>array</em> of struct <c>{ bbox, label, score }</c> — the shape an
    /// <c>UNNEST(models.detect(file))</c> source expands one struct per row.
    /// </summary>
    private static ModelCatalog BuildModelCatalogWithDetector()
    {
        ModelCatalog models = new(modelDirectory: System.IO.Path.GetTempPath());
        models.Register(new ModelCatalogEntry(
            Name: "detect",
            Backend: "test",
            RelativePath: null,
            InputKinds: [DataKind.Image],
            OutputKind: DataKind.Struct,
            IsDeterministic: true,
            Loader: _ => throw new NotSupportedException("Plan-time validation must not load the model."),
            OutputIsArray: true,
            OutputStructFields:
            [
                new ModelStructFieldInfo("bbox", DataKind.Struct, IsArray: false, KindLabel: "Struct"),
                new ModelStructFieldInfo("label", DataKind.String, IsArray: false, KindLabel: "String"),
                new ModelStructFieldInfo("score", DataKind.Float32, IsArray: false, KindLabel: "Float32"),
            ]));
        return models;
    }

    [Fact]
    public void Plan_UnknownStructFieldFromUnnestedDetector_Throws()
    {
        // The 3-part shape from the detector model cards:
        // `UNNEST(models.detect(file)) AS d` then `d.value.bogus`. The model's
        // OutputStructFields describe the element struct UNNEST yields as
        // `value`, so a typo'd field fails AT PLAN TIME — before the detector
        // runs over the whole input (the GROUP BY d.value.label shape is
        // blocking, so the runtime error would otherwise wait for every frame).
        TableCatalog catalog = BuildCatalogWithItemsTable();
        catalog.Models = BuildModelCatalogWithDetector();

        QueryExpression query = Parse(
            "SELECT d.value.bogus FROM items i CROSS JOIN UNNEST(models.detect(i.file)) AS d");

        ExecutionException ex = Assert.Throws<ExecutionException>(() => catalog.PlanQuery(query));
        Assert.Contains("Struct column 'd.value' has no field named 'bogus'", ex.Message);
    }

    [Fact]
    public void Plan_KnownStructFieldFromUnnestedDetector_DoesNotThrow()
    {
        // Companion: real element-struct fields (`d.value.label`,
        // `d.value.score`) plan cleanly, including in WHERE / GROUP BY / ORDER
        // BY — every clause routes through the same field check.
        TableCatalog catalog = BuildCatalogWithItemsTable();
        catalog.Models = BuildModelCatalogWithDetector();

        QueryExpression query = Parse(
            "SELECT d.value.label AS label, COUNT(*) AS hits "
            + "FROM items i CROSS JOIN UNNEST(models.detect(i.file)) AS d "
            + "WHERE d.value.score > 0.4 "
            + "GROUP BY d.value.label ORDER BY hits DESC");

        catalog.PlanQuery(query);
    }

    [Fact]
    public void Plan_UnknownStructFieldFromSubqueryModelColumn_Throws()
    {
        // The headline of Slice 3: a typo'd struct field on a model-projected
        // column (`p.labxl` where the subquery emits `models.classify(file) AS p`
        // and the model declares fields label/score) FAILS AT PLAN TIME. Before
        // this, the error surfaced only per-row at the very end of execution —
        // after the model inference had already run for minutes.
        TableCatalog catalog = BuildCatalogWithItemsTable();
        catalog.Models = BuildModelCatalogWithClassify();

        QueryExpression query = Parse(
            "SELECT p.labxl FROM (SELECT models.classify(file) AS p FROM items) t");

        ExecutionException ex = Assert.Throws<ExecutionException>(() => catalog.PlanQuery(query));
        Assert.Contains("Struct column 'p' has no field named 'labxl'", ex.Message);
    }

    [Fact]
    public void Plan_KnownStructFieldFromSubqueryModelColumn_DoesNotThrow()
    {
        // Companion: real fields of the model's declared output struct plan
        // cleanly — the field-name check must not false-positive.
        TableCatalog catalog = BuildCatalogWithItemsTable();
        catalog.Models = BuildModelCatalogWithClassify();

        QueryExpression query = Parse(
            "SELECT p.label, p.score FROM (SELECT models.classify(file) AS p FROM items) t");

        catalog.PlanQuery(query);
    }

    [Fact]
    public void Plan_UnknownStructFieldFromSubqueryStructLiteralColumn_Throws()
    {
        // The struct shape can also come from a struct literal, whose field
        // names live directly in the AST — no model needed. `s.bogus` fails at
        // plan time; `s.label` / `s.score` would pass.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        QueryExpression query = Parse(
            "SELECT s.bogus FROM (SELECT { label: 'test', score: 0.9 } s) t");

        ExecutionException ex = Assert.Throws<ExecutionException>(() => catalog.PlanQuery(query));
        Assert.Contains("Struct column 's' has no field named 'bogus'", ex.Message);
    }

    [Fact]
    public async Task StructColumnFromSubquery_FieldAccess_RunsEndToEnd()
    {
        // End-to-end companion to the plan-time test above: the exact
        // user-reported shape executes and projects the struct's fields.
        TableCatalog catalog = BuildCatalogWithItemsTable();

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT s.label, s.score FROM ("
            + "SELECT { label: 'test', score: 0.9 } s) t",
            catalog);

        Row row = Assert.Single(rows);
        Arena scratch = catalog.Pool.Backing.RentArena();
        try
        {
            Assert.Equal("test", row["label"].AsString(scratch));
            Assert.Equal(0.9, row["score"].AsFloat64(), 5);
        }
        finally { catalog.Pool.Backing.TryReturn(scratch); }
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

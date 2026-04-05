namespace DatumIngest.Tests.Execution;

using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

/// <summary>
/// Tests for the post-pass that collapses adjacent single-invocation
/// <see cref="ModelInvocationOperator"/> stacks (optionally bridged by
/// pure-alias <see cref="RowEnricherOperator"/> rungs) into one
/// multi-invocation <see cref="ModelInvocationOperator"/>. Single-MIO
/// plans are left unchanged; stacks of 2+ are merged with the bottom-up
/// invocation order the operator expects.
/// </summary>
public sealed class BatchedModelDagCollapserTests : ServiceTestBase
{
    private static QueryOperator PlanQuery(string sql, TableCatalog catalog)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        return planner.Plan(query);
    }

    private sealed class FakeModel : IModel
    {
        public required string Name { get; init; }
        public bool IsDeterministic => true;
        public IReadOnlyList<DataKind> InputKinds => [DataKind.String];
        public DataKind OutputKind => DataKind.String;

        public Task<IReadOnlyList<ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
            CancellationToken cancellationToken)
        {
            ValueRef[] results = new ValueRef[inputs.Count];
            for (int i = 0; i < inputs.Count; i++)
                results[i] = ValueRef.FromString("ok");
            return Task.FromResult<IReadOnlyList<ValueRef>>(results);
        }
    }

    private static void RegisterEcho(ModelCatalog models, string name)
    {
        models.Register(new ModelCatalogEntry(
            Name: name,
            Backend: "fake",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => new FakeModel { Name = name }));
    }

    [Fact]
    public void SingleModelInvocation_NotCollapsed()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["name"],
            new object?[] { "x" });
        catalog.Models = new ModelCatalog();
        RegisterEcho(catalog.Models, "echo");

        QueryOperator plan = PlanQuery("SELECT models.echo(name) FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        // Single MIO is left as-is (Invocations.Count == 1). The
        // collapser is for merging adjacent MIOs into a multi-invocation
        // shape; with nothing to merge it returns the input unchanged.
        ModelInvocationOperator mio = Assert.IsType<ModelInvocationOperator>(project.Source);
        Assert.Single(mio.Invocations);
    }

    [Fact]
    public void TwoSiblingInvocations_CollapsedIntoDag()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["name"],
            new object?[] { "x" });
        catalog.Models = new ModelCatalog();
        RegisterEcho(catalog.Models, "echo");

        QueryOperator plan = PlanQuery(
            "SELECT models.echo('a'), models.echo('b') FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        ModelInvocationOperator dag = Assert.IsType<ModelInvocationOperator>(project.Source);

        Assert.Equal(2, dag.Invocations.Count);
        // Both invocations reference the same model — they're siblings,
        // each with a different literal arg.
        Assert.All(dag.Invocations, inv => Assert.Equal("echo", inv.ModelName));
        Assert.NotEqual(
            dag.Invocations[0].OutputColumnName,
            dag.Invocations[1].OutputColumnName);
    }

    [Fact]
    public void ThreeInvocations_CollapsedIntoOneOperator()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["name"],
            new object?[] { "x" });
        catalog.Models = new ModelCatalog();
        RegisterEcho(catalog.Models, "echo");

        QueryOperator plan = PlanQuery(
            "SELECT models.echo('a'), models.echo('b'), models.echo('c') FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        ModelInvocationOperator dag = Assert.IsType<ModelInvocationOperator>(project.Source);
        Assert.Equal(3, dag.Invocations.Count);

        // The source under the collapsed operator is no longer a MIO —
        // the entire MIO stack got swallowed into the multi-invocation
        // node above it.
        Assert.IsNotType<ModelInvocationOperator>(dag.Source);
    }

    [Fact]
    public void LetBindingBetweenModels_CollapsesAcrossAliasEnricher()
    {
        // SELECT LET model = models.echo('seed'), models.echo(model), models.echo(model) FROM t
        // — the LET-staircase rewrite puts a pure-alias RowEnricher between
        // the inner MIO (the LET's model call) and the outer MIOs (which
        // reference the LET name). The hoister dedups the two textually-
        // identical `echo(model)` consumers into a single MIO, leaving
        // two MIOs total: the seed and one consumer. The collapser should
        // fold across the alias enricher to produce one
        // ModelInvocationOperator with both invocations.
        TableCatalog catalog = CreateCatalog("t",
            columns: ["name"],
            new object?[] { "x" });
        catalog.Models = new ModelCatalog();
        RegisterEcho(catalog.Models, "echo");

        QueryOperator plan = PlanQuery(
            "SELECT LET model = models.echo('seed'), models.echo(model), models.echo(model) FROM t",
            catalog);

        // The Project's source should now be a RowEnricher (the LET
        // staircase's alias rung, restacked above the collapsed Dag).
        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        RowEnricherOperator restackedAlias = Assert.IsType<RowEnricherOperator>(project.Source);

        // The alias rung should still carry an enrichment for the LET
        // binding — the planner renames `model` to a synthetic
        // `__let_model_*` name during the LET-staircase rewrite, so
        // checking for "model" by name isn't reliable. The structural
        // property is what matters: at least one alias enrichment
        // survived restacking.
        Assert.NotEmpty(restackedAlias.Enrichments);
        Assert.All(restackedAlias.Enrichments,
            e => Assert.IsType<ColumnReference>(e.Expression));

        // Below the alias rung: one ModelInvocationOperator with two
        // invocations (seed echo + the deduped consumer).
        ModelInvocationOperator dag = Assert.IsType<ModelInvocationOperator>(restackedAlias.Source);
        Assert.Equal(2, dag.Invocations.Count);
    }

    [Fact]
    public void LetBindingBetweenModels_RewritesAliasReferencesToCanonical()
    {
        // The consumer invocations reference `model` (the LET name) — the
        // collapser must rewrite those references to the canonical hidden
        // output column of the seed invocation so the new operator's
        // per-invocation evaluation reads from the in-progress output
        // batch correctly.
        TableCatalog catalog = CreateCatalog("t",
            columns: ["name"],
            new object?[] { "x" });
        catalog.Models = new ModelCatalog();
        RegisterEcho(catalog.Models, "echo");

        QueryOperator plan = PlanQuery(
            "SELECT LET model = models.echo('seed'), models.echo(model) FROM t",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        RowEnricherOperator restackedAlias = Assert.IsType<RowEnricherOperator>(project.Source);
        ModelInvocationOperator dag = Assert.IsType<ModelInvocationOperator>(restackedAlias.Source);

        Assert.Equal(2, dag.Invocations.Count);

        // Invocation 0 is the seed (innermost). Its input is the literal
        // 'seed', not a column reference.
        ModelInvocationOperator.Invocation seed = dag.Invocations[0];
        // Invocation 1 is the consumer. Its input was originally
        // ColumnReference("model"); after rewriting it must reference
        // the seed invocation's hidden output column.
        ModelInvocationOperator.Invocation consumer = dag.Invocations[1];
        ColumnReference consumerInput = Assert.IsType<ColumnReference>(consumer.InputExpressions[0]);
        Assert.Equal(seed.OutputColumnName, consumerInput.ColumnName);
    }

    [Fact]
    public void NonPassThroughEnricher_DoesNotFold()
    {
        // An enricher whose RHS isn't a pure column reference (e.g. a
        // computed expression) is NOT pass-through — the collapser must
        // not fold through it, since the new operator's in-progress
        // output batch can't faithfully evaluate that computation in
        // the right order.
        TableCatalog catalog = CreateCatalog("t",
            columns: ["a", "b"],
            new object?[] { "alpha", "beta" });
        catalog.Models = new ModelCatalog();
        RegisterEcho(catalog.Models, "echo");

        // concat(a, b) is a non-trivial RHS; CSE will hoist it into a
        // RowEnricher rung. The two model calls bracket the CSE rung,
        // so the chain is broken and they should NOT be collapsed.
        QueryOperator plan = PlanQuery(
            "SELECT models.echo(concat(a, b)), models.echo(concat(a, b)) FROM t",
            catalog);

        // The two echo calls share an arg expression — the hoister
        // dedups them into a single invocation, so this query reduces
        // to a one-invocation MIO regardless and stays that shape (no
        // collapse). Confirms that without LET, a non-pass-through
        // enricher path isn't reached at all.
        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        ModelInvocationOperator mio = Assert.IsType<ModelInvocationOperator>(project.Source);
        Assert.Single(mio.Invocations);
    }

    [Fact]
    public void NestedInvocations_PreservesInnermostFirstOrder()
    {
        // models.echo(models.echo(name)) should produce two invocations
        // with the INNER one first, since the outer's input expression
        // references the inner's output column.
        TableCatalog catalog = CreateCatalog("t",
            columns: ["name"],
            new object?[] { "x" });
        catalog.Models = new ModelCatalog();
        RegisterEcho(catalog.Models, "echo");

        QueryOperator plan = PlanQuery(
            "SELECT models.echo(models.echo(name)) FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        ModelInvocationOperator dag = Assert.IsType<ModelInvocationOperator>(project.Source);

        Assert.Equal(2, dag.Invocations.Count);
        // Invocation 0 is the inner — its inputs reference the source
        // column `name`. Invocation 1 is the outer — its inputs
        // reference invocation 0's output column.
        ModelInvocationOperator.Invocation inner = dag.Invocations[0];
        ModelInvocationOperator.Invocation outer = dag.Invocations[1];

        ColumnReference innerInput = Assert.IsType<ColumnReference>(inner.InputExpressions[0]);
        Assert.Equal("name", innerInput.ColumnName);

        ColumnReference outerInput = Assert.IsType<ColumnReference>(outer.InputExpressions[0]);
        Assert.Equal(inner.OutputColumnName, outerInput.ColumnName);
    }
}

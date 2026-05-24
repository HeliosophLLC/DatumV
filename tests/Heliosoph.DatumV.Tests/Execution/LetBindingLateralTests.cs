namespace Heliosoph.DatumV.Tests.Execution;

using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

/// <summary>
/// Tests for SELECT-clause LET binding visibility in lateral function-source
/// arguments. Lets a writer say
/// <c>LET classes = models.foo(file), ... FROM t CROSS JOIN unnest(classes) c</c>
/// instead of repeating the model call inside the unnest. The planner lifts the
/// referenced LET into a staircase above the driving source so one
/// <see cref="ModelInvocationOperator"/> feeds both the lateral source and the
/// projection-side LET — giving CSE for free.
/// </summary>
public sealed class LetBindingLateralTests : ServiceTestBase
{
    /// <summary>
    /// Test backend that splits its String input on commas and returns the
    /// pieces as <c>Array&lt;String&gt;</c>. Shared shape with
    /// <c>ModelInvocationTests.SplitModel</c> — re-declared here so this file
    /// can stand alone.
    /// </summary>
    private sealed class SplitModel : IModel
    {
        public string Name => "split";
        public bool IsDeterministic => true;
        public IReadOnlyList<DataKind> InputKinds { get; } = [DataKind.String];
        public DataKind OutputKind => DataKind.String;

        public Task<IReadOnlyList<ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
            CancellationToken cancellationToken)
        {
            _ = overrides;
            ValueRef[] outputs = new ValueRef[inputs.Count];
            for (int row = 0; row < inputs.Count; row++)
            {
                string text = inputs[row][0].AsString();
                string[] pieces = text.Split(',');
                ValueRef[] elements = new ValueRef[pieces.Length];
                for (int i = 0; i < pieces.Length; i++)
                {
                    elements[i] = ValueRef.FromString(pieces[i]);
                }
                outputs[row] = ValueRef.FromArray(DataKind.String, elements);
            }
            return Task.FromResult<IReadOnlyList<ValueRef>>(outputs);
        }
    }

    private static ModelCatalog BuildCatalogWithSplit()
    {
        ModelCatalog catalog = new(modelDirectory: System.IO.Path.GetTempPath());
        catalog.Register(new ModelCatalogEntry(
            Name: "split",
            Backend: "split",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => new SplitModel(),
            OutputIsArray: true));
        return catalog;
    }

    /// <summary>
    /// End-to-end: a LET binding holding a model-returned array can be
    /// referenced inside a lateral <c>unnest</c>. Two driving rows, each
    /// expanded by the unnest, with the projection also reading the LET
    /// binding back as an array-valued column.
    /// </summary>
    [Fact]
    public async Task LetBindingReferencedInLateralUnnest_RunsEndToEnd()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["csv"],
            new object?[] { "a,b,c" },
            new object?[] { "d,e" });
        catalog.Models = BuildCatalogWithSplit();

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET classes = models.split(csv), csv, value " +
            "FROM t " +
            "CROSS JOIN LATERAL unnest(classes)",
            catalog);

        // First, run the canonical inline form to make sure the assertion
        // shape matches the planner's row order. Then assert the LET form
        // produces identical rows.
        List<Row> baselineRows = await ExecuteQueryAsync(
            "SELECT csv, value FROM t CROSS JOIN LATERAL unnest(models.split(csv))",
            catalog);

        Assert.Equal(baselineRows.Count, rows.Count);
        Arena scratch = catalog.Pool.Backing.RentArena();
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(
                (baselineRows[i][0].AsString(scratch), baselineRows[i][1].AsString(scratch)),
                (rows[i][0].AsString(scratch), rows[i][1].AsString(scratch)));
        }
    }

    /// <summary>
    /// Plan-shape: the lateral pass must place the MIO above the driving
    /// source (so it runs once per driving row) and rewrite the unnest's
    /// argument to a column reference, not leave a duplicate model call
    /// inside the lateral source's arguments.
    /// </summary>
    [Fact]
    public void Planner_LiftsLetBindingForLateralUnnest_StaircaseOnLeftSide()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["csv"],
            new object?[] { "a,b" });
        catalog.Models = BuildCatalogWithSplit();

        QueryExpression query = SqlParser.Parse(
            "SELECT LET classes = models.split(csv), csv, value " +
            "FROM t CROSS JOIN LATERAL unnest(classes)");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        QueryOperator plan = planner.Plan(query);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);

        // Walk down past any RowEnricher rungs the projection-side LET
        // handling left in place (the lifted binding becomes a pass-through
        // column ref, which the projection layer may still wrap).
        QueryOperator cursor = project.Source;
        while (cursor is RowEnricherOperator enricher)
        {
            cursor = enricher.Source;
        }

        LateralJoinOperator lateral = Assert.IsType<LateralJoinOperator>(cursor);

        // Left side: MIO (the lifted LET binding) wraps an AliasOperator(t).
        ModelInvocationOperator leftMio = Assert.IsType<ModelInvocationOperator>(lateral.Left);
        Assert.Equal("split", leftMio.ModelName);

        // The MIO's output column name is the lateral-pass synthetic name.
        Assert.Equal("__let_classes_lat", leftMio.OutputColumnName);

        // Right side: unnest's arg has been rewritten to reference the MIO's output.
        AliasOperator rightAlias = Assert.IsType<AliasOperator>(lateral.Right);
        FunctionSourceOperator fnSrc = Assert.IsType<FunctionSourceOperator>(rightAlias.Source);
        Assert.Single(fnSrc.Arguments);
        ColumnReference argCol = Assert.IsType<ColumnReference>(fnSrc.Arguments[0]);
        Assert.Equal("__let_classes_lat", argCol.ColumnName);
    }

    /// <summary>
    /// Chained LETs: <c>LET csv2 = upper(csv), LET parts = models.split(csv2)</c>
    /// with <c>unnest(parts)</c>. Both LETs must lift in dependency order —
    /// the scalar one as a RowEnricher, the model one as a MIO — and only
    /// the model output's synth column should appear in the unnest arg.
    /// </summary>
    [Fact]
    public async Task ChainedLetsBeforeLateralUnnest_LiftInTopoOrder()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["csv"],
            new object?[] { "a,b" });
        catalog.Models = BuildCatalogWithSplit();

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET csv2 = upper(csv), LET parts = models.split(csv2), csv, value " +
            "FROM t " +
            "CROSS JOIN LATERAL unnest(parts)",
            catalog);

        Assert.Equal(2, rows.Count);
        Arena scratch = catalog.Pool.Backing.RentArena();
        Assert.Equal(("a,b", "A"), (rows[0][0].AsString(scratch), rows[0][1].AsString(scratch)));
        Assert.Equal(("a,b", "B"), (rows[1][0].AsString(scratch), rows[1][1].AsString(scratch)));
    }

    /// <summary>
    /// Negative: lateral source with no LET reference should plan exactly as
    /// before the feature — the inline-model hoister still fires, but no
    /// <c>__let_*_lat</c> synthetic column appears. Guards against the
    /// lateral pass firing spuriously when <c>userLetBindings</c> is present
    /// but unreferenced by the lateral source's arguments.
    /// </summary>
    [Fact]
    public void NoLetReferenceInLateralArg_PlanShapeUnchanged()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["csv"],
            new object?[] { "a,b" });
        catalog.Models = BuildCatalogWithSplit();

        QueryExpression query = SqlParser.Parse(
            "SELECT LET parts = models.split(csv), csv, value, parts " +
            "FROM t " +
            "CROSS JOIN LATERAL unnest(models.split(csv))");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        QueryOperator plan = planner.Plan(query);

        // Walk down through the projection-side rungs (Project, optional
        // RowEnricher / ModelInvocation for the LET binding itself) until we
        // hit the lateral. None of the MIOs we pass should have a `_lat`
        // synthetic — that prefix is reserved for the lateral pass.
        QueryOperator cursor = plan;
        while (cursor is not LateralJoinOperator)
        {
            if (cursor is ModelInvocationOperator midMio)
            {
                Assert.False(
                    midMio.OutputColumnName.EndsWith("_lat", System.StringComparison.Ordinal),
                    $"Lateral pass fired spuriously: MIO '{midMio.OutputColumnName}' uses _lat suffix.");
                cursor = midMio.Source;
            }
            else if (cursor is ProjectOperator pr) cursor = pr.Source;
            else if (cursor is RowEnricherOperator re) cursor = re.Source;
            else break;
        }
        LateralJoinOperator lateral = Assert.IsType<LateralJoinOperator>(cursor);
        ModelInvocationOperator leftMio = Assert.IsType<ModelInvocationOperator>(lateral.Left);
        Assert.False(
            leftMio.OutputColumnName.EndsWith("_lat", System.StringComparison.Ordinal),
            $"Lateral pass fired spuriously: MIO '{leftMio.OutputColumnName}' uses _lat suffix on no-LET query.");
    }
}

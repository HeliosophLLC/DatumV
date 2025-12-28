namespace DatumIngest.Tests.Execution;

using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

/// <summary>
/// Phase A smoke tests for the model invocation pipeline:
/// parser namespace lookahead, planner hoist pass, runtime operator dispatch.
/// Uses <see cref="EchoModel"/> as a synthetic backend so the whole architecture
/// can be validated without dragging in ONNX Runtime.
/// </summary>
public sealed class ModelInvocationTests : ServiceTestBase
{
    private static ModelCatalog BuildCatalogWithEcho(IReadOnlyList<DataKind>? optionalArgKinds = null)
    {
        ModelCatalog catalog = new(modelDirectory: System.IO.Path.GetTempPath());
        catalog.Register(new ModelCatalogEntry(
            Name: "echo",
            Backend: "echo",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => EchoModel.Instance,
            OptionalArgKinds: optionalArgKinds));
        return catalog;
    }

    /// <summary>
    /// Parser smoke: <c>models.echo(name)</c> tokenises and parses as a single
    /// <see cref="FunctionCallExpression"/> whose qualified name is <c>"models.echo"</c>.
    /// Confirms the namespace lookahead doesn't fire on a bare <c>name</c> column ref.
    /// </summary>
    [Fact]
    public void Parser_NamespacedFunctionName_FoldsIntoFunctionName()
    {
        QueryExpression q = SqlParser.Parse("SELECT models.echo(name) FROM t");
        SelectQueryExpression sqe = Assert.IsType<SelectQueryExpression>(q);
        FunctionCallExpression fn = Assert.IsType<FunctionCallExpression>(
            sqe.Statement.Columns[0].Expression);
        Assert.Equal("models.echo", fn.FunctionName);
        Assert.Single(fn.Arguments);
        Assert.IsType<ColumnReference>(fn.Arguments[0]);
    }

    /// <summary>
    /// <c>t.col</c> still parses as a <see cref="ColumnReference"/> — namespace
    /// lookahead must backtrack when no <c>(</c> follows the second identifier.
    /// </summary>
    [Fact]
    public void Parser_QualifiedColumn_StillParsesAsColumnReference()
    {
        QueryExpression q = SqlParser.Parse("SELECT t.col FROM t");
        SelectQueryExpression sqe = Assert.IsType<SelectQueryExpression>(q);
        ColumnReference col = Assert.IsType<ColumnReference>(
            sqe.Statement.Columns[0].Expression);
        Assert.Equal("t", col.TableName);
        Assert.Equal("col", col.ColumnName);
    }

    /// <summary>
    /// Planner hoists <c>models.echo(name)</c> out of the project expression and
    /// replaces it with a column reference to a synthesised name. The resulting
    /// plan tree has <c>Project &gt; ModelInvocation &gt; Scan</c> shape.
    /// </summary>
    [Fact]
    public void Planner_HoistsModelCall_OutOfProject()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" },
            new object?[] { "bob" });
        catalog.Models = BuildCatalogWithEcho();

        QueryExpression query = SqlParser.Parse("SELECT models.echo(name) FROM t");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        IQueryOperator plan = planner.Plan(query);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        ModelInvocationOperator invocation = Assert.IsType<ModelInvocationOperator>(project.Source);
        Assert.Equal("echo", invocation.ModelName);
        Assert.Single(invocation.InputExpressions);
        Assert.StartsWith("__model_echo_", invocation.OutputColumnName);
    }

    /// <summary>
    /// End-to-end: <c>SELECT models.echo(name) FROM t</c> dispatches through the
    /// EchoModel backend and returns each input string unchanged.
    /// </summary>
    [Fact]
    public async Task EndToEnd_EchoModel_ReturnsInputUnchanged()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" },
            new object?[] { "bob" },
            new object?[] { "carol" });
        catalog.Models = BuildCatalogWithEcho();

        List<Row> rows = await ExecuteQueryAsync("SELECT models.echo(name) FROM t", catalog);

        Assert.Equal(3, rows.Count);
        // Each row's single output column carries the model's echoed string.
        Arena scratch = catalog.Pool.Backing.RentArena();
        Assert.Equal("alice", rows[0][0].AsString(scratch));
        Assert.Equal("bob", rows[1][0].AsString(scratch));
        Assert.Equal("carol", rows[2][0].AsString(scratch));
    }

    /// <summary>
    /// Hoister accepts a trailing positional override when the catalog entry
    /// declares an <c>OptionalArgKinds</c> slot for it. The first <em>required</em>
    /// arg ends up in <see cref="ModelInvocationOperator.InputExpressions"/>;
    /// trailing optional args land in
    /// <see cref="ModelInvocationOperator.OptionalExpressions"/>.
    /// </summary>
    [Fact]
    public void Planner_HoistsModelCallWithOptionalArg()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" });
        catalog.Models = BuildCatalogWithEcho(optionalArgKinds: [DataKind.Float64]);

        QueryExpression query = SqlParser.Parse("SELECT models.echo(name, 0.5) FROM t");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        IQueryOperator plan = planner.Plan(query);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        ModelInvocationOperator invocation = Assert.IsType<ModelInvocationOperator>(project.Source);
        Assert.Single(invocation.InputExpressions);
        Assert.Single(invocation.OptionalExpressions);
    }

    /// <summary>
    /// Nested model calls hoist in post-order: the inner call's MIO must end
    /// up closer to the scan than the outer's so the outer can reference the
    /// inner's synthesised output column. Plus the outer call's input
    /// expressions must have nested model-call references rewritten to
    /// <see cref="ColumnReference"/>s — otherwise MIO's runtime evaluator
    /// throws "Unknown function: 'models.X'" because models.* isn't in the
    /// scalar function registry.
    /// </summary>
    [Fact]
    public async Task Planner_NestedModelCalls_HoistInCorrectOrder()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" },
            new object?[] { "bob" });
        catalog.Models = BuildCatalogWithEcho();

        // Outer echo wraps the inner echo's output. With the bug, the outer's
        // MIO would receive `'X: ' || models.echo(name)` — a raw model call
        // node — and fail at runtime. Post-order hoist + arg rewrite makes
        // the outer's MIO see `'X: ' || <ColRef to inner's output>` instead.
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT models.echo(concat('X: ', models.echo(name))) FROM t", catalog);

        Assert.Equal(2, rows.Count);
        Arena scratch = catalog.Pool.Backing.RentArena();
        Assert.Equal("X: alice", rows[0][0].AsString(scratch));
        Assert.Equal("X: bob", rows[1][0].AsString(scratch));
    }

    /// <summary>
    /// Hoister rejects a call with more args than the entry's required + optional
    /// declared count. Catches typos and stale signatures at plan time rather
    /// than dispatching them silently.
    /// </summary>
    [Fact]
    public void Planner_RejectsCallExceedingOptionalArity()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" });
        catalog.Models = BuildCatalogWithEcho(optionalArgKinds: [DataKind.Float64]);

        QueryExpression query = SqlParser.Parse("SELECT models.echo(name, 0.5, 100) FROM t");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => planner.Plan(query));
        Assert.Contains("at most", ex.Message);
    }

    /// <summary>
    /// Counting model that records every input it sees, so a test can assert
    /// the model was invoked exactly N times — not just that N rows came back.
    /// Without this distinction the original LIMIT test passed even when
    /// MIO ran the model on every source row and let LIMIT discard the rest.
    /// </summary>
    private sealed class CountingEchoModel : DatumIngest.Models.IModel
    {
        public List<string> SeenInputs { get; } = new();
        public string Name => "counting_echo";
        public bool IsDeterministic => true;
        public IReadOnlyList<DataKind> InputKinds { get; } = [DataKind.String];
        public DataKind OutputKind => DataKind.String;

        public Task<IReadOnlyList<DatumIngest.Functions.ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<DatumIngest.Functions.ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<DatumIngest.Functions.ValueRef>> overrides,
            CancellationToken cancellationToken)
        {
            _ = overrides;
            DatumIngest.Functions.ValueRef[] outputs = new DatumIngest.Functions.ValueRef[inputs.Count];
            for (int row = 0; row < inputs.Count; row++)
            {
                DatumIngest.Functions.ValueRef value = inputs[row][0];
                string text = value.AsString();
                SeenInputs.Add(text);
                outputs[row] = DatumIngest.Functions.ValueRef.FromString(text);
            }
            return Task.FromResult<IReadOnlyList<DatumIngest.Functions.ValueRef>>(outputs);
        }
    }

    /// <summary>
    /// LIMIT N above a model invocation must invoke the model EXACTLY N times,
    /// not "process the whole upstream batch and let LIMIT discard the rest."
    /// For expensive operators (LLMs) the latter is a real cost regression.
    /// We verify by registering a counting model and asserting the recorded
    /// invocation count matches the LIMIT.
    /// </summary>
    [Fact]
    public async Task EndToEnd_LimitAboveModel_InvokesModelExactlyLimitTimes()
    {
        CountingEchoModel counter = new();
        ModelCatalog modelCatalog = new(modelDirectory: System.IO.Path.GetTempPath());
        modelCatalog.Register(new ModelCatalogEntry(
            Name: "counting_echo",
            Backend: "echo",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => counter));

        // 50 source rows to exceed any reasonable batch size and force the
        // LIMIT cap to actually clamp work mid-batch.
        object?[][] rows = new object?[50][];
        for (int i = 0; i < rows.Length; i++) rows[i] = new object?[] { $"row_{i}" };
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            rows);
        catalog.Models = modelCatalog;

        List<Row> result = await ExecuteQueryAsync(
            "SELECT models.counting_echo(name) FROM t LIMIT 7", catalog);

        Assert.Equal(7, result.Count);
        Assert.Equal(7, counter.SeenInputs.Count);
    }

    /// <summary>
    /// LIMIT applied above a model invocation should still allow the engine to
    /// stop after N rows have been produced. The model is dispatched per upstream
    /// batch — the LIMIT downstream stops requesting batches once it has its rows.
    /// </summary>
    [Fact]
    public async Task EndToEnd_LimitAboveModel_StopsAfterRequestedRows()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" },
            new object?[] { "bob" },
            new object?[] { "carol" },
            new object?[] { "dave" },
            new object?[] { "erin" });
        catalog.Models = BuildCatalogWithEcho();

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT models.echo(name) FROM t LIMIT 2", catalog);

        Assert.Equal(2, rows.Count);
    }

    /// <summary>
    /// Two textually-identical <c>models.echo(...)</c> calls in the same SELECT
    /// list dedupe by structural fingerprint into a single
    /// <see cref="ModelInvocationOperator"/>. Both projection columns reference
    /// the same hidden output column, so the model dispatches once per batch.
    /// Per the inference-integration convention: same call site → one eval.
    /// </summary>
    [Fact]
    public void Planner_TwoIdenticalModelCalls_HoistOnce()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" });
        catalog.Models = BuildCatalogWithEcho();

        QueryExpression query = SqlParser.Parse(
            "SELECT models.echo('test'), models.echo('test') FROM t");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        IQueryOperator plan = planner.Plan(query);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        ModelInvocationOperator invocation = Assert.IsType<ModelInvocationOperator>(project.Source);

        // Source of MIO is the scan itself — only ONE MIO in the chain.
        Assert.IsNotType<ModelInvocationOperator>(invocation.Source);

        // Both projection columns reference the same synthesised model output.
        Assert.Equal(2, project.Columns.Count);
        ColumnReference c0 = Assert.IsType<ColumnReference>(project.Columns[0].Expression);
        ColumnReference c1 = Assert.IsType<ColumnReference>(project.Columns[1].Expression);
        Assert.Equal(c0.ColumnName, c1.ColumnName);
        Assert.Equal(invocation.OutputColumnName, c0.ColumnName);
    }

    /// <summary>
    /// <c>SELECT *, models.echo(name) FROM t</c> must not duplicate the model's output column.
    /// The hoister inserts a ModelInvocationOperator above the scan, so the operator stream feeding
    /// the projection contains <c>name</c> + the synthesised model column. Without a planner-side
    /// rewrite, <c>SELECT *</c> includes BOTH (giving the model column once via <c>*</c>), then the
    /// explicit projection adds it again — the row ends up with three columns, two of them identical
    /// model output. The fix excludes hoisted synthetic columns from <c>*</c> so the row has exactly
    /// the user-visible columns: <c>name</c> + one model output.
    /// </summary>
    [Fact]
    public async Task EndToEnd_StarPlusModelCall_DoesNotDuplicateModelColumn()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" },
            new object?[] { "bob" });
        catalog.Models = BuildCatalogWithEcho();

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT *, models.echo(name) FROM t", catalog);

        Assert.Equal(2, rows.Count);

        // Exactly two output columns: the source `name` and one model output column.
        // With the bug, FieldCount is 3 because `*` re-emits the hoisted column.
        Assert.Equal(2, rows[0].FieldCount);

        Arena scratch = catalog.Pool.Backing.RentArena();
        Assert.Equal("alice", rows[0][0].AsString(scratch));
        Assert.Equal("alice", rows[0][1].AsString(scratch));
    }

    /// <summary>
    /// Different literal arguments produce different fingerprints — the hoister
    /// keeps them as separate operators. Catches a false-positive dedup that
    /// would conflate <c>models.echo('a')</c> with <c>models.echo('b')</c>.
    /// </summary>
    [Fact]
    public void Planner_DifferentLiteralArgs_HoistSeparately()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" });
        catalog.Models = BuildCatalogWithEcho();

        QueryExpression query = SqlParser.Parse(
            "SELECT models.echo('a'), models.echo('b') FROM t");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        IQueryOperator plan = planner.Plan(query);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);

        // Two MIOs stacked above the scan — outermost first, then inner.
        ModelInvocationOperator outer = Assert.IsType<ModelInvocationOperator>(project.Source);
        ModelInvocationOperator inner = Assert.IsType<ModelInvocationOperator>(outer.Source);
        Assert.NotEqual(outer.OutputColumnName, inner.OutputColumnName);

        // Each projection column references one of the two distinct outputs.
        ColumnReference c0 = Assert.IsType<ColumnReference>(project.Columns[0].Expression);
        ColumnReference c1 = Assert.IsType<ColumnReference>(project.Columns[1].Expression);
        Assert.NotEqual(c0.ColumnName, c1.ColumnName);
    }

    /// <summary>
    /// When a projection contains both LET bindings and at least one
    /// <c>models.*</c> call, the planner's LET-staircase pass lifts every LET
    /// binding into its own upstream rung. For <c>LET v = models.echo(name)</c>
    /// this produces a <see cref="ModelInvocationOperator"/> rung
    /// (<c>__model_echo_*</c>) followed by a <see cref="RowEnricherOperator"/>
    /// rung that aliases the model's column under the binding's hidden name
    /// (<c>__let_v_*</c>). The projection's <c>LetBindings</c> ends up empty.
    /// </summary>
    [Fact]
    public void Planner_HoistsModelCallInLetBody()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" });
        catalog.Models = BuildCatalogWithEcho();

        QueryExpression query = SqlParser.Parse(
            "SELECT LET v = models.echo(name), v FROM t");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        IQueryOperator plan = planner.Plan(query);

        // Plan shape: Project ← Enricher(__let_v_*) ← MIO(echo) ← Scan.
        // LET binding has been lifted out of the projection.
        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        Assert.Null(project.LetBindings);

        RowEnricherOperator enricher = Assert.IsType<RowEnricherOperator>(project.Source);
        Assert.Single(enricher.Enrichments);
        Assert.StartsWith("__let_v_", enricher.Enrichments[0].ColumnName);

        ModelInvocationOperator invocation = Assert.IsType<ModelInvocationOperator>(enricher.Source);
        Assert.Equal("echo", invocation.ModelName);
    }

    /// <summary>
    /// Model call inside a WHERE predicate hoists upstream of the filter.
    /// Plan shape: Scan → MIO → Filter → Project. The filter's predicate
    /// then operates on the hoisted column rather than re-invoking the model.
    /// </summary>
    [Fact]
    public void Planner_HoistsModelCallInWhere()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" });
        catalog.Models = BuildCatalogWithEcho();

        QueryExpression query = SqlParser.Parse(
            "SELECT name FROM t WHERE upper(models.echo(name)) = 'ALICE'");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        IQueryOperator plan = planner.Plan(query);

        // Find the FilterOperator in the chain.
        FilterOperator? filter = null;
        IQueryOperator? cursor = plan;
        while (cursor is not null)
        {
            if (cursor is FilterOperator f) { filter = f; break; }
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                ModelInvocationOperator m => m.Source,
                _ => null,
            };
        }
        Assert.NotNull(filter);

        // Filter's source is a ModelInvocationOperator (the hoisted echo call).
        ModelInvocationOperator invocation = Assert.IsType<ModelInvocationOperator>(filter!.Source);
        Assert.Equal("echo", invocation.ModelName);

        // The WHERE predicate's deepest model call is gone — replaced with a
        // ColumnReference. We don't pin the precise predicate shape (parser
        // sugar is fragile), just that no models.* call survives in it.
        Assert.DoesNotContain("models.echo", QueryExplainer.FormatExpression(filter.Predicate));
    }

    /// <summary>
    /// Model call inside an ORDER BY item hoists upstream of the sort. Plan
    /// shape: Scan → MIO → OrderBy → Project. The comparator works against
    /// the pre-computed hoisted column.
    /// </summary>
    [Fact]
    public void Planner_HoistsModelCallInOrderBy()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" });
        catalog.Models = BuildCatalogWithEcho();

        QueryExpression query = SqlParser.Parse(
            "SELECT name FROM t ORDER BY upper(models.echo(name))");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        IQueryOperator plan = planner.Plan(query);

        // Walk the plan looking for the OrderByOperator.
        OrderByOperator? orderBy = null;
        IQueryOperator? cursor = plan;
        while (cursor is not null)
        {
            if (cursor is OrderByOperator ob) { orderBy = ob; break; }
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                FilterOperator f => f.Source,
                ModelInvocationOperator m => m.Source,
                _ => null,
            };
        }
        Assert.NotNull(orderBy);

        // OrderBy's source is the hoisted MIO.
        ModelInvocationOperator invocation = Assert.IsType<ModelInvocationOperator>(orderBy!.Source);
        Assert.Equal("echo", invocation.ModelName);

        // No models.* survives in the order-by item expressions.
        foreach (OrderByItem item in orderBy.OrderByItems)
        {
            Assert.DoesNotContain("models.echo", QueryExplainer.FormatExpression(item.Expression));
        }
    }

    /// <summary>
    /// End-to-end: a model call in WHERE filters rows correctly. Echo returns
    /// the input string; <c>upper()</c> upper-cases it; the filter keeps rows
    /// matching the literal. The hoister places the MIO upstream of the
    /// filter, so the WHERE evaluator sees a column reference rather than
    /// a model call.
    /// </summary>
    [Fact]
    public async Task EndToEnd_ModelCallInWhere_FiltersByModelOutput()
    {
        CountingEchoModel counter = new();
        ModelCatalog modelCatalog = new(modelDirectory: System.IO.Path.GetTempPath());
        modelCatalog.Register(new ModelCatalogEntry(
            Name: "counting_echo",
            Backend: "echo",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => counter));

        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" },
            new object?[] { "bob" },
            new object?[] { "carol" });
        catalog.Models = modelCatalog;

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT name FROM t WHERE upper(models.counting_echo(name)) = 'ALICE'",
            catalog);

        Assert.Single(rows);
        Arena scratch = catalog.Pool.Backing.RentArena();
        try
        {
            Assert.Equal("alice", rows[0]["name"].AsString(scratch));
        }
        finally { catalog.Pool.Backing.TryReturn(scratch); }

        // Model invoked once per source row (3 rows = 3 invocations).
        Assert.Equal(3, counter.SeenInputs.Count);
    }

    /// <summary>
    /// End-to-end: model call in LET body works just like in projection. The
    /// LET name resolves to the MIO's hidden column on every row.
    /// </summary>
    [Fact]
    public async Task EndToEnd_ModelCallInLetBody_RoundTrips()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" },
            new object?[] { "bob" });
        catalog.Models = BuildCatalogWithEcho();

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET v = models.echo(name), v FROM t", catalog);

        Assert.Equal(2, rows.Count);
        Arena scratch = catalog.Pool.Backing.RentArena();
        try
        {
            Assert.Equal("alice", rows[0]["v"].AsString(scratch));
            Assert.Equal("bob", rows[1]["v"].AsString(scratch));
        }
        finally { catalog.Pool.Backing.TryReturn(scratch); }
    }

    /// <summary>
    /// Cross-clause: same model call in WHERE and SELECT shares a single
    /// <see cref="ModelInvocationOperator"/>, placed upstream of the filter so
    /// both clauses see the hoisted column. The model dispatches once per row.
    /// </summary>
    [Fact]
    public async Task EndToEnd_CrossClause_WhereAndSelect_RunsModelOncePerRow()
    {
        CountingEchoModel counter = new();
        ModelCatalog modelCatalog = new(modelDirectory: System.IO.Path.GetTempPath());
        modelCatalog.Register(new ModelCatalogEntry(
            Name: "counting_echo",
            Backend: "echo",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => counter));

        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" },
            new object?[] { "bob" },
            new object?[] { "carol" });
        catalog.Models = modelCatalog;

        // The same call appears in both clauses. Cross-clause stage hoists once;
        // both references resolve to the shared hidden column.
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT models.counting_echo(name) AS echoed FROM t WHERE upper(models.counting_echo(name)) = 'ALICE'",
            catalog);

        Assert.Single(rows);
        Arena scratch = catalog.Pool.Backing.RentArena();
        try
        {
            Assert.Equal("alice", rows[0]["echoed"].AsString(scratch));
        }
        finally { catalog.Pool.Backing.TryReturn(scratch); }

        // Three source rows × one cross-clause-deduped call = three invocations,
        // not six. Without the cross-clause pass we'd see a separate MIO above
        // FilterOperator and a second above ProjectOperator.
        Assert.Equal(3, counter.SeenInputs.Count);
    }

    /// <summary>
    /// Plan-shape check for cross-clause WHERE+SELECT: exactly one
    /// <see cref="ModelInvocationOperator"/> in the chain, placed upstream of
    /// FilterOperator (deepest referencing position). Filter and Project both
    /// reference the same hidden column.
    /// </summary>
    [Fact]
    public void Planner_CrossClauseWhereSelect_HoistsOnceUpstreamOfFilter()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" });
        catalog.Models = BuildCatalogWithEcho();

        QueryExpression query = SqlParser.Parse(
            "SELECT models.echo(name) FROM t WHERE upper(models.echo(name)) = 'ALICE'");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        IQueryOperator plan = planner.Plan(query);

        // Walk the plan, count MIOs.
        int mioCount = 0;
        ModelInvocationOperator? deepestMio = null;
        IQueryOperator? cursor = plan;
        while (cursor is not null)
        {
            if (cursor is ModelInvocationOperator m)
            {
                mioCount++;
                deepestMio = m;
            }
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                FilterOperator f => f.Source,
                ModelInvocationOperator mm => mm.Source,
                _ => null,
            };
        }

        // Exactly one MIO — the cross-clause hoist made the duplicate disappear.
        Assert.Equal(1, mioCount);
        Assert.NotNull(deepestMio);
    }

    /// <summary>
    /// Cross-clause across SELECT and ORDER BY: model call appears in a
    /// projected column AND in an ORDER BY item. Single MIO, placed
    /// upstream of the OrderByOperator (the deepest reference).
    /// </summary>
    [Fact]
    public void Planner_CrossClauseSelectOrderBy_HoistsOnce()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" });
        catalog.Models = BuildCatalogWithEcho();

        QueryExpression query = SqlParser.Parse(
            "SELECT models.echo(name) FROM t ORDER BY models.echo(name)");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        IQueryOperator plan = planner.Plan(query);

        int mioCount = 0;
        IQueryOperator? cursor = plan;
        while (cursor is not null)
        {
            if (cursor is ModelInvocationOperator) mioCount++;
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                FilterOperator f => f.Source,
                OrderByOperator ob => ob.Source,
                ModelInvocationOperator mm => mm.Source,
                _ => null,
            };
        }

        Assert.Equal(1, mioCount);
    }

    /// <summary>
    /// Cross-clause sanity: when the same call appears in WHERE and SELECT
    /// AND in a LET binding all together, all three sites unify into one MIO.
    /// LET + projection-column + filter predicate, four textual occurrences,
    /// one canonical operator.
    /// </summary>
    [Fact]
    public async Task EndToEnd_CrossClause_LetAndSelectAndWhere_RunsOnce()
    {
        CountingEchoModel counter = new();
        ModelCatalog modelCatalog = new(modelDirectory: System.IO.Path.GetTempPath());
        modelCatalog.Register(new ModelCatalogEntry(
            Name: "counting_echo",
            Backend: "echo",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => counter));

        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" },
            new object?[] { "bob" });
        catalog.Models = modelCatalog;

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET v = models.counting_echo(name), v AS via_let, models.counting_echo(name) AS direct " +
            "FROM t WHERE upper(models.counting_echo(name)) IN ('ALICE', 'BOB')",
            catalog);

        Assert.Equal(2, rows.Count);

        // Two source rows × one cross-clause hoist = two invocations.
        Assert.Equal(2, counter.SeenInputs.Count);
    }

    /// <summary>
    /// End-to-end: <c>SELECT models.x(name), models.x(name) FROM t</c> invokes
    /// the model exactly once per row — verifies the structural dedup actually
    /// reaches the runtime, not just the plan shape.
    /// </summary>
    [Fact]
    public async Task EndToEnd_DuplicateModelCall_RunsModelOncePerRow()
    {
        CountingEchoModel counter = new();
        ModelCatalog modelCatalog = new(modelDirectory: System.IO.Path.GetTempPath());
        modelCatalog.Register(new ModelCatalogEntry(
            Name: "counting_echo",
            Backend: "echo",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => counter));

        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" },
            new object?[] { "bob" },
            new object?[] { "carol" });
        catalog.Models = modelCatalog;

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT models.counting_echo(name), models.counting_echo(name) FROM t",
            catalog);

        Assert.Equal(3, rows.Count);
        // 3 source rows, two textual call sites — should still be 3 invocations,
        // not 6. That's the property the structural dedup guarantees.
        Assert.Equal(3, counter.SeenInputs.Count);

        // Both columns hold the echoed value for each row.
        Arena scratch = catalog.Pool.Backing.RentArena();
        try
        {
            Assert.Equal("alice", rows[0][0].AsString(scratch));
            Assert.Equal("alice", rows[0][1].AsString(scratch));
            Assert.Equal("bob", rows[1][0].AsString(scratch));
            Assert.Equal("bob", rows[1][1].AsString(scratch));
        }
        finally { catalog.Pool.Backing.TryReturn(scratch); }
    }

    /// <summary>
    /// Mock model emitting <c>Array&lt;Struct{score: Float32, label: String}&gt;</c>
    /// per row — mirrors SCRFD's shape (multiple detections per image, each with
    /// a struct payload). The test verifies the operator stamps the *array*
    /// TypeId on the per-row DataValue, not the *element struct* TypeId. Without
    /// the fix, descriptor lookups went straight to the element struct
    /// descriptor (Fields populated, IsArray=false), and downstream
    /// <c>ResolveElementTypeId</c> in the evaluator/formatters returned 0
    /// because <c>desc.IsArray</c> was false — producing the f0..fN regression.
    /// </summary>
    private sealed class ArrayStructEchoModel : DatumIngest.Models.IModel
    {
        public string Name => "array_struct_echo";
        public bool IsDeterministic => true;
        public IReadOnlyList<DataKind> InputKinds { get; } = [DataKind.String];
        public DataKind OutputKind => DataKind.Struct;
        public IReadOnlyList<ColumnInfo>? OutputFields { get; } =
        [
            new ColumnInfo("score", DataKind.Float32, nullable: false),
            new ColumnInfo("label", DataKind.String, nullable: false),
        ];

        public Task<IReadOnlyList<DatumIngest.Functions.ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<DatumIngest.Functions.ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<DatumIngest.Functions.ValueRef>> overrides,
            CancellationToken cancellationToken)
        {
            _ = overrides;
            DatumIngest.Functions.ValueRef[] outputs =
                new DatumIngest.Functions.ValueRef[inputs.Count];
            for (int row = 0; row < inputs.Count; row++)
            {
                // Two detections per row to force the InArena layout (N>=2)
                // so TypeId actually rides along — N=1 inline arrays strip it.
                DatumIngest.Functions.ValueRef d0 = DatumIngest.Functions.ValueRef.FromStruct(
                [
                    DatumIngest.Functions.ValueRef.FromFloat32(0.9f),
                    DatumIngest.Functions.ValueRef.FromString("first"),
                ]);
                DatumIngest.Functions.ValueRef d1 = DatumIngest.Functions.ValueRef.FromStruct(
                [
                    DatumIngest.Functions.ValueRef.FromFloat32(0.7f),
                    DatumIngest.Functions.ValueRef.FromString("second"),
                ]);
                outputs[row] = DatumIngest.Functions.ValueRef.FromArray(
                    DataKind.Struct, [d0, d1]);
            }
            return Task.FromResult<IReadOnlyList<DatumIngest.Functions.ValueRef>>(outputs);
        }
    }

    [Fact]
    public async Task EndToEnd_ArrayOfStructOutput_StampsArrayTypeIdNotElementTypeId()
    {
        // The bug: operator passed the element struct's TypeId to
        // ToDataValue for the per-row Array<Struct> value, so the resulting
        // DataValue carried a struct (Fields-populated) descriptor instead of
        // an array (IsArray=true, ElementTypeId=struct) descriptor. Renderers
        // and the evaluator's index-access path both rely on
        // `desc.IsArray && desc.ElementTypeId` — without an array descriptor,
        // they fall back to f0..fN. Fix: lazily intern Array<Struct> when the
        // model's per-row value is an array, then stamp THAT TypeId.
        ArrayStructEchoModel model = new();
        ModelCatalog modelCatalog = new(modelDirectory: System.IO.Path.GetTempPath());
        modelCatalog.Register(new ModelCatalogEntry(
            Name: "array_struct_echo",
            Backend: "echo",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.Struct,
            IsDeterministic: true,
            Loader: _ => model));

        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" });
        catalog.Models = modelCatalog;

        // Drive through ExecuteQueryAsync with our own context so we can
        // inspect the registry after execution.
        DatumIngest.Execution.ExecutionContext context = CreateExecutionContext(catalog: catalog);
        QueryExpression query = SqlParser.Parse("SELECT models.array_struct_echo(name) FROM t");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        IQueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);
        List<Row> rows = await plan.CollectRowsAsync(context);

        Assert.Single(rows);
        DataValue arrayValue = rows[0][0];
        Assert.Equal(DataKind.Struct, arrayValue.Kind);
        Assert.True(arrayValue.IsArray);
        Assert.NotEqual((ushort)0, arrayValue.TypeId);

        // The stamped TypeId must be the *array* descriptor — IsArray=true and
        // ElementTypeId pointing at the element struct shape.
        TypeDescriptor? arrayDesc = context.Types.GetDescriptor(arrayValue.TypeId);
        Assert.NotNull(arrayDesc);
        Assert.True(arrayDesc.IsArray);
        Assert.NotNull(arrayDesc.ElementTypeId);

        TypeDescriptor? elementDesc = context.Types.GetDescriptor(arrayDesc.ElementTypeId.Value);
        Assert.NotNull(elementDesc);
        Assert.Equal(DataKind.Struct, elementDesc.Kind);
        Assert.False(elementDesc.IsArray);
        Assert.NotNull(elementDesc.Fields);
        Assert.Equal(2, elementDesc.Fields.Count);
        Assert.Equal("score", elementDesc.Fields[0].Name);
        Assert.Equal("label", elementDesc.Fields[1].Name);
    }
}

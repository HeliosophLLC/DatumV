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
    /// Model calls inside <c>LET</c> binding bodies hoist out of the project
    /// just like calls in column expressions. Plan shape: Scan → MIO → Project,
    /// where the LET binding's expression is rewritten to a ColumnReference
    /// pointing at the MIO's hidden output.
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

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        ModelInvocationOperator invocation = Assert.IsType<ModelInvocationOperator>(project.Source);
        Assert.Equal("echo", invocation.ModelName);

        // LET body now references the MIO's hidden column.
        Assert.NotNull(project.LetBindings);
        Assert.Single(project.LetBindings);
        ColumnReference letRef = Assert.IsType<ColumnReference>(project.LetBindings![0].Expression);
        Assert.Equal(invocation.OutputColumnName, letRef.ColumnName);
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
}

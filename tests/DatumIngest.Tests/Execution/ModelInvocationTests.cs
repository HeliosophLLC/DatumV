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
}

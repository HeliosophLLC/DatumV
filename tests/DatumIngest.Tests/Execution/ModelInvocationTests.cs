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
    /// <see cref="FunctionCallExpression"/> whose <see cref="FunctionCallExpression.SchemaName"/>
    /// is <c>"models"</c> and bare <see cref="FunctionCallExpression.FunctionName"/>
    /// is <c>"echo"</c>. Confirms the namespace lookahead doesn't fire on a
    /// bare <c>name</c> column ref.
    /// </summary>
    [Fact]
    public void Parser_NamespacedFunctionName_SplitsSchemaAndName()
    {
        QueryExpression q = SqlParser.Parse("SELECT models.echo(name) FROM t");
        SelectQueryExpression sqe = Assert.IsType<SelectQueryExpression>(q);
        FunctionCallExpression fn = Assert.IsType<FunctionCallExpression>(
            sqe.Statement.Columns[0].Expression);
        Assert.Equal("models", fn.SchemaName);
        Assert.Equal("echo", fn.FunctionName);
        Assert.Equal("models.echo", fn.CallName);
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
    /// Test backend that declares <c>[Float64, Float64]</c> inputs and records
    /// the actual <see cref="DataKind"/> + value of each input it sees. Lets
    /// tests assert the operator's coercion at the model-call boundary
    /// produces values whose kind matches the declared signature, not the
    /// kind the SQL literal happened to bind to.
    /// </summary>
    private sealed class Float64ProbeModel : DatumIngest.Models.IModel
    {
        public List<(DataKind kind, double value)> SeenArg0 { get; } = new();
        public List<(DataKind kind, double value)> SeenArg1 { get; } = new();
        public string Name => "float64_probe";
        public bool IsDeterministic => true;
        public IReadOnlyList<DataKind> InputKinds { get; } = [DataKind.Float64, DataKind.Float64];
        public DataKind OutputKind => DataKind.Float64;

        public Task<IReadOnlyList<DatumIngest.Functions.ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<DatumIngest.Functions.ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<DatumIngest.Functions.ValueRef>> overrides,
            CancellationToken cancellationToken)
        {
            _ = overrides;
            DatumIngest.Functions.ValueRef[] outputs = new DatumIngest.Functions.ValueRef[inputs.Count];
            for (int row = 0; row < inputs.Count; row++)
            {
                DatumIngest.Functions.ValueRef a = inputs[row][0];
                DatumIngest.Functions.ValueRef b = inputs[row][1];
                SeenArg0.Add((a.Kind, a.AsFloat64()));
                SeenArg1.Add((b.Kind, b.AsFloat64()));
                outputs[row] = DatumIngest.Functions.ValueRef.FromFloat64(a.AsFloat64() + b.AsFloat64());
            }
            return Task.FromResult<IReadOnlyList<DatumIngest.Functions.ValueRef>>(outputs);
        }
    }

    /// <summary>
    /// Integer literals in SQL bind to the smallest integer kind that fits
    /// (Int16 for <c>300</c>); when a model declares Float64 inputs, the
    /// operator must auto-widen at the call boundary so the model's
    /// <c>AsFloat64()</c> accessor doesn't throw "Cannot read Int16 as Float64".
    /// Verifies both kind (Float64 reaches the model) and value (no
    /// truncation through the conversion).
    /// </summary>
    [Fact]
    public async Task EndToEnd_NumericLiteralsCoerceToDeclaredFloat64InputKind()
    {
        Float64ProbeModel probe = new();
        ModelCatalog modelCatalog = new(modelDirectory: System.IO.Path.GetTempPath());
        modelCatalog.Register(new ModelCatalogEntry(
            Name: "float64_probe",
            Backend: "test",
            RelativePath: null,
            InputKinds: [DataKind.Float64, DataKind.Float64],
            OutputKind: DataKind.Float64,
            IsDeterministic: true,
            Loader: _ => probe));

        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["id"],
            new object?[] { 1 });
        catalog.Models = modelCatalog;

        List<Row> result = await ExecuteQueryAsync(
            "SELECT models.float64_probe(300, 300) FROM t", catalog);

        Assert.Single(result);
        Assert.Single(probe.SeenArg0);
        Assert.Equal(DataKind.Float64, probe.SeenArg0[0].kind);
        Assert.Equal(DataKind.Float64, probe.SeenArg1[0].kind);
        Assert.Equal(300.0, probe.SeenArg0[0].value);
        Assert.Equal(300.0, probe.SeenArg1[0].value);
    }

    /// <summary>
    /// Synthetic model that returns <c>Array&lt;Image&gt;</c> — N tiny
    /// solid-colour PNGs per row, with N taken from the input column. Used
    /// to exercise the engine's array-of-image output path without
    /// needing the real MobileSAM ONNX files. MobileSAM is the first
    /// engine consumer of <c>Array&lt;Image&gt;</c>; this probe pins the
    /// path so a regression in
    /// <c>ValueRef.ToDataValue → BuildImageArray</c> or the operator's
    /// scatter step shows up in fast unit tests rather than only in the
    /// model-files-required MobileSAM smoke tests.
    /// </summary>
    private sealed class ImageArrayProbeModel : DatumIngest.Models.IModel
    {
        public string Name => "image_array_probe";
        public bool IsDeterministic => true;
        public IReadOnlyList<DataKind> InputKinds { get; } = [DataKind.Int32];
        public DataKind OutputKind => DataKind.Image;

        public Task<IReadOnlyList<DatumIngest.Functions.ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<DatumIngest.Functions.ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<DatumIngest.Functions.ValueRef>> overrides,
            CancellationToken cancellationToken)
        {
            _ = overrides;
            DatumIngest.Functions.ValueRef[] outputs = new DatumIngest.Functions.ValueRef[inputs.Count];
            for (int row = 0; row < inputs.Count; row++)
            {
                int count = inputs[row][0].AsInt32();
                DatumIngest.Functions.ValueRef[] images = new DatumIngest.Functions.ValueRef[count];
                for (int i = 0; i < count; i++)
                {
                    SkiaSharp.SKBitmap bmp = new(
                        16, 16,
                        SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque);
                    bmp.Erase(new SkiaSharp.SKColor(
                        red: (byte)(row * 30 + i * 7),
                        green: 128,
                        blue: 64));
                    images[i] = DatumIngest.Functions.ValueRef.FromImage(bmp);
                }
                outputs[row] = DatumIngest.Functions.ValueRef.FromArray(DataKind.Image, images);
            }
            return Task.FromResult<IReadOnlyList<DatumIngest.Functions.ValueRef>>(outputs);
        }
    }

    /// <summary>
    /// End-to-end SQL through the model-invocation operator with an
    /// <c>Array&lt;Image&gt;</c>-returning model: verifies the operator's
    /// scatter step calls <c>ValueRef.ToDataValue</c> with the right
    /// arena, the resulting <c>DataValue</c> survives in the per-query
    /// arena past plan execution, and <c>AsImageArray</c> reads back
    /// the encoded PNG bytes for every element. Without this test the
    /// arena materialisation step for image arrays is uncovered —
    /// MobileSAM-everything's <c>Array&lt;Image&gt;</c> output would be
    /// the first time the path runs in production.
    /// </summary>
    [Fact]
    public async Task EndToEnd_ImageArrayModel_PersistsThroughArena()
    {
        ImageArrayProbeModel probe = new();
        ModelCatalog modelCatalog = new(modelDirectory: System.IO.Path.GetTempPath());
        modelCatalog.Register(new ModelCatalogEntry(
            Name: "image_array_probe",
            Backend: "test",
            RelativePath: null,
            InputKinds: [DataKind.Int32],
            OutputKind: DataKind.Image,
            IsDeterministic: true,
            Loader: _ => probe));

        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["count"],
            new object?[] { 1 },   // 1-element array
            new object?[] { 3 },   // 3-element array
            new object?[] { 5 });  // 5-element array — exercises the multi-slot block path
        catalog.Models = modelCatalog;

        // Pass an explicit retention store. CollectRowsAsync (the test
        // helper that drives the plan) stabilises every row's payload
        // into context.Store so values survive past the producing
        // RowBatch's arena being returned to the pool. Reads of arena-
        // backed payloads must use that same store as the resolution
        // arena, otherwise they walk into a freshly-pooled arena and
        // throw "Arena[#N] has not been allocated".
        Pool pool = GetService<Pool>();
        Arena retention = pool.Backing.RentArena();
        try
        {
            List<Row> rows = await ExecuteQueryAsync(
                "SELECT count, models.image_array_probe(count) FROM t", catalog, store: retention);

            Assert.Equal(3, rows.Count);

            int[] expectedCounts = [1, 3, 5];
            for (int r = 0; r < rows.Count; r++)
            {
                DataValue arrayValue = rows[r][1];
                Assert.True(arrayValue.IsArray,
                    $"row[{r}] result should be an array, got Kind={arrayValue.Kind}, IsArray={arrayValue.IsArray}.");
                Assert.Equal(DataKind.Image, arrayValue.Kind);

                byte[][] elements = arrayValue.AsImageArray(retention);
                Assert.Equal(expectedCounts[r], elements.Length);

                for (int i = 0; i < elements.Length; i++)
                {
                    Assert.True(elements[i].Length > 0,
                        $"row[{r}].element[{i}] arena bytes are empty.");
                    using SkiaSharp.SKBitmap decoded = SkiaSharp.SKBitmap.Decode(elements[i]);
                    Assert.NotNull(decoded);
                    Assert.Equal(16, decoded.Width);
                    Assert.Equal(16, decoded.Height);
                }
            }
        }
        finally
        {
            pool.Backing.TryReturn(retention);
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
    public async Task EndToEnd_ArrayOfStructOutput_PerElementTypeIdStampedOnRows()
    {
        // After the per-element TypeId layout, the array container itself no
        // longer carries a TypeId — every element row is self-describing via
        // its slot's reserved bytes. The test verifies that each emitted
        // element stamps the model's element struct shape, recoverable through
        // the in-flight registry without any container-side ElementTypeId hop.
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

        DatumIngest.Execution.ExecutionContext context = CreateExecutionContext(catalog: catalog);
        QueryExpression query = SqlParser.Parse("SELECT models.array_struct_echo(name) FROM t");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        IQueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);

        // Stream the result so the per-batch arena stays alive while we read
        // AsStructArray. Capture only the registry-resolved fields we need to
        // assert on — they survive the batch's lifetime since they live on
        // managed TypeDescriptor objects.
        bool sawRow = false;
        ushort capturedElementTypeId = 0;
        TypeDescriptor? capturedElementDesc = null;
        await foreach (RowBatch batch in plan.ExecuteAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                sawRow = true;
                DataValue arrayValue = batch[i][0];
                Assert.Equal(DataKind.Struct, arrayValue.Kind);
                Assert.True(arrayValue.IsArray);
                // Container TypeId is intentionally 0 — elements carry shape,
                // not the container.
                Assert.Equal((ushort)0, arrayValue.TypeId);

                DataValue[] elements = arrayValue.AsStructArray(batch.Arena);
                Assert.Equal(2, elements.Length);
                Assert.All(elements, e => Assert.Equal(DataKind.Struct, e.Kind));
                capturedElementTypeId = elements[0].TypeId;
                Assert.NotEqual((ushort)0, capturedElementTypeId);
                Assert.All(elements, e => Assert.Equal(capturedElementTypeId, e.TypeId));
                capturedElementDesc = context.Types.GetDescriptor(capturedElementTypeId);
            }
        }

        Assert.True(sawRow);
        Assert.NotNull(capturedElementDesc);
        Assert.Equal(DataKind.Struct, capturedElementDesc.Kind);
        Assert.False(capturedElementDesc.IsArray);
        Assert.NotNull(capturedElementDesc.Fields);
        Assert.Equal(2, capturedElementDesc.Fields.Count);
        Assert.Equal("score", capturedElementDesc.Fields[0].Name);
        Assert.Equal("label", capturedElementDesc.Fields[1].Name);
    }
}

using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Inference;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// End-to-end tests for PG-style named function-call arguments.
/// Exercises the parser → <c>NamedArgPermuter</c> planner pass → scalar
/// dispatch pipeline against built-in scalar functions and procedural
/// UDFs registered via <c>CREATE FUNCTION</c>.
/// </summary>
public sealed class NamedArgumentExecutionTests : ServiceTestBase
{
    private static async Task<List<DataValue>> CollectFirstColumnAsync(StatementPlan plan)
    {
        List<DataValue> values = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0]);
            }
        }
        return values;
    }

    /// <summary>
    /// Built-in <c>assert_true</c> declares <c>(condition, message)</c>
    /// where the second slot is optional. Calling with the message
    /// supplied by name validates the simplest pass-through case.
    /// </summary>
    [Fact]
    public async Task AssertTrue_NamedMessage_PassesThroughTruthyValue()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"], new object?[] { 1 });

        StatementPlan plan = catalog.Plan(
            "SELECT assert_true(true, message := 'ok') AS r FROM t");
        List<DataValue> values = await CollectFirstColumnAsync(plan);

        Assert.Single(values);
        Assert.True(values[0].AsBoolean());
    }

    /// <summary>
    /// Fat-arrow equivalent of the named-message case. Same call
    /// semantics; the parser tolerates both operators.
    /// </summary>
    [Fact]
    public async Task AssertTrue_FatArrowMessage_PassesThrough()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"], new object?[] { 1 });

        StatementPlan plan = catalog.Plan(
            "SELECT assert_true(true, message => 'ok') AS r FROM t");
        List<DataValue> values = await CollectFirstColumnAsync(plan);

        Assert.Single(values);
        Assert.True(values[0].AsBoolean());
    }

    /// <summary>
    /// Both arguments supplied by name. Permutation must place
    /// <c>condition</c> in slot 0 and <c>message</c> in slot 1 regardless
    /// of source-order — the call site below intentionally lists
    /// <c>message</c> first.
    /// </summary>
    [Fact]
    public async Task AssertTrue_AllNamed_ReordersToPositional()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"], new object?[] { 1 });

        StatementPlan plan = catalog.Plan(
            "SELECT assert_true(message := 'ok', condition := true) AS r FROM t");
        List<DataValue> values = await CollectFirstColumnAsync(plan);

        Assert.Single(values);
        Assert.True(values[0].AsBoolean());
    }

    /// <summary>
    /// Unknown parameter name surfaces the planner-pass diagnostic with
    /// the call site name, not an opaque "Unknown function" or runtime
    /// arity error.
    /// </summary>
    [Fact]
    public void AssertTrue_UnknownParameterName_ThrowsWithSpecificMessage()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"], new object?[] { 1 });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("SELECT assert_true(true, unknown_param := 'x') AS r FROM t"));

        Assert.Contains("assert_true", ex.Message);
        Assert.Contains("unknown_param", ex.Message);
    }

    /// <summary>
    /// Positional arguments must precede every named argument. The
    /// planner pass rejects the inverse ordering — surfacing a precise
    /// "positional after named" message rather than relying on
    /// downstream dispatch to misinterpret the call.
    /// </summary>
    [Fact]
    public void AssertTrue_PositionalAfterNamed_ThrowsWithSpecificMessage()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"], new object?[] { 1 });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("SELECT assert_true(message := 'x', true) AS r FROM t"));

        Assert.Contains("positional", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Procedural UDFs reach the runtime via the synthetic descriptor
    /// registered by <see cref="Heliosoph.DatumV.Catalog.RoutineRegistrar"/>;
    /// the permuter must recognise that descriptor and reorder named
    /// args accordingly.
    /// </summary>
    [Fact]
    public async Task ProceduralUdf_AllNamed_ReordersAccordingToParameterNames()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"], new object?[] { 1 });
        catalog.Plan(
            "CREATE FUNCTION subtract(a INT32, b INT32) RETURNS INT32 BEGIN RETURN a - b END");

        StatementPlan positional = catalog.Plan("SELECT subtract(10, 3) AS r FROM t");
        List<DataValue> positionalValues = await CollectFirstColumnAsync(positional);
        Assert.Equal(7, positionalValues[0].AsInt32());

        StatementPlan reordered = catalog.Plan("SELECT subtract(b := 3, a := 10) AS r FROM t");
        List<DataValue> reorderedValues = await CollectFirstColumnAsync(reordered);
        Assert.Equal(7, reorderedValues[0].AsInt32());
    }

    /// <summary>
    /// When a procedural UDF parameter has a <c>DEFAULT</c> and the
    /// caller skips it via named-arg ordering, the planner pass must
    /// inject the default's AST fragment — not a NULL literal — into
    /// the omitted slot so the body sees the declared fallback value.
    /// </summary>
    [Fact]
    public async Task ProceduralUdf_NamedSkipUsesParameterDefault()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"], new object?[] { 1 });
        catalog.Plan(
            "CREATE FUNCTION add_with_default(a INT32, b INT32 = 100) RETURNS INT32 BEGIN RETURN a + b END");

        StatementPlan plan = catalog.Plan("SELECT add_with_default(a := 5) AS r FROM t");
        List<DataValue> values = await CollectFirstColumnAsync(plan);

        Assert.Equal(105, values[0].AsInt32());
    }

    // ─── SQL-defined model named arguments ────────────────────────────
    //
    // Models declared via CREATE MODEL register a synthetic scalar
    // FunctionDescriptor (IsOptional set from the parameter's DEFAULT) but
    // carry their default AST on ModelDescriptor.Parameters[i].Default in a
    // separate registry from UDFs. The permuter must consult that registry
    // when filling skipped middle slots — otherwise a positional + named
    // call that skips a defaulted middle param NULL-fills it, and the
    // model body sees NULL instead of the declared default. These use
    // delegating models (no USING clause), so no ONNX Runtime / infer() is
    // needed — the body computes a scalar directly from its parameters,
    // making the injected defaults observable in the result.

    private TableCatalog CreateCatalogWithModels()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"], new object?[] { 1 });
        catalog.InferenceDispatcher = new NoopDispatcher();
        return catalog;
    }

    /// <summary>
    /// Primary repro: a positional arg plus one trailing named arg skips a
    /// defaulted middle parameter. Pre-fix the permuter NULL-filled the
    /// middle slot; the fix injects the model's declared default so the
    /// body sees 100, not NULL.
    /// </summary>
    [Fact]
    public async Task Model_NamedSkip_MiddleSlot_UsesParameterDefault()
    {
        TableCatalog catalog = CreateCatalogWithModels();
        catalog.Plan(
            "CREATE MODEL sum3(a Int32, b Int32 = 100, c Int32 = 7) RETURNS Int32 "
            + "AS BEGIN RETURN a + b + c END");

        StatementPlan plan = catalog.Plan("SELECT models.sum3(1, c := 3) AS r FROM t");
        List<DataValue> values = await CollectFirstColumnAsync(plan);

        Assert.Equal(104, values[0].AsInt32()); // 1 + 100(default) + 3
    }

    /// <summary>
    /// The exact shape of the reported bug: two skipped middle defaults
    /// (steps, guidance) between a positional prompt-stand-in and a trailing
    /// named arg (seed). Both defaults must flow through — including the
    /// Float32 default — while the supplied trailing arg is honoured.
    /// </summary>
    [Fact]
    public async Task Model_NamedSkip_MultipleMiddleSlots_UseDefaults()
    {
        TableCatalog catalog = CreateCatalogWithModels();
        catalog.Plan(
            "CREATE MODEL cfg(a Int32, steps Int32 = 25, "
            + "guidance Float32 = CAST(7.5 AS Float32), seed Int64 = NULL) RETURNS Float32 "
            + "AS BEGIN RETURN CAST(steps AS Float32) + guidance "
            + "+ CAST(COALESCE(seed, CAST(0 AS Int64)) AS Float32) END");

        StatementPlan plan = catalog.Plan("SELECT models.cfg(1, seed := 10) AS r FROM t");
        List<DataValue> values = await CollectFirstColumnAsync(plan);

        Assert.Equal(42.5f, values[0].AsFloat32(), 4); // 25 + 7.5 + 10
    }

    /// <summary>
    /// A parameter whose default is <c>NULL</c> stays NULL when skipped in
    /// a middle slot — injecting the model's default reproduces exactly the
    /// NULL the old NULL-fill produced, and no spurious IS NOT NULL
    /// assertion fires. Here <c>seed</c> is a middle slot (a trailing
    /// <c>tail</c> arg is supplied by name).
    /// </summary>
    [Fact]
    public async Task Model_NamedSkip_NullDefaultParam_StaysNull()
    {
        TableCatalog catalog = CreateCatalogWithModels();
        catalog.Plan(
            "CREATE MODEL seeded(a Int32, seed Int64 = NULL, tail Int32 = 9) RETURNS Int64 "
            + "AS BEGIN RETURN COALESCE(seed, CAST(-1 AS Int64)) + CAST(tail AS Int64) END");

        StatementPlan plan = catalog.Plan("SELECT models.seeded(1, tail := 3) AS r FROM t");
        List<DataValue> values = await CollectFirstColumnAsync(plan);

        Assert.Equal(2L, values[0].AsInt64()); // COALESCE(NULL, -1) + 3
    }

    /// <summary>
    /// Regression guard for the form that already worked: supplying every
    /// argument by name still binds correctly, so the model-default
    /// injection doesn't disturb the all-named path.
    /// </summary>
    [Fact]
    public async Task Model_AllNamed_StillReordersCorrectly()
    {
        TableCatalog catalog = CreateCatalogWithModels();
        catalog.Plan(
            "CREATE MODEL sum3b(a Int32, b Int32 = 100, c Int32 = 7) RETURNS Int32 "
            + "AS BEGIN RETURN a + b + c END");

        StatementPlan plan = catalog.Plan(
            "SELECT models.sum3b(c := 3, a := 1, b := 2) AS r FROM t");
        List<DataValue> values = await CollectFirstColumnAsync(plan);

        Assert.Equal(6, values[0].AsInt32()); // 1 + 2 + 3, no defaults used
    }

    /// <summary>
    /// Minimal <see cref="IInferenceDispatcher"/> that satisfies the
    /// CREATE MODEL registration-time non-null check. Delegating models
    /// (no USING clause) never load a bundle, so LoadBundleAsync is never
    /// reached — it throws to make any accidental call obvious.
    /// </summary>
    private sealed class NoopDispatcher : IInferenceDispatcher
    {
        public IReadOnlyList<IInferenceBackend> Backends => Array.Empty<IInferenceBackend>();

        public ValueTask<IReadOnlyDictionary<string, IModelSession>> LoadBundleAsync(
            BundleManifest bundle,
            InferencePreferences preferences,
            CancellationToken cancellationToken)
            => throw new NotSupportedException(
                "Delegating models declare no USING clause and never load a bundle.");
    }

    // ─── TVF named arguments in FROM ──────────────────────────────────
    //
    // Same parser → NamedArgPermuter → dispatch pipeline as scalar
    // calls, except the call site is a TableSource (FunctionSource)
    // rather than a FunctionCallExpression. Exercised against
    // generate_series — a numeric three-arg TVF with an optional step
    // and stable PG-compatible parameter names (start, stop, step).

    private static async Task<List<DataValue>> CollectColumnAsync(
        TableCatalog catalog, string sql, string column)
    {
        StatementPlan plan = catalog.Plan(sql);
        List<DataValue> values = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][column]);
            }
        }
        return values;
    }

    /// <summary>
    /// Two named args, source-order matching declared parameter order.
    /// The TVF must receive (0, 4) and yield 0..4 inclusive.
    /// </summary>
    [Fact]
    public async Task Tvf_AllNamedInDeclaredOrder_YieldsPositionalRange()
    {
        TableCatalog catalog = CreateCatalog();
        List<DataValue> values = await CollectColumnAsync(catalog,
            "SELECT value FROM generate_series(start := 0, stop := 4)", "value");

        Assert.Equal(5, values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            Assert.Equal(i, values[i].AsInt32());
        }
    }

    /// <summary>
    /// Same call, names supplied in reverse source order. The permuter
    /// must place stop in slot 1 and start in slot 0 regardless of
    /// textual ordering — otherwise generate_series would interpret
    /// (4, 0) as an empty range.
    /// </summary>
    [Fact]
    public async Task Tvf_AllNamedReversed_PermutesToDeclaredOrder()
    {
        TableCatalog catalog = CreateCatalog();
        List<DataValue> values = await CollectColumnAsync(catalog,
            "SELECT value FROM generate_series(stop := 4, start := 0)", "value");

        Assert.Equal(5, values.Count);
        Assert.Equal(0, values[0].AsInt32());
        Assert.Equal(4, values[4].AsInt32());
    }

    /// <summary>
    /// Positional prefix + trailing named arg. Skipping the optional
    /// middle slot is the case the MS MARCO recipe needs — call
    /// open_csv_typed(path, header := FALSE) without supplying
    /// skip_lines/comment/null_token in between. generate_series stands
    /// in here with the same shape: (start, stop, step) and step
    /// optional.
    /// </summary>
    [Fact]
    public async Task Tvf_PositionalPlusTrailingNamed_RangeUsesStep()
    {
        TableCatalog catalog = CreateCatalog();
        List<DataValue> values = await CollectColumnAsync(catalog,
            "SELECT value FROM generate_series(0, 6, step := 2)", "value");

        Assert.Equal(4, values.Count);
        Assert.Equal(0, values[0].AsInt32());
        Assert.Equal(2, values[1].AsInt32());
        Assert.Equal(4, values[2].AsInt32());
        Assert.Equal(6, values[3].AsInt32());
    }

    /// <summary>
    /// Unknown parameter name surfaces the permuter's diagnostic with
    /// the call-site function name and the offending parameter, same
    /// shape as the scalar path.
    /// </summary>
    [Fact]
    public void Tvf_UnknownParameterName_ThrowsWithSpecificMessage()
    {
        TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("SELECT * FROM generate_series(start := 0, nope := 4)"));

        Assert.Contains("generate_series", ex.Message);
        Assert.Contains("nope", ex.Message);
    }
}

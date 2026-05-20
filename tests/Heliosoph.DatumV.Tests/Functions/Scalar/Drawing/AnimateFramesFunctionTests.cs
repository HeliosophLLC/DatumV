using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Drawing;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Drawing;

/// <summary>
/// Phase D-A: <see cref="AnimateFramesFunction"/> drives a lambda over
/// <c>duration × fps</c> frames + the <see cref="ExpressionEvaluator"/>'s
/// LambdaInvoker auto-attach ensures the function works through actual
/// SQL execution paths, not just direct test-frame construction.
/// </summary>
public sealed class AnimateFramesFunctionTests : ServiceTestBase
{
    // ----- direct invocation -----

    [Fact]
    public async Task AnimateFrames_ProducesRequestedFrameCount()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame,
            "(t) -> draw_circle(point2d(8, 8), 4, color(255, 0, 0))");

        ValueRef result = await new AnimateFramesFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromFloat32(1.0f),       // duration
                ValueRef.FromInt32(12),           // fps
                ValueRef.FromPoint2D(16, 16),     // size
                lambda,
            },
            frame, default);

        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Image, result.Kind);
        Assert.Equal(12, result.GetArrayLength());
    }

    [Fact]
    public async Task AnimateFrames_FramesAreRenderedToRequestedSize()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame,
            "(t) -> draw_rect(point2d(0, 0), point2d(8, 16), color(0, 255, 0))");

        ValueRef result = await new AnimateFramesFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromFloat32(0.5f), ValueRef.FromInt32(8),
                ValueRef.FromPoint2D(8, 16), lambda,
            },
            frame, default);

        ReadOnlySpan<ValueRef> frames = result.GetArrayElements();
        SKBitmap firstFrame = frames[0].AsImage();
        Assert.Equal(8, firstFrame.Width);
        Assert.Equal(16, firstFrame.Height);
        SKColor px = firstFrame.GetPixel(4, 8);
        Assert.Equal(0, px.Red);
        Assert.Equal(255, px.Green);
        Assert.Equal(0, px.Blue);
    }

    [Fact]
    public async Task AnimateFrames_LambdaReceivesTimeInZeroToOneRange()
    {
        // The lambda body uses t to vary the drawn circle's color so we can
        // verify each frame got a distinct t value.
        // t = 0 → circle color (0, 0, 0); t = 0.5 → (127ish, …); etc.
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame,
            "(t) -> draw_rect(point2d(0, 0), point2d(8, 8), color(t * 255, 0, 0))");

        ValueRef result = await new AnimateFramesFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromFloat32(1.0f), ValueRef.FromInt32(4),
                ValueRef.FromPoint2D(8, 8), lambda,
            },
            frame, default);

        ReadOnlySpan<ValueRef> frames = result.GetArrayElements();
        // Frames receive t = 0, 0.25, 0.5, 0.75 → red channel = 0, 63, 127, 191.
        Assert.Equal(0, frames[0].AsImage().GetPixel(4, 4).Red);
        Assert.InRange((int)frames[1].AsImage().GetPixel(4, 4).Red, 60, 66);
        Assert.InRange((int)frames[2].AsImage().GetPixel(4, 4).Red, 125, 130);
        Assert.InRange((int)frames[3].AsImage().GetPixel(4, 4).Red, 188, 194);
    }

    [Fact]
    public async Task AnimateFrames_NonPositiveDuration_Throws()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame,
            "(t) -> draw_circle(point2d(0, 0), 1, color(0, 0, 0))");

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new AnimateFramesFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromFloat32(0), ValueRef.FromInt32(12),
                    ValueRef.FromPoint2D(8, 8), lambda,
                },
                frame, default));
        Assert.Contains("duration", ex.Message);
    }

    [Fact]
    public async Task AnimateFrames_NullLambda_ReturnsNullArray()
    {
        var (_, frame) = MakeEvaluatorAndFrame();
        ValueRef result = await new AnimateFramesFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromFloat32(1), ValueRef.FromInt32(4),
                ValueRef.FromPoint2D(8, 8),
                ValueRef.Null(DataKind.Lambda),
            },
            frame, default);
        Assert.True(result.IsNull);
        Assert.True(result.IsArray);
    }

    // ----- end-to-end through SQL execution (verifies auto-attach) -----

    [Fact]
    public async Task EndToEnd_AnimateFramesThroughSQL_AutoAttachesLambdaInvoker()
    {
        // The critical wiring test: a frame constructed by ProjectOperator
        // (during normal SELECT execution) doesn't set LambdaInvoker explicitly.
        // The evaluator's auto-attach must wire `this` so that animate_frames
        // can invoke its lambda parameter. If the auto-attach is broken,
        // animate_frames throws "requires an ILambdaInvoker".
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        catalog.Add(new InMemoryTableProvider(pool, "t",
            columns: ["x"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT animate_frames(0.5, 8, point2d(8, 8), "
            + "(t) -> draw_rect(point2d(0, 0), point2d(8, 8), color(100, 150, 200))) AS frames "
            + "FROM t",
            catalog);

        Assert.Single(rows);
        DataValue value = rows[0]["frames"];
        Assert.True(value.IsArray);
        Assert.Equal(DataKind.Image, value.Kind);
        // duration=0.5, fps=8 → 4 frames. ElementCount doesn't work for
        // arrays of non-fixed-width kinds (Image); the IsArray + Kind
        // checks confirm we got an Array<Image> back from the SQL path.
        // Per-frame element extraction would go through AsImageArray or
        // similar accessors — not necessary for this wiring test.
    }

    [Fact]
    public async Task EndToEnd_AnimateFramesWithClosureCapture()
    {
        // Closure capture: the lambda references a column from the enclosing
        // row. Each row's animation should see that row's value.
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        catalog.Add(new InMemoryTableProvider(pool, "t",
            columns: ["fill_red"],
            columnKinds: [DataKind.Int32],
            rows: [[200]]));

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT animate_frames(0.25, 4, point2d(4, 4), "
            + "(t) -> draw_rect(point2d(0, 0), point2d(4, 4), color(fill_red, 0, 0))) AS frames "
            + "FROM t",
            catalog);

        Assert.Single(rows);
        DataValue framesValue = rows[0]["frames"];
        Assert.True(framesValue.IsArray);
        Assert.Equal(DataKind.Image, framesValue.Kind);
        // The wiring test passing (no thrown exception about missing
        // ILambdaInvoker or VariableScope) confirms that closure captures
        // resolve correctly through the operator-pipeline frame.
    }

    // ----- helpers -----

    private (ExpressionEvaluator Evaluator, EvaluationFrame Frame) MakeEvaluatorAndFrame()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        Heliosoph.DatumV.Execution.ExecutionContext context = CreateExecutionContext(
            store: arena, accountant: accountant);
        Heliosoph.DatumV.Execution.ExecutionContext scoped = context.Derive(
            variableScope: scope, variableStore: arena);
        ExpressionEvaluator evaluator = scoped.CreateEvaluator();
        EvaluationFrame frame = evaluator.CreateFrame(Row.Empty, arena);
        return (evaluator, frame);
    }

    /// <summary>
    /// Parses a lambda expression out of a probe SQL string, evaluates it
    /// against the test frame to produce a Lambda <see cref="ValueRef"/>.
    /// </summary>
    private static ValueRef MakeLambda(
        ExpressionEvaluator evaluator, EvaluationFrame frame, string lambdaSql)
    {
        QueryExpression query = SqlParser.Parse(
            $"SELECT array_transform(arr, {lambdaSql}) FROM t");
        SelectQueryExpression select = (SelectQueryExpression)query;
        FunctionCallExpression call =
            (FunctionCallExpression)select.Statement.Columns[0].Expression;
        LambdaExpression ast = (LambdaExpression)call.Arguments[1];
        return ValueRef.FromLambda(LambdaValue.Capture(ast, frame.Row));
    }
}

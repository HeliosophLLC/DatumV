using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Audio;
using Heliosoph.DatumV.Functions.Scalar.Drawing;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Audio;

/// <summary>
/// <c>audio_waveform_drawing(envelope, render)</c>: walks a precomputed
/// (bins, 2) envelope, invokes a WaveformContext-scoped lambda per bin, and
/// groups the resulting Drawings. Validates the per-bin lambda invocation,
/// the (t, min, max) parameter binding, null + shape handling, and the
/// SQL-execution auto-attach for ILambdaInvoker.
/// </summary>
public sealed class AudioWaveformDrawingFunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_ReturnsDrawing()
    {
        AudioWaveformDrawingFunction fn = new();
        DataKind kind = ((IScalarFunction)fn).ValidateArguments([DataKind.Float32, DataKind.Lambda]);
        Assert.Equal(DataKind.Drawing, kind);
    }

    [Fact]
    public async Task NullEnvelope_ReturnsNullDrawing()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame,
            "(t, lo, hi) -> draw_rect(point2d(0, 0), point2d(1, 1), color(0, 0, 0))");

        ValueRef result = await new AudioWaveformDrawingFunction().ExecuteAsync(
            new[] { ValueRef.NullArray(DataKind.Float32), lambda },
            frame, default);

        Assert.Equal(DataKind.Drawing, result.Kind);
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task NullLambda_ReturnsNullDrawing()
    {
        var (_, frame) = MakeEvaluatorAndFrame();
        ValueRef envelope = ValueRef.FromPrimitiveMultiDimArray(
            new float[] { -0.5f, 0.5f, -0.25f, 0.25f }, [2, 2], DataKind.Float32);

        ValueRef result = await new AudioWaveformDrawingFunction().ExecuteAsync(
            new[] { envelope, ValueRef.Null(DataKind.Lambda) },
            frame, default);

        Assert.Equal(DataKind.Drawing, result.Kind);
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task ValidEnvelope_ProducesGroupDrawingWithBinChildren()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame,
            "(t, lo, hi) -> draw_rect(point2d(0, 0), point2d(1, 1), color(0, 0, 0))");
        ValueRef envelope = ValueRef.FromPrimitiveMultiDimArray(
            new float[] { -0.5f, 0.5f, -0.25f, 0.25f, -0.1f, 0.1f }, [3, 2], DataKind.Float32);

        ValueRef result = await new AudioWaveformDrawingFunction().ExecuteAsync(
            new[] { envelope, lambda }, frame, default);

        Assert.Equal(DataKind.Drawing, result.Kind);
        DrawingPayload payload = result.AsDrawing();
        GroupDrawing group = Assert.IsType<GroupDrawing>(payload);
        Assert.Equal(3, group.Children.Length);
    }

    [Fact]
    public async Task LambdaReceives_t_min_max_AsExpected()
    {
        // Encode (t, min, max) into the rectangle's geometry so we can read
        // them back per-child after the group renders:
        //   Position.X = t * 1000      → recovers t in [0, 1000]
        //   Position.Y = min * 1000    → recovers min in [-1000, 1000]
        //   Size.Width = max * 1000    → recovers max in [-1000, 1000]
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame,
            "(t, lo, hi) -> draw_rect(point2d(t * 1000, lo * 1000), point2d(hi * 1000, 1), color(0, 0, 0))");

        ValueRef envelope = ValueRef.FromPrimitiveMultiDimArray(
            new float[]
            {
                -0.5f, 0.7f,   // bin 0: t = 0, lo = -0.5, hi = 0.7
                -0.2f, 0.3f,   // bin 1: t = 0.5
                -0.9f, 0.1f,   // bin 2: t = 1.0
            },
            [3, 2], DataKind.Float32);

        ValueRef result = await new AudioWaveformDrawingFunction().ExecuteAsync(
            new[] { envelope, lambda }, frame, default);

        GroupDrawing group = (GroupDrawing)result.AsDrawing();

        ShapeDrawing s0 = (ShapeDrawing)group.Children[0];
        Assert.Equal(0f,     s0.Position.X, 3);   // t = 0
        Assert.Equal(-500f,  s0.Position.Y, 3);   // min = -0.5
        Assert.Equal(700f,   s0.Size.Width, 3);   // max = 0.7

        ShapeDrawing s1 = (ShapeDrawing)group.Children[1];
        Assert.Equal(500f,   s1.Position.X, 3);   // t = 0.5
        Assert.Equal(-200f,  s1.Position.Y, 3);
        Assert.Equal(300f,   s1.Size.Width, 3);

        ShapeDrawing s2 = (ShapeDrawing)group.Children[2];
        Assert.Equal(1000f,  s2.Position.X, 3);   // t = 1.0 (endpoint-inclusive)
        Assert.Equal(-900f,  s2.Position.Y, 3);
        Assert.Equal(100f,   s2.Size.Width, 3);
    }

    [Fact]
    public async Task SingleBin_LambdaReceivesTimeZero()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame,
            "(t, lo, hi) -> draw_rect(point2d(t * 1000, 0), point2d(1, 1), color(0, 0, 0))");

        ValueRef envelope = ValueRef.FromPrimitiveMultiDimArray(
            new float[] { -0.3f, 0.3f }, [1, 2], DataKind.Float32);

        ValueRef result = await new AudioWaveformDrawingFunction().ExecuteAsync(
            new[] { envelope, lambda }, frame, default);

        GroupDrawing group = (GroupDrawing)result.AsDrawing();
        ShapeDrawing single = (ShapeDrawing)Assert.Single(group.Children);
        Assert.Equal(0f, single.Position.X, 3);
    }

    [Fact]
    public async Task FlatEnvelope_NonMultiDim_Throws()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame,
            "(t, lo, hi) -> draw_rect(point2d(0, 0), point2d(1, 1), color(0, 0, 0))");
        // 1-D Float32 array — no shape attachment, can't be interpreted as
        // (bins, 2). The function should reject with a pointer at the
        // canonical producer.
        ValueRef envelope = ValueRef.FromPrimitiveArray(new float[] { 0f, 1f, 2f, 3f }, DataKind.Float32);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new AudioWaveformDrawingFunction().ExecuteAsync(
                new[] { envelope, lambda }, frame, default));
        Assert.Contains("audio_waveform_envelope", ex.Message);
    }

    [Fact]
    public async Task WrongSecondDim_Throws()
    {
        // Shape (bins, 3) instead of (bins, 2).
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame,
            "(t, lo, hi) -> draw_rect(point2d(0, 0), point2d(1, 1), color(0, 0, 0))");
        ValueRef envelope = ValueRef.FromPrimitiveMultiDimArray(
            new float[] { 0f, 1f, 2f, 3f, 4f, 5f }, [2, 3], DataKind.Float32);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new AudioWaveformDrawingFunction().ExecuteAsync(
                new[] { envelope, lambda }, frame, default));
        Assert.Contains("(bins, 2)", ex.Message);
    }

    // ───────────────────────── Helpers ─────────────────────────

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

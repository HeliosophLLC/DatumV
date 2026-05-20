using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Audio;
using Heliosoph.DatumV.Functions.Scalar.Drawing;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Audio;

/// <summary>
/// <c>audio_waveform_path(envelope, width, height, fill)</c>: builds a
/// single closed filled <see cref="PathDrawing"/> tracing the waveform's
/// top and bottom edges. Validates the command shape (move + top edge +
/// bottom edge + close), pixel-space layout, null + shape handling.
/// </summary>
public sealed class AudioWaveformPathFunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_ReturnsDrawing()
    {
        AudioWaveformPathFunction fn = new();
        DataKind kind = ((IScalarFunction)fn).ValidateArguments(
            [DataKind.Float32, DataKind.Int32, DataKind.Int32, DataKind.Color]);
        Assert.Equal(DataKind.Drawing, kind);
    }

    [Fact]
    public async Task NullEnvelope_ReturnsNullDrawing()
    {
        EvaluationFrame frame = CreateEvaluationFrame();
        ValueRef result = await new AudioWaveformPathFunction().ExecuteAsync(
            new[]
            {
                ValueRef.NullArray(DataKind.Float32),
                ValueRef.FromInt32(100),
                ValueRef.FromInt32(50),
                ValueRef.FromColor(255, 0, 0, 255),
            },
            frame, default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Drawing, result.Kind);
    }

    [Fact]
    public async Task NullFill_ReturnsNullDrawing()
    {
        EvaluationFrame frame = CreateEvaluationFrame();
        ValueRef envelope = ValueRef.FromPrimitiveMultiDimArray(
            new float[] { -0.5f, 0.5f, -0.25f, 0.25f }, [2, 2], DataKind.Float32);

        ValueRef result = await new AudioWaveformPathFunction().ExecuteAsync(
            new[]
            {
                envelope,
                ValueRef.FromInt32(100),
                ValueRef.FromInt32(50),
                ValueRef.Null(DataKind.Color),
            },
            frame, default);

        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task ZeroWidth_Throws()
    {
        EvaluationFrame frame = CreateEvaluationFrame();
        ValueRef envelope = ValueRef.FromPrimitiveMultiDimArray(
            new float[] { -0.5f, 0.5f }, [1, 2], DataKind.Float32);

        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new AudioWaveformPathFunction().ExecuteAsync(
                new[]
                {
                    envelope, ValueRef.FromInt32(0), ValueRef.FromInt32(50),
                    ValueRef.FromColor(255, 0, 0, 255),
                },
                frame, default));
    }

    [Fact]
    public async Task ValidEnvelope_ProducesPathDrawingWithFill()
    {
        EvaluationFrame frame = CreateEvaluationFrame();
        ValueRef envelope = ValueRef.FromPrimitiveMultiDimArray(
            new float[] { -0.5f, 0.5f, -0.25f, 0.25f, -0.1f, 0.1f }, [3, 2], DataKind.Float32);

        ValueRef result = await new AudioWaveformPathFunction().ExecuteAsync(
            new[]
            {
                envelope, ValueRef.FromInt32(100), ValueRef.FromInt32(50),
                ValueRef.FromColor(255, 128, 0, 255),
            },
            frame, default);

        PathDrawing path = Assert.IsType<PathDrawing>(result.AsDrawing());
        Assert.NotNull(path.Fill);
        Assert.Equal((byte)255, path.Fill!.Value.Red);
        Assert.Equal((byte)128, path.Fill.Value.Green);
        Assert.Null(path.Stroke);
    }

    [Fact]
    public async Task PathHasExpectedCommandShape()
    {
        // For bins=N the path is:
        //   1 PathMove (top edge start)
        // + (N - 1) PathLine (rest of top edge)
        // + N PathLine (bottom edge right-to-left)
        // + 1 PathClose
        // = 2N + 1 commands total.
        EvaluationFrame frame = CreateEvaluationFrame();
        const int bins = 4;
        float[] env = new float[bins * 2];
        for (int b = 0; b < bins; b++)
        {
            env[b * 2 + 0] = -0.5f; env[b * 2 + 1] = 0.5f;
        }
        ValueRef envelope = ValueRef.FromPrimitiveMultiDimArray(env, [bins, 2], DataKind.Float32);

        ValueRef result = await new AudioWaveformPathFunction().ExecuteAsync(
            new[]
            {
                envelope, ValueRef.FromInt32(100), ValueRef.FromInt32(50),
                ValueRef.FromColor(255, 255, 255, 255),
            },
            frame, default);

        PathDrawing path = (PathDrawing)result.AsDrawing();
        Assert.Equal(2 * bins + 1, path.Commands.Length);
        Assert.IsType<PathMove>(path.Commands[0]);
        for (int i = 1; i < bins; i++)
        {
            Assert.IsType<PathLine>(path.Commands[i]);
        }
        for (int i = bins; i < 2 * bins; i++)
        {
            Assert.IsType<PathLine>(path.Commands[i]);
        }
        Assert.IsType<PathClose>(path.Commands[^1]);
    }

    [Fact]
    public async Task PathCoordinates_MapAmplitudeToPixelSpace()
    {
        // For (-1, +1) the path should hit y=height (bottom) and y=0 (top).
        // Centreline at y = height/2 = 25.
        EvaluationFrame frame = CreateEvaluationFrame();
        const int width = 100, height = 50;
        // 2-bin envelope: bin 0 → (-1, +1), bin 1 → (-0.5, 0).
        ValueRef envelope = ValueRef.FromPrimitiveMultiDimArray(
            new float[] { -1f, 1f, -0.5f, 0f }, [2, 2], DataKind.Float32);

        ValueRef result = await new AudioWaveformPathFunction().ExecuteAsync(
            new[]
            {
                envelope, ValueRef.FromInt32(width), ValueRef.FromInt32(height),
                ValueRef.FromColor(255, 255, 255, 255),
            },
            frame, default);

        PathDrawing path = (PathDrawing)result.AsDrawing();
        PathMove start = (PathMove)path.Commands[0];
        // bin 0 max = +1.0 → y = 25 - 1.0 * 25 = 0. x = 0.
        Assert.Equal(0f, start.Point.X, 2);
        Assert.Equal(0f, start.Point.Y, 2);

        // bin 1 max = 0 → y = 25. x = 99 (width - 1, endpoint-inclusive).
        PathLine topEnd = (PathLine)path.Commands[1];
        Assert.Equal(99f, topEnd.Point.X, 2);
        Assert.Equal(25f, topEnd.Point.Y, 2);

        // Bottom edge runs right-to-left: bin 1 min = -0.5 → y = 25 + 12.5 = 37.5
        PathLine botStart = (PathLine)path.Commands[2];
        Assert.Equal(99f, botStart.Point.X, 2);
        Assert.Equal(37.5f, botStart.Point.Y, 2);

        // bin 0 min = -1.0 → y = 50. x = 0.
        PathLine botEnd = (PathLine)path.Commands[3];
        Assert.Equal(0f, botEnd.Point.X, 2);
        Assert.Equal(50f, botEnd.Point.Y, 2);
    }

    [Fact]
    public async Task FlatEnvelope_NonMultiDim_Throws()
    {
        EvaluationFrame frame = CreateEvaluationFrame();
        ValueRef envelope = ValueRef.FromPrimitiveArray(new float[] { 0f, 1f, 2f, 3f }, DataKind.Float32);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new AudioWaveformPathFunction().ExecuteAsync(
                new[]
                {
                    envelope, ValueRef.FromInt32(100), ValueRef.FromInt32(50),
                    ValueRef.FromColor(255, 0, 0, 255),
                },
                frame, default));
        Assert.Contains("audio_waveform_envelope", ex.Message);
    }
}

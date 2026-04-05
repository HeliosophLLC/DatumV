using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Execution.Contexts;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Drawing;

/// <summary>
/// <c>animate_frames(duration Float32, fps Int32, size Point2D, render_frame Lambda) → Array&lt;Image&gt;</c>.
/// Drives an animation-context lambda over <c>frameCount = round(duration *
/// fps)</c> evenly-spaced time values in <c>[0, 1)</c>, rasterising the
/// Drawing each invocation produces into an Image of the requested size.
/// Returns the per-frame Images as a flat array — no encoding, no GIF, no
/// composition into a single output value.
/// </summary>
/// <remarks>
/// <para>
/// This is the substrate function for animation work. Downstream consumers
/// turn the array into something displayable:
/// </para>
/// <list type="bullet">
///   <item><strong>Per-frame transforms</strong> via
///     <c>array_transform(animate_frames(...), f -&gt; sobel(f))</c>. Every
///     existing image function operates on individual frames naturally;
///     no silent first-frame-only flattening.</item>
///   <item><strong>Static thumbnail strips</strong> by feeding the array
///     into the IDE preview, which renders it as a horizontal sprite
///     sheet — good for tuning animations during development.</item>
///   <item><strong>GIF / video encoding</strong> via a later
///     <c>frames_to_gif(frames, fps)</c> when a single-Image output is
///     needed.</item>
/// </list>
/// <para>
/// The lambda's closure captures are snapshotted at the call site (i.e.
/// per-row), so each row's animation sees that row's column values; the
/// frame iteration within a row reuses the same captured environment
/// across all invocations.
/// </para>
/// </remarks>
public sealed class AnimateFramesFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "animate_frames";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;

    /// <inheritdoc />
    public static string Description =>
        "Drives an animation lambda over duration × fps frames and returns the rendered "
        + "per-frame Images as an Array<Image>. Downstream consumers transform, preview, "
        + "or encode the array; no single-output encoding happens here.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("duration",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("fps",          DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("size",         DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("render_frame", DataKindMatcher.Lambda(
                                                      AnimationContext.Name,
                                                      DataKindMatcher.Exact(DataKind.Drawing))),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Image))),
    ];

    /// <inheritdoc />
    public int QueryUnitCost => 100;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AnimateFramesFunction>(argumentKinds);

    /// <inheritdoc />
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        // Snapshot args[3] from the Span outside the await loop — span/in
        // arguments are stack-only and can't cross awaits in C#.
        ValueRef lambda;
        float duration;
        int fps;
        Vector2 size;
        {
            ReadOnlySpan<ValueRef> args = arguments.Span;
            if (args[0].IsNull || args[1].IsNull || args[2].IsNull || args[3].IsNull)
            {
                return ValueRef.NullArray(DataKind.Image);
            }
            duration = args[0].ToFloat();
            fps = args[1].ToInt32();
            size = args[2].AsPoint2D();
            lambda = args[3];
        }

        if (duration <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"duration must be positive; got {duration}.");
        }
        if (fps <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"fps must be positive; got {fps}.");
        }
        int width = (int)System.Math.Round(size.X);
        int height = (int)System.Math.Round(size.Y);
        if (width <= 0 || height <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"size must be positive in both dimensions; got {width}×{height}.");
        }

        if (frame.LambdaInvoker is null)
        {
            throw new InvalidOperationException(
                "animate_frames requires an ILambdaInvoker on the evaluation frame. "
                + "The query pipeline auto-attaches one via ExpressionEvaluator; "
                + "this error indicates a frame built outside that pipeline.");
        }

        int frameCount = (int)System.Math.Round(duration * fps);
        if (frameCount <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"duration × fps must round to a positive frame count; got {frameCount} "
                + $"from duration={duration}, fps={fps}.");
        }

        ValueRef[] frames = new ValueRef[frameCount];
        ValueRef[] lambdaArgs = new ValueRef[1];
        for (int i = 0; i < frameCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            float t = (float)i / frameCount;
            lambdaArgs[0] = ValueRef.FromFloat32(t);
            ValueRef drawing = await frame.LambdaInvoker.InvokeLambdaAsync(
                lambda, lambdaArgs, frame, cancellationToken).ConfigureAwait(false);

            if (drawing.IsNull)
            {
                frames[i] = ValueRef.Null(DataKind.Image);
                continue;
            }

            DrawingPayload payload = drawing.AsDrawing();
            SKBitmap bitmap = new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using (SKCanvas canvas = new(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                DrawingRenderer.Render(canvas, payload);
            }
            frames[i] = ValueRef.FromImage(bitmap);
        }

        return ValueRef.FromArray(DataKind.Image, frames);
    }
}

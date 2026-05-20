using System.Collections.Immutable;
using System.Numerics;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Drawing;

/// <summary>
/// <c>draw_group(children Array&lt;Drawing&gt;)</c> → Drawing. Composes an
/// ordered list of child drawings into a single Drawing; later children
/// render on top of earlier ones. The natural result of an array-literal
/// lambda body that produces a scene.
/// </summary>
public sealed class GroupFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "draw_group";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;

    /// <inheritdoc />
    public static string Description =>
        "Composes a list of Drawings into a single Drawing. Later children render on top of earlier ones.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("children", DataKindMatcher.Exact(DataKind.Drawing), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<GroupFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Drawing));
        }
        ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
        ImmutableArray<DrawingPayload>.Builder children =
            ImmutableArray.CreateBuilder<DrawingPayload>(elements.Length);
        for (int i = 0; i < elements.Length; i++)
        {
            // Null child = skip (matches "ignore missing layer" intuition).
            // A user who wants the failure can filter nulls upstream.
            if (elements[i].IsNull) continue;
            children.Add(elements[i].AsDrawing());
        }
        return new ValueTask<ValueRef>(ValueRef.FromDrawing(new GroupDrawing(children.ToImmutable())));
    }
}

/// <summary>
/// <c>draw_transformed(content Drawing, anchor Point2D, rotation, scale, opacity)</c> → Drawing.
/// Wraps a child drawing with a 2D affine transform — translate (via
/// anchor placement), rotation, uniform scale, opacity. Each component is
/// optional via overloads; the full five-argument form is the most
/// general.
/// </summary>
/// <remarks>
/// Convention: <c>anchor</c> is the inner-content pixel position that
/// lands at the canvas origin after the transform; rotation is in degrees
/// (clockwise); scale is uniform (one factor for both axes); opacity is
/// multiplicative in <c>[0, 1]</c>.
/// </remarks>
public sealed class TransformedFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "draw_transformed";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;

    /// <inheritdoc />
    public static string Description =>
        "Wraps a Drawing with a 2D transform: place the anchor at the origin, "
        + "rotate (degrees, clockwise), scale uniformly, and apply opacity.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("content",  DataKindMatcher.Exact(DataKind.Drawing)),
                new ParameterSpec("anchor",   DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("rotation", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("scale",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("opacity",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TransformedFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull)
            {
                return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Drawing));
            }
        }
        DrawingPayload inner = args[0].AsDrawing();
        Vector2 anchor = args[1].AsPoint2D();
        float rotation = args[2].ToFloat();
        float scale = args[3].ToFloat();
        float opacity = System.Math.Clamp(args[4].ToFloat(), 0f, 1f);

        return new ValueTask<ValueRef>(ValueRef.FromDrawing(new TransformedDrawing(
            Inner: inner,
            Anchor: new SKPoint(anchor.X, anchor.Y),
            Translate: default,
            Scale: scale,
            RotationDegrees: rotation,
            Opacity: opacity)));
    }
}

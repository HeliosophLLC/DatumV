using DatumIngest.Functions.Image;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

using SkiaSharp;

namespace DatumIngest.Execution;

/// <summary>
/// Lowered form of a SQL <c>image(source, f =&gt; f.transform_a().transform_b().reduce())</c>
/// call. Produced by <see cref="ImagePipelineLowerer"/> at plan time and consumed by
/// <see cref="ExpressionEvaluator"/> at run time.
/// </summary>
/// <remarks>
/// <para>
/// Holding the chain in one node lets the runtime evaluator decode the source bytes
/// exactly once, thread the live <see cref="SKBitmap"/> through every transform, and
/// either encode the final bitmap (no sink) or reduce it (sink) — paying decode/encode
/// cost once regardless of chain depth.
/// </para>
/// <para>
/// <strong>Identity short-circuit.</strong> When <c>image(source, f =&gt; f)</c> appears,
/// the lowerer rewrites to <see cref="Source"/> directly and never produces a
/// <see cref="FusedImagePipelineExpression"/>. So this node always represents a
/// non-trivial chain: at least one transform, or a sink, or both.
/// </para>
/// </remarks>
/// <param name="Source">
/// Expression that produces encoded image bytes (typically a column reference, a
/// <c>load_image(...)</c> call, or a sidecar-backed <see cref="DataKind.Image"/> value).
/// Evaluated once per row at the start of the pipeline.
/// </param>
/// <param name="Transforms">
/// Ordered transforms applied to the decoded bitmap. May be empty when the body is
/// just a terminal sink (<c>image(file, f =&gt; f.brightness_mean())</c>).
/// </param>
/// <param name="TerminalSink">
/// Optional terminal sink. When present the pipeline returns its <see cref="DataValue"/>
/// directly and skips the final encode step. When <see langword="null"/>, the pipeline
/// re-encodes the final bitmap to bytes and returns a <see cref="DataKind.Image"/> value.
/// </param>
/// <param name="OutputFormatOverride">
/// Optional explicit encode format for the no-sink path (e.g. caller wants PNG). When
/// <see langword="null"/>, the runtime detects the source's format and re-encodes in
/// kind. Has no effect when <paramref name="TerminalSink"/> is set.
/// </param>
/// <param name="ResultKind">
/// The <see cref="DataKind"/> of the value this pipeline produces. <see cref="DataKind.Image"/>
/// for transform-only chains; the sink's <see cref="IImagePipelineSink.ResultKind"/>
/// when a sink is present. Cached at lowering time so type resolution doesn't have to
/// re-walk the chain.
/// </param>
public sealed record FusedImagePipelineExpression(
    Expression Source,
    IReadOnlyList<PipelineStage> Transforms,
    PipelineSink? TerminalSink,
    SKEncodedImageFormat? OutputFormatOverride,
    DataKind ResultKind) : Expression;

/// <summary>
/// One transform in a fused image pipeline.
/// </summary>
/// <param name="Function">The transform implementation. Receives a live bitmap and produces the next.</param>
/// <param name="AuxiliaryArgs">
/// The non-image arguments to the transform (radius, dimensions, format, etc.). Evaluated
/// per row by the runtime against the outer expression frame and passed to
/// <see cref="IImagePipelineFunction.Apply"/> as a <see cref="ReadOnlySpan{T}"/> of
/// <see cref="DataValue"/>s.
/// </param>
public sealed record PipelineStage(
    IImagePipelineFunction Function,
    IReadOnlyList<Expression> AuxiliaryArgs);

/// <summary>
/// The terminal sink in a fused image pipeline, if present.
/// </summary>
/// <param name="Function">The sink implementation. Reduces the final bitmap to a non-image <see cref="DataValue"/>.</param>
/// <param name="AuxiliaryArgs">Non-image arguments to the sink (e.g. histogram bin count).</param>
public sealed record PipelineSink(
    IImagePipelineSink Function,
    IReadOnlyList<Expression> AuxiliaryArgs);

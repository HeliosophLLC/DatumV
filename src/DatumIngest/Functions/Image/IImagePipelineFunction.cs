using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Functions.Image;

/// <summary>
/// Marker interface for image transform functions that can participate in a fused
/// image pipeline. The pipeline decodes the source image once, threads the live
/// <see cref="SKBitmap"/> through each transform, and encodes the final result back
/// to bytes — paying decode/encode cost exactly once regardless of chain length.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline transforms operate on a live <see cref="SKBitmap"/> rather than on
/// encoded image bytes inside <see cref="DataValue"/>. Auxiliary arguments
/// (radius, intensity, target dimensions, etc.) still flow as <see cref="DataValue"/>
/// — they're typically inline numerics whose evaluation is cheap.
/// </para>
/// <para>
/// Implementations are recognised by the planner during the <c>image(source, lambda)</c>
/// fusion pass. Each function call inside the lambda body must resolve to either an
/// <see cref="IImagePipelineFunction"/> (a transform) or an <see cref="IImagePipelineSink"/>
/// (a terminal reducer). Fusion stops when a non-pipeline function is encountered.
/// </para>
/// </remarks>
public interface IImagePipelineFunction
{
    /// <summary>The unqualified function name as it appears in SQL (e.g. <c>blur</c>, <c>resize</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Validates the auxiliary (non-image) argument kinds and returns whether the
    /// auxiliary count and types are acceptable. The image input itself is implicit —
    /// it's always the lambda parameter being threaded through.
    /// </summary>
    /// <param name="auxiliaryKinds">
    /// The kinds of every argument <em>except</em> the image arg. For
    /// <c>blur(f, 5)</c>, this is <c>[Float32]</c> (or whatever numeric kind 5 narrows to).
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when the auxiliary signature doesn't match what the transform expects.
    /// </exception>
    void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds);

    /// <summary>
    /// Applies this transform to a live <see cref="SKBitmap"/> and returns the next
    /// bitmap in the chain. The caller (the pipeline runtime) owns the lifecycle of
    /// the input <paramref name="input"/> until <see cref="Apply"/> returns.
    /// </summary>
    /// <param name="input">The current bitmap. Must not be disposed by the implementation.</param>
    /// <param name="auxiliaryArgs">
    /// Pre-evaluated non-image arguments (e.g. radius, target dimensions). The implementation
    /// may use any of the widening accessors (<see cref="DataValue.ToFloat"/>,
    /// <see cref="DataValue.ToInt32"/>) on these.
    /// </param>
    /// <returns>
    /// A new <see cref="SKBitmap"/> representing the transform's output. May return
    /// <paramref name="input"/> itself when the transform is a no-op for the given
    /// arguments (the caller will detect reference equality and skip disposal).
    /// </returns>
    SKBitmap Apply(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs);

    /// <summary>
    /// Optional override for the output encoding format. Returns <see langword="null"/>
    /// when this transform doesn't dictate the format (most transforms — they preserve
    /// whatever the source had). Returns a specific format when the user passed one as
    /// an auxiliary arg (e.g. <c>blur(f, 5, 'png')</c>) or when the transform always
    /// emits a specific kind. The pipeline runtime applies the rightmost non-null
    /// override across all stages — so a later transform's choice wins.
    /// </summary>
    /// <param name="auxiliaryArgs">
    /// Same span passed to <see cref="Apply"/>. Lets transforms inspect their own args
    /// (a per-call format string) without storing per-instance state.
    /// </param>
    SKEncodedImageFormat? FormatOverride(ReadOnlySpan<DataValue> auxiliaryArgs) => null;
}

/// <summary>
/// Marker interface for terminal "sink" functions that consume an
/// <see cref="SKBitmap"/> and produce a non-image <see cref="DataValue"/>.
/// A pipeline can end with at most one sink; if a sink is present, the pipeline
/// returns the sink's result and skips the final encode step.
/// </summary>
/// <remarks>
/// <para>
/// Sinks include statistics (<c>brightness_mean</c>, <c>perceptual_hash</c>), tensor
/// extraction (<c>image_to_tensor_chw</c>), and metric scores (<c>detect_blur</c>,
/// <c>compression_artifact_score</c>) — anything that takes an image and returns a
/// scalar / vector / tensor value.
/// </para>
/// </remarks>
public interface IImagePipelineSink
{
    /// <summary>The unqualified function name as it appears in SQL.</summary>
    string Name { get; }

    /// <summary>The <see cref="DataKind"/> of the sink's result. Used by the planner for type resolution.</summary>
    DataKind ResultKind { get; }

    /// <summary>
    /// Validates the auxiliary (non-image) argument kinds. The image input is implicit.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown for an unacceptable auxiliary signature.</exception>
    void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds);

    /// <summary>
    /// Reduces the input bitmap to a <see cref="DataValue"/>. The pipeline runtime
    /// passes its current target store via <paramref name="targetStore"/> so the sink
    /// can materialise non-inline results (e.g. a vector of histogram bins) into it.
    /// </summary>
    /// <param name="input">The terminal bitmap. Owned by the pipeline; not disposed by the sink.</param>
    /// <param name="auxiliaryArgs">Pre-evaluated non-image arguments.</param>
    /// <param name="targetStore">Where the sink may materialise reference-typed results.</param>
    DataValue Reduce(
        SKBitmap input,
        ReadOnlySpan<DataValue> auxiliaryArgs,
        IValueStore targetStore);
}

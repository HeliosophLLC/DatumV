namespace DatumIngest.Execution.Contexts;

/// <summary>
/// <see cref="IFunctionContext"/> for the per-column lambda accepted by
/// <c>audio_waveform_drawing</c>. The lambda is invoked once per output
/// column with the column's normalised position and the bin's amplitude
/// envelope, and is expected to return a Drawing that will be composited
/// into the final waveform visualisation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Parameter convention.</strong>
/// <list type="bullet">
///   <item><description><c>t: Float32</c> — column position in <c>[0, 1]</c>
///   across the rendered width.</description></item>
///   <item><description><c>min: Float32</c> — minimum sample amplitude in
///   this bin, in <c>[-1, 1]</c>.</description></item>
///   <item><description><c>max: Float32</c> — maximum sample amplitude in
///   this bin, in <c>[-1, 1]</c>.</description></item>
/// </list>
/// Width and height for the rendered canvas are not lambda parameters: the
/// caller already names them at the call site, so the lambda body captures
/// them by closure and uses them directly in pixel-space drawing calls.
/// </para>
/// <para>
/// <strong>Scope.</strong> Inherits from <see cref="AnimationContext"/> so
/// the animation-curve toolkit (<c>lerp</c>, <c>oscillate</c>, <c>wobble</c>,
/// <c>bounce</c>, <c>fade_in</c>, <c>fade_out</c>, <c>random_walk</c>) is
/// callable inside the column lambda on <c>t</c>. The functions are
/// mathematically agnostic to whether their input represents animation
/// time or column position — the inheritance gives waveform stylisation
/// the same expressive kit without re-tagging every curve.
/// </para>
/// </remarks>
public sealed class WaveformContext : IFunctionContext
{
    /// <inheritdoc />
    public static string Name => "waveform";

    /// <inheritdoc />
    public static IReadOnlyList<LambdaParameterSpec> Parameters { get; } =
    [
        new LambdaParameterSpec("t",   Model.DataKind.Float32),
        new LambdaParameterSpec("min", Model.DataKind.Float32),
        new LambdaParameterSpec("max", Model.DataKind.Float32),
    ];

    /// <inheritdoc />
    public static string? ParentName => AnimationContext.Name;

    /// <inheritdoc />
    public static IReadOnlyList<string> Borrows { get; } = [];
}

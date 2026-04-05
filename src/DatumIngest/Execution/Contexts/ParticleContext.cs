namespace DatumIngest.Execution.Contexts;

/// <summary>
/// <see cref="IFunctionContext"/> for the per-particle sprite lambda
/// accepted by <c>draw_particles</c>. The lambda receives a single
/// <c>Float32</c> argument <c>x</c> — the particle's <strong>normalised
/// age</strong> in <c>[0, 1]</c> — and is expected to return a Drawing
/// that will be stamped at the particle's current position.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Parameter convention.</strong> <c>x: Float32</c> represents
/// <c>age / lifetime</c> for the particular particle being rendered. A
/// freshly-emitted particle sees <c>x = 0</c>, a particle about to die
/// sees <c>x → 1</c>. Users can rename the parameter — the canonical
/// name is only an LS pre-fill hint.
/// </para>
/// <para>
/// <strong>Scope.</strong> Inherits from <see cref="AnimationContext"/>
/// so animation curves (<c>lerp</c>, <c>oscillate</c>, <c>wobble</c>,
/// <c>bounce</c>, <c>fade_in</c>, <c>fade_out</c>, <c>random_walk</c>)
/// are all callable inside the sprite lambda on the particle's <c>x</c>.
/// The semantics carry over: applying <c>oscillate(x, low, high)</c>
/// inside the lambda oscillates each particle's value over its own
/// lifetime, not over the animation timeline.
/// </para>
/// </remarks>
public sealed class ParticleContext : IFunctionContext
{
    /// <inheritdoc />
    public static string Name => "particle";

    /// <inheritdoc />
    public static IReadOnlyList<LambdaParameterSpec> Parameters { get; } =
    [
        new LambdaParameterSpec("x", Model.DataKind.Float32),
    ];

    /// <inheritdoc />
    public static string? ParentName => AnimationContext.Name;

    /// <inheritdoc />
    public static IReadOnlyList<string> Borrows { get; } = [];
}

namespace Heliosoph.DatumV.Execution.Contexts;

/// <summary>
/// <see cref="IFunctionContext"/> for lambda bodies that produce a
/// per-frame Drawing in an animation pipeline. Animation drivers
/// (<c>animate_frames</c>, <c>animate_gif</c>) declare a lambda parameter
/// scoped to this context; the lambda receives the current frame's
/// normalised time <c>t</c> and is expected to return a Drawing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Parameter convention.</strong> The canonical parameter is
/// <c>t: Float32</c> in the range <c>[0, 1)</c> across the animation's
/// duration. Animation drivers iterate frame indices <c>i</c> in
/// <c>[0, frameCount)</c> and pass <c>t = i / frameCount</c>. Users can
/// rename the parameter (<c>u -&gt; ...</c>) — the canonical name is
/// only the language-server pre-fill hint.
/// </para>
/// <para>
/// <strong>Scope.</strong> Inherits the <see cref="PureContext"/>
/// whitelist (arithmetic, math, string ops, all globally-visible
/// functions). Drawing primitives (<c>draw_rect</c>, <c>draw_ellipse</c>,
/// …, <c>draw_group</c>, <c>draw_transformed</c>) are globally visible
/// today, so they are automatically callable here without needing an
/// explicit <see cref="Borrows"/> entry. Animation-specific primitives
/// landing in later phases (<c>oscillate</c>, <c>wobble</c>,
/// <c>lerp</c>, <c>draw_particles</c>, …) will tag themselves with this
/// context's name via <c>static Contexts =&gt; [AnimationContext.Name]</c>
/// to become callable here without escaping into general SQL scope.
/// </para>
/// </remarks>
public sealed class AnimationContext : IFunctionContext
{
    /// <inheritdoc />
    public static string Name => "animation";

    /// <inheritdoc />
    public static IReadOnlyList<LambdaParameterSpec> Parameters { get; } =
    [
        new LambdaParameterSpec("t", Model.DataKind.Float32),
    ];

    /// <inheritdoc />
    public static string? ParentName => PureContext.Name;

    /// <inheritdoc />
    public static IReadOnlyList<string> Borrows { get; } = [];
}

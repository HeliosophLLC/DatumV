namespace DatumIngest.Execution.Contexts;

/// <summary>
/// The root <see cref="IFunctionContext"/>. A lambda parameter slot that
/// declares <c>DataKindMatcher.Lambda</c> with no explicit context
/// resolves to <c>PureContext</c>, meaning the lambda body sees the union
/// of all globally-visible functions and nothing more — no implicit
/// variables, no DSL-specific primitives.
/// </summary>
/// <remarks>
/// <para>
/// PureContext is the conceptual baseline for "a callback in regular SQL
/// space": <c>array_transform(arr, x -&gt; x + 1)</c>, <c>array_filter(arr,
/// x -&gt; x &gt; 0)</c>, and anything similar that doesn't need a scoped
/// sub-language. The lambda's single parameter is whatever the
/// higher-order function declares — there's no canonical name baked into
/// the context itself because PureContext is the empty-parameter case;
/// concrete consumers override the parameter list at the matcher level
/// via the <c>Returns</c> parameter shape they declare.
/// </para>
/// <para>
/// Specialised contexts (animation, drawing, future panel-layout) inherit
/// from PureContext to keep arithmetic / math / string / array operations
/// implicitly available without restating them as borrows.
/// </para>
/// </remarks>
public sealed class PureContext : IFunctionContext
{
    /// <inheritdoc />
    public static string Name => "pure";

    /// <inheritdoc />
    public static IReadOnlyList<LambdaParameterSpec> Parameters { get; } = [];

    /// <inheritdoc />
    public static string? ParentName => null;

    /// <inheritdoc />
    public static IReadOnlyList<string> Borrows { get; } = [];
}

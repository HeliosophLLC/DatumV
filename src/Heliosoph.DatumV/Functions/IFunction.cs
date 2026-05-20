using Heliosoph.DatumV.Manifest;

namespace Heliosoph.DatumV.Functions;

/// <summary>
/// Static-abstract metadata interface for registered functions. Carries the
/// canonical name, category, description, and accepted signature shapes.
/// Read via generic constraints (<c>where T : IFunction</c>) so the catalog
/// and language-server tooling can describe functions without instantiating
/// their classes.
/// </summary>
/// <remarks>
/// <para>
/// Kept separate from <see cref="IScalarFunction"/> because interfaces with
/// static abstract members can't be used as generic type arguments
/// (Dictionary&lt;string, IFunction&gt; would fail with CS8920). Implementing
/// classes implement both <see cref="IFunction"/> (for metadata) and
/// <see cref="IScalarFunction"/> (for instance dispatch).
/// </para>
/// </remarks>
public interface IFunction
{
    /// <summary>The function's canonical name (case-insensitive).</summary>
    static abstract string Name { get; }

    /// <summary>Functional category (drives grouping in completion / catalog views).</summary>
    static abstract FunctionCategory Category { get; }

    /// <summary>Human-readable description for hover / catalog text.</summary>
    static abstract string Description { get; }

    /// <summary>
    /// Accepted argument shapes. The metadata-driven validator
    /// (<see cref="FunctionMetadata.Validate{T}"/>) walks these to pick the
    /// matching variant and resolve the result kind.
    /// </summary>
    static abstract IReadOnlyList<FunctionSignatureVariant> Signatures { get; }

    /// <summary>
    /// Procedural context required for the function to be callable.
    /// Defaults to <see cref="BodyScopeRequirement.None"/> (callable
    /// anywhere); body-scoped functions like <c>infer()</c> override to
    /// <see cref="BodyScopeRequirement.ModelBody"/> so
    /// <see cref="Heliosoph.DatumV.Execution.PlanTimeFunctionGate"/> can refuse
    /// out-of-context call sites at plan time.
    /// </summary>
    static virtual BodyScopeRequirement BodyScope => BodyScopeRequirement.None;

    /// <summary>
    /// Names of the <see cref="Heliosoph.DatumV.Execution.Contexts.IFunctionContext"/>s
    /// this function is visible inside. The default <c>[]</c> means
    /// "globally visible" — the function resolves in every name-resolution
    /// scope, the same posture every function has had before this
    /// mechanism existed. A non-empty list restricts visibility to lambda
    /// bodies whose parameter slot declared one of those contexts (or any
    /// descendant context, via parent-chain inheritance).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use a non-empty <c>Contexts</c> list when a function only makes
    /// sense inside a specific DSL — e.g. <c>oscillate(t, ...)</c> and
    /// <c>draw_particles(t, ...)</c> are pointless outside an animation
    /// context, so they tag themselves with the animation context name
    /// and the planner refuses to resolve them in any other scope.
    /// </para>
    /// <para>
    /// The complementary mechanism is
    /// <see cref="Heliosoph.DatumV.Execution.Contexts.IFunctionContext.Borrows"/>:
    /// when a globally-visible function is also intentionally useful
    /// inside a context, the context can borrow it without the function
    /// declaring membership. The two halves together cover both
    /// "purpose-built primitive" (function declares) and "general-purpose
    /// utility opted in" (context declares).
    /// </para>
    /// </remarks>
    static virtual IReadOnlyList<string> Contexts => [];
}

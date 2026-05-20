using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Contexts;

/// <summary>
/// A named scope that constrains which functions and variables are visible
/// inside a lambda body. Implementing types pair this static-abstract
/// interface with registration via <see cref="FunctionContextRegistry"/>
/// at engine startup.
/// </summary>
/// <remarks>
/// <para>
/// Function-context membership has two complementary mechanisms that together
/// determine the effective whitelist for a context:
/// </para>
/// <list type="bullet">
///   <item>
///     <strong>Function-side opt-in</strong> — a function declaring
///     <c>static Contexts =&gt; [SomeContext.Name]</c> becomes visible inside
///     that context (and only there, unless globally visible because its
///     <c>Contexts</c> list is empty).
///   </item>
///   <item>
///     <strong>Context-side opt-in</strong> — a context's <see cref="Borrows"/>
///     list names globally-visible functions whose semantics are desired
///     inside the context's lambdas. The context "borrows" these from the
///     ambient scope without modifying the functions themselves.
///   </item>
/// </list>
/// <para>
/// The two halves cover the two natural locality preferences: when a function
/// is purpose-built for one DSL (e.g. <c>oscillate</c> for animation), it
/// declares its context membership in its own file. When a function is
/// general-purpose but happens to be useful inside a specific DSL (e.g.
/// <c>rotate</c> from the image-transform set), the context's definition
/// lists it as a borrow.
/// </para>
/// <para>
/// A context optionally specifies a <see cref="ParentName"/>: ancestor
/// contexts' whitelists transitively apply, so <c>AnimationContext</c> with
/// parent <c>PureContext</c> inherits everything pure without restating it.
/// The root context is typically <see cref="PureContext"/>.
/// </para>
/// </remarks>
public interface IFunctionContext
{
    /// <summary>
    /// Stable identifier used by <c>DataKindMatcher.Lambda</c>,
    /// per-function <c>Contexts</c> lists, and the LS manifest to refer to
    /// this context.
    /// </summary>
    static abstract string Name { get; }

    /// <summary>
    /// The lambda parameter list a function-taking-a-Lambda-of-this-context
    /// expects. For example, <c>AnimationContext.Parameters</c> is
    /// <c>[(t, Float32)]</c>: any lambda passed to a function that declared
    /// the animation context must accept one Float32 argument.
    /// </summary>
    /// <remarks>
    /// Empty list = the lambda takes no arguments. Used by the planner /
    /// matcher to validate at plan time that the lambda's declared
    /// parameter count and kinds match the context's expectation. The
    /// parameter <em>names</em> in the user's lambda may differ from the
    /// names here; those declared here are the canonical names the LS
    /// pre-fills on completion (e.g. typing the lambda arrow inside an
    /// animation context produces <c>t -&gt; </c>).
    /// </remarks>
    static abstract IReadOnlyList<LambdaParameterSpec> Parameters { get; }

    /// <summary>
    /// Name of the parent context, or <see langword="null"/> for a root
    /// context. The effective whitelist of this context is the union of its
    /// own borrows, its function-tagged members, and the parent's effective
    /// whitelist (recursively).
    /// </summary>
    static virtual string? ParentName => null;

    /// <summary>
    /// Globally-visible function names this context opts in to. Use for
    /// general-purpose functions defined elsewhere whose semantics happen to
    /// be useful inside this DSL — without modifying the function itself.
    /// </summary>
    static virtual IReadOnlyList<string> Borrows => [];
}

/// <summary>
/// The declared parameter shape of a lambda accepted by a function in a
/// given <see cref="IFunctionContext"/>. Used both as plan-time validation
/// metadata and as the LS hint for what name + kind to pre-fill when the
/// user types an arrow inside a lambda parameter slot.
/// </summary>
/// <param name="Name">Canonical parameter name (e.g. <c>t</c> for animation).</param>
/// <param name="Kind">Expected <see cref="DataKind"/> of the parameter.</param>
public sealed record LambdaParameterSpec(string Name, DataKind Kind);

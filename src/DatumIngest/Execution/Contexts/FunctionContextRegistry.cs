namespace DatumIngest.Execution.Contexts;

/// <summary>
/// Per-engine registry of <see cref="IFunctionContext"/>s by name. Populated
/// at startup (via <see cref="Register{T}"/>) for every context type the
/// engine surfaces; consulted at plan time by the function resolver to
/// compute the effective whitelist for a lambda body's scope, and exported
/// to the language-server manifest for completion / hover.
/// </summary>
/// <remarks>
/// <para>
/// The registry stores immutable <see cref="FunctionContextDescriptor"/>
/// snapshots rather than the static-abstract interface types themselves —
/// this keeps the rest of the engine free of the awkward generic-constraint
/// gymnastics that static-abstract interfaces require, and gives us a single
/// place to add cached computed properties (effective whitelist, parameter
/// signature) later.
/// </para>
/// <para>
/// Registration is idempotent: registering a context type twice replaces
/// the previous descriptor under the same name, which matches the behaviour
/// of <see cref="DatumIngest.Functions.FunctionRegistry.RegisterScalar"/> and
/// keeps engine-bootstrap order forgiving.
/// </para>
/// </remarks>
public sealed class FunctionContextRegistry
{
    private readonly Dictionary<string, FunctionContextDescriptor> _byName =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a <see cref="IFunctionContext"/> implementation by reading
    /// its static-abstract metadata into a runtime descriptor.
    /// </summary>
    public void Register<T>() where T : IFunctionContext
    {
        FunctionContextDescriptor descriptor = new(
            Name: T.Name,
            Parameters: T.Parameters,
            ParentName: T.ParentName,
            Borrows: T.Borrows);
        _byName[descriptor.Name] = descriptor;
    }

    /// <summary>
    /// Returns the descriptor for the context with the given name, or
    /// <see langword="null"/> if no such context is registered.
    /// </summary>
    public FunctionContextDescriptor? TryGet(string name) =>
        _byName.TryGetValue(name, out FunctionContextDescriptor? descriptor)
            ? descriptor
            : null;

    /// <summary>
    /// Returns the descriptor for <paramref name="name"/>, or throws
    /// <see cref="ArgumentException"/> when no such context is registered.
    /// Use this when the caller has a static reference to a context type and
    /// the absence indicates a bootstrap-order bug rather than a user error.
    /// </summary>
    public FunctionContextDescriptor Get(string name) =>
        TryGet(name) ?? throw new ArgumentException(
            $"No function context registered under the name '{name}'. "
            + "Ensure the context type is registered via "
            + "FunctionContextRegistry.Register<T>() at engine startup.",
            nameof(name));

    /// <summary>
    /// Returns the names of every context registered in this instance.
    /// Used by the LS manifest exporter to enumerate contexts for the
    /// completion provider.
    /// </summary>
    public IReadOnlyCollection<string> Names => _byName.Keys;

    /// <summary>
    /// Walks the parent chain starting at <paramref name="contextName"/>,
    /// yielding the context itself first and each ancestor in order. Stops
    /// when a context has no parent or when the chain becomes self-referential
    /// (defensive — a malformed registry shouldn't infinite-loop callers).
    /// </summary>
    public IEnumerable<FunctionContextDescriptor> WalkAncestors(string contextName)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        string? current = contextName;
        while (current is not null && seen.Add(current))
        {
            FunctionContextDescriptor? descriptor = TryGet(current);
            if (descriptor is null)
            {
                yield break;
            }
            yield return descriptor;
            current = descriptor.ParentName;
        }
    }

    /// <summary>
    /// Creates a registry populated with the engine's built-in contexts:
    /// <see cref="PureContext"/> (root) and
    /// <see cref="AnimationContext"/> (for animation drivers like
    /// <c>animate_frames</c>). Additional consumer-introduced contexts
    /// can register themselves via <see cref="Register{T}"/> at startup.
    /// </summary>
    public static FunctionContextRegistry CreateDefault()
    {
        FunctionContextRegistry registry = new();
        registry.Register<PureContext>();
        registry.Register<AnimationContext>();
        registry.Register<ParticleContext>();
        registry.Register<WaveformContext>();
        return registry;
    }
}

/// <summary>
/// Runtime metadata snapshot for an <see cref="IFunctionContext"/>: the
/// static-abstract members captured into a regular record so consumers can
/// pass context info as a value without generic-constraint plumbing.
/// </summary>
/// <param name="Name">The context's identifier.</param>
/// <param name="Parameters">Lambda parameter list expected by functions accepting a Lambda-of-this-context.</param>
/// <param name="ParentName">Optional parent context name for whitelist inheritance.</param>
/// <param name="Borrows">Globally-visible function names this context opts in to.</param>
public sealed record FunctionContextDescriptor(
    string Name,
    IReadOnlyList<LambdaParameterSpec> Parameters,
    string? ParentName,
    IReadOnlyList<string> Borrows);

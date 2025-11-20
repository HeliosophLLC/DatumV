using DatumIngest.Manifest;

namespace DatumIngest.Functions;

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
}

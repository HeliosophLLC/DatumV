using DatumIngest.Manifest;

namespace DatumIngest.Functions;

/// <summary>
/// Catalog-side metadata for a registered function. Built once at
/// <see cref="FunctionRegistry.RegisterScalar{T}"/> time by reading the
/// implementing type's static-abstract members; the same descriptor is
/// reused for catalog virtual tables, language-server completion, hover,
/// and signature help.
/// </summary>
/// <param name="PrimaryName">Canonical name of the function (case-insensitive).</param>
/// <param name="Aliases">Additional names registered for the same function.</param>
/// <param name="Category">Functional category (String / Numeric / Conversion …).</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="Signatures">Accepted argument shapes.</param>
/// <param name="BodyScope">
/// Procedural context required for the function to be callable. Defaults
/// to <see cref="BodyScopeRequirement.None"/> (callable anywhere); set to
/// <see cref="BodyScopeRequirement.ModelBody"/> for functions that only
/// make sense inside a <c>CREATE MODEL</c> body. The plan-time gate in
/// <c>ExpressionTypeResolver</c> checks this and refuses out-of-context
/// call sites before any rows are scanned.
/// </param>
public sealed record FunctionDescriptor(
    string PrimaryName,
    IReadOnlyList<string> Aliases,
    FunctionCategory Category,
    string Description,
    IReadOnlyList<FunctionSignatureVariant> Signatures,
    BodyScopeRequirement BodyScope = BodyScopeRequirement.None);

using Heliosoph.DatumV.Manifest;

namespace Heliosoph.DatumV.Functions;

/// <summary>
/// Catalog-side metadata for a registered aggregate function. Built once at
/// <see cref="FunctionRegistry.RegisterAggregate{T}"/> time by reading the
/// implementing type's static-abstract members; the same descriptor feeds
/// catalog virtual tables, language-server completion, hover, and signature
/// help. Mirrors <see cref="FunctionDescriptor"/> for scalars.
/// </summary>
/// <param name="PrimaryName">Canonical name of the aggregate (case-insensitive).</param>
/// <param name="Aliases">Additional names registered for the same aggregate.</param>
/// <param name="Category">Functional category (typically <see cref="FunctionCategory.Aggregate"/>).</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="Signatures">Accepted argument shapes.</param>
/// <param name="SchemaName">
/// SQL schema this aggregate is registered under (typically <c>system</c>).
/// Threaded through to the language-server manifest so completion can filter
/// built-ins on <c>schema.</c>-qualified popups.
/// </param>
public sealed record AggregateFunctionDescriptor(
    string PrimaryName,
    IReadOnlyList<string> Aliases,
    FunctionCategory Category,
    string Description,
    IReadOnlyList<FunctionSignatureVariant> Signatures,
    string SchemaName = "system");

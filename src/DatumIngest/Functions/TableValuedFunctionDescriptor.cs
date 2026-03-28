using DatumIngest.Manifest;

namespace DatumIngest.Functions;

/// <summary>
/// Catalog-side metadata for a registered table-valued function. Built
/// once at <see cref="FunctionRegistry.RegisterTableValued{T}"/> time by
/// reading the implementing type's static-abstract members; the same
/// descriptor feeds catalog virtual tables, language-server completion,
/// hover, and signature help. Mirrors <see cref="FunctionDescriptor"/> for
/// scalars but carries TVF-flavoured signature variants
/// (<see cref="TableValuedFunctionSignatureVariant"/>), which describe a
/// row schema rather than a scalar return kind.
/// </summary>
/// <param name="PrimaryName">Canonical name of the function (case-insensitive).</param>
/// <param name="Category">Functional category (Table / Inference / …).</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="Signatures">Accepted argument shapes.</param>
/// <param name="SchemaName">
/// SQL schema this function is registered under (<c>system</c>,
/// <c>inference</c>, …). Threaded through to the language-server manifest
/// so completion can filter built-ins on <c>schema.</c>-qualified popups.
/// </param>
public sealed record TableValuedFunctionDescriptor(
    string PrimaryName,
    FunctionCategory Category,
    string Description,
    IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures,
    string SchemaName = "system");

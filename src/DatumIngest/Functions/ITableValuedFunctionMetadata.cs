using DatumIngest.Manifest;

namespace DatumIngest.Functions;

/// <summary>
/// Static-abstract metadata interface for table-valued functions. Mirrors
/// <see cref="IFunction"/> for scalar functions — carries the canonical name,
/// category, and description so the registry can describe the function without
/// instantiating it.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="ITableValuedFunction"/> for the same reason
/// <see cref="IFunction"/> is separate from <see cref="IScalarFunction"/>:
/// interfaces with static abstract members cannot be used as generic type
/// arguments, so the metadata and dispatch contracts live on distinct interfaces.
/// Implementing classes implement both, and
/// <see cref="FunctionRegistry.RegisterTableValued{T}"/> reads the metadata at
/// registration time.
/// </remarks>
public interface ITableValuedFunctionMetadata
{
    /// <summary>The function's canonical name (case-insensitive).</summary>
    static abstract string Name { get; }

    /// <summary>Functional category (drives grouping in completion / catalog views).</summary>
    static abstract FunctionCategory Category { get; }

    /// <summary>Human-readable description for hover / catalog text.</summary>
    static abstract string Description { get; }

    /// <summary>
    /// Accepted argument shapes. The runtime check in
    /// <see cref="ITableValuedFunction.ValidateArguments"/> remains the
    /// source of truth for execution-time enforcement; this static surface
    /// is what the language server reads for completion, hover, and
    /// signature help. Implementations should keep the two in lock-step.
    /// </summary>
    static abstract IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; }
}

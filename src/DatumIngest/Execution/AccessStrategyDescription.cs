namespace DatumIngest.Execution;

/// <summary>
/// Describes the data access strategy for a scan operator, including the
/// primary access method and any chunk-level pruning capabilities.
/// </summary>
public sealed class AccessStrategyDescription
{
    /// <summary>
    /// Creates an access strategy description.
    /// </summary>
    /// <param name="method">The primary access method.</param>
    /// <param name="pruningCapabilities">Chunk-level pruning techniques available.</param>
    public AccessStrategyDescription(AccessMethod method, IReadOnlyList<PruningCapability>? pruningCapabilities = null)
    {
        Method = method;
        PruningCapabilities = pruningCapabilities ?? [];
    }

    /// <summary>The primary access method chosen by the planner.</summary>
    public AccessMethod Method { get; }

    /// <summary>Chunk-level pruning capabilities available at this scan.</summary>
    public IReadOnlyList<PruningCapability> PruningCapabilities { get; }
}

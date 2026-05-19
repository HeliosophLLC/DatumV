namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Describes a single chunk-level pruning technique available at a scan operator.
/// </summary>
public sealed class PruningCapability
{
    /// <summary>
    /// Creates a pruning capability description.
    /// </summary>
    /// <param name="technique">The pruning technique.</param>
    /// <param name="columns">The column(s) this pruning applies to.</param>
    /// <param name="pendingRuntime">
    /// Whether this capability is pending a runtime decision (e.g. bloom filter
    /// pruning from joins is not confirmed until the build side executes).
    /// </param>
    public PruningCapability(PruningTechnique technique, IReadOnlyList<string> columns, bool pendingRuntime = false)
    {
        Technique = technique;
        Columns = columns;
        PendingRuntime = pendingRuntime;
    }

    /// <summary>The pruning technique.</summary>
    public PruningTechnique Technique { get; }

    /// <summary>Column(s) this pruning applies to.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>
    /// Whether this capability is pending a runtime decision.
    /// Bloom filter pruning from joins is pending until the build side executes.
    /// </summary>
    public bool PendingRuntime { get; }
}

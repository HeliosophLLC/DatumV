namespace Axon.QueryEngine.Output;

/// <summary>
/// Defines the sharding strategy for splitting output into multiple files.
/// </summary>
/// <param name="Mode">The sharding mode (by sample count or byte size).</param>
/// <param name="Threshold">The threshold value at which a new shard is created.</param>
public sealed record ShardStrategy(ShardMode Mode, long Threshold);

/// <summary>
/// Sharding modes for output splitting.
/// </summary>
public enum ShardMode
{
    /// <summary>Create a new shard every N rows.</summary>
    SampleCount,

    /// <summary>Create a new shard every N bytes.</summary>
    ByteSize
}

namespace DatumQuery.Output.Checkpoint;

/// <summary>
/// Orchestrates checkpoint read/write operations for sharded output.
/// Scans for existing checkpoint markers, computes resume state,
/// and writes new markers as shards complete.
/// </summary>
public sealed class CheckpointManager
{
    private readonly string _basePath;
    private readonly string _extension;

    /// <summary>
    /// Initializes a new instance of the <see cref="CheckpointManager"/> class.
    /// </summary>
    /// <param name="basePath">Base output file path (with extension) matching the ShardingOutputWriter path.</param>
    public CheckpointManager(string basePath)
    {
        _extension = Path.GetExtension(basePath);
        _basePath = Path.Combine(
            Path.GetDirectoryName(basePath) ?? ".",
            Path.GetFileNameWithoutExtension(basePath));
    }

    /// <summary>
    /// Scans the output directory for existing checkpoint marker files,
    /// parses each, and returns them sorted by shard index.
    /// </summary>
    /// <returns>A sorted list of checkpoint markers from previous runs.</returns>
    public async Task<IReadOnlyList<CheckpointMarker>> ScanExistingCheckpointsAsync()
    {
        string directory = Path.GetDirectoryName(_basePath) ?? ".";
        string fileNameBase = Path.GetFileName(_basePath);

        if (!Directory.Exists(directory))
        {
            return [];
        }

        string pattern = $"{fileNameBase}_shard_*{_extension}.checkpoint";
        string[] checkpointFiles = Directory.GetFiles(directory, pattern);

        List<CheckpointMarker> markers = new();
        foreach (string file in checkpointFiles)
        {
            CheckpointMarker? marker = await CheckpointSerializer.ReadFromFileAsync(file);
            if (marker is not null)
            {
                markers.Add(marker);
            }
        }

        markers.Sort((a, b) => a.ShardIndex.CompareTo(b.ShardIndex));
        return markers;
    }

    /// <summary>
    /// Writes a checkpoint marker file for a completed shard.
    /// The marker is written to <c>{shardPath}.checkpoint</c>.
    /// </summary>
    /// <param name="marker">The checkpoint marker to persist.</param>
    public async Task WriteCheckpointAsync(CheckpointMarker marker)
    {
        string checkpointPath = $"{marker.ShardPath}.checkpoint";
        await CheckpointSerializer.WriteToFileAsync(marker, checkpointPath);
    }

    /// <summary>
    /// Computes the resume state from a sequence of consecutive checkpoint markers.
    /// Only counts markers that form a contiguous sequence starting from shard index 0.
    /// </summary>
    /// <param name="checkpoints">Sorted checkpoint markers from a previous run.</param>
    /// <returns>The resume state indicating how many rows to skip and which shard to start at.</returns>
    public static ResumeState ComputeResumeState(IReadOnlyList<CheckpointMarker> checkpoints)
    {
        if (checkpoints.Count == 0)
        {
            return new ResumeState(RowsToSkip: 0, NextShardIndex: 0);
        }

        long totalRows = 0;
        int contiguousCount = 0;

        for (int i = 0; i < checkpoints.Count; i++)
        {
            if (checkpoints[i].ShardIndex != i)
            {
                // Gap in shard sequence — stop counting.
                break;
            }

            totalRows += checkpoints[i].RowCount;
            contiguousCount++;
        }

        return new ResumeState(RowsToSkip: totalRows, NextShardIndex: contiguousCount);
    }

    /// <summary>
    /// Deletes the orphaned shard file at the resume point, if it exists without
    /// a corresponding checkpoint marker. This handles the case where the process
    /// crashed mid-shard, leaving an incomplete file.
    /// </summary>
    /// <param name="nextShardIndex">The shard index to resume from.</param>
    public void DeleteOrphanedShard(int nextShardIndex)
    {
        string orphanPath = $"{_basePath}_shard_{nextShardIndex:D5}{_extension}";
        if (File.Exists(orphanPath))
        {
            File.Delete(orphanPath);
        }
    }

    /// <summary>
    /// Deletes all checkpoint marker files for this output.
    /// Called after a successful full completion to clean up.
    /// </summary>
    public void CleanupCheckpoints()
    {
        string directory = Path.GetDirectoryName(_basePath) ?? ".";
        string fileNameBase = Path.GetFileName(_basePath);

        if (!Directory.Exists(directory))
        {
            return;
        }

        string pattern = $"{fileNameBase}_shard_*{_extension}.checkpoint";
        foreach (string file in Directory.GetFiles(directory, pattern))
        {
            File.Delete(file);
        }
    }
}

/// <summary>
/// The computed state for resuming a checkpointed sharded write.
/// </summary>
/// <param name="RowsToSkip">Number of rows to skip (fast-forward) from the pipeline.</param>
/// <param name="NextShardIndex">The shard index to start writing at.</param>
public sealed record ResumeState(long RowsToSkip, int NextShardIndex);

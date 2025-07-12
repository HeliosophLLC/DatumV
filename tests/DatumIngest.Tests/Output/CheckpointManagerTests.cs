namespace DatumIngest.Tests.Output;

using DatumIngest.Output.Checkpoint;

/// <summary>
/// Tests for <see cref="CheckpointManager"/>.
/// </summary>
public sealed class CheckpointManagerTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"checkpoint_mgr_{Guid.NewGuid():N}");

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ScanExistingCheckpoints_EmptyDirectory_ReturnsEmptyList()
    {
        string basePath = Path.Combine(_tempDir, "output.csv");
        CheckpointManager manager = new(basePath);

        IReadOnlyList<CheckpointMarker> checkpoints = await manager.ScanExistingCheckpointsAsync();

        Assert.Empty(checkpoints);
    }

    [Fact]
    public async Task ScanExistingCheckpoints_FindsAndSortsByIndex()
    {
        string basePath = Path.Combine(_tempDir, "output.csv");
        CheckpointManager manager = new(basePath);

        // Write checkpoint files out of order
        await WriteTestCheckpoint(basePath, shardIndex: 2, rowCount: 300);
        await WriteTestCheckpoint(basePath, shardIndex: 0, rowCount: 100);
        await WriteTestCheckpoint(basePath, shardIndex: 1, rowCount: 200);

        IReadOnlyList<CheckpointMarker> checkpoints = await manager.ScanExistingCheckpointsAsync();

        Assert.Equal(3, checkpoints.Count);
        Assert.Equal(0, checkpoints[0].ShardIndex);
        Assert.Equal(1, checkpoints[1].ShardIndex);
        Assert.Equal(2, checkpoints[2].ShardIndex);
    }

    [Fact]
    public async Task WriteCheckpoint_CreatesMarkerFile()
    {
        string basePath = Path.Combine(_tempDir, "output.csv");
        CheckpointManager manager = new(basePath);
        string shardPath = Path.Combine(_tempDir, "output_shard_00000.csv");

        CheckpointMarker marker = new(
            ShardIndex: 0,
            ShardPath: shardPath,
            RowCount: 100,
            ByteCount: 5000,
            CompletedAtUtc: DateTime.UtcNow,
            SourceFingerprints: []);

        await manager.WriteCheckpointAsync(marker);

        string expectedCheckpointPath = $"{shardPath}.checkpoint";
        Assert.True(File.Exists(expectedCheckpointPath));

        CheckpointMarker? loaded = await CheckpointSerializer.ReadFromFileAsync(expectedCheckpointPath);
        Assert.NotNull(loaded);
        Assert.Equal(100, loaded.RowCount);
    }

    [Fact]
    public void ComputeResumeState_SumsRowCounts_ReturnsNextIndex()
    {
        List<CheckpointMarker> checkpoints =
        [
            CreateMarker(shardIndex: 0, rowCount: 100),
            CreateMarker(shardIndex: 1, rowCount: 200),
            CreateMarker(shardIndex: 2, rowCount: 150)
        ];

        ResumeState state = CheckpointManager.ComputeResumeState(checkpoints);

        Assert.Equal(450, state.RowsToSkip);
        Assert.Equal(3, state.NextShardIndex);
    }

    [Fact]
    public void ComputeResumeState_EmptyCheckpoints_ReturnsZeroState()
    {
        ResumeState state = CheckpointManager.ComputeResumeState([]);

        Assert.Equal(0, state.RowsToSkip);
        Assert.Equal(0, state.NextShardIndex);
    }

    [Fact]
    public void ComputeResumeState_GapInSequence_StopsAtGap()
    {
        List<CheckpointMarker> checkpoints =
        [
            CreateMarker(shardIndex: 0, rowCount: 100),
            CreateMarker(shardIndex: 1, rowCount: 200),
            // Gap: shard 2 missing
            CreateMarker(shardIndex: 3, rowCount: 300)
        ];

        ResumeState state = CheckpointManager.ComputeResumeState(checkpoints);

        Assert.Equal(300, state.RowsToSkip);
        Assert.Equal(2, state.NextShardIndex);
    }

    [Fact]
    public void DeleteOrphanedShard_RemovesFile()
    {
        string basePath = Path.Combine(_tempDir, "output.csv");
        CheckpointManager manager = new(basePath);

        // Create an orphaned shard file (partial write from crash)
        string orphanPath = Path.Combine(_tempDir, "output_shard_00002.csv");
        File.WriteAllText(orphanPath, "partial data");

        Assert.True(File.Exists(orphanPath));
        manager.DeleteOrphanedShard(2);
        Assert.False(File.Exists(orphanPath));
    }

    [Fact]
    public void DeleteOrphanedShard_NoFile_DoesNotThrow()
    {
        string basePath = Path.Combine(_tempDir, "output.csv");
        CheckpointManager manager = new(basePath);

        // Should not throw even if the file doesn't exist
        manager.DeleteOrphanedShard(5);
    }

    [Fact]
    public async Task CleanupCheckpoints_RemovesAllMarkerFiles()
    {
        string basePath = Path.Combine(_tempDir, "output.csv");
        CheckpointManager manager = new(basePath);

        await WriteTestCheckpoint(basePath, shardIndex: 0, rowCount: 100);
        await WriteTestCheckpoint(basePath, shardIndex: 1, rowCount: 200);

        // Verify they exist
        string pattern = "*.checkpoint";
        Assert.Equal(2, Directory.GetFiles(_tempDir, pattern).Length);

        manager.CleanupCheckpoints();

        Assert.Empty(Directory.GetFiles(_tempDir, pattern));
    }

    private static CheckpointMarker CreateMarker(int shardIndex, long rowCount)
    {
        return new CheckpointMarker(
            ShardIndex: shardIndex,
            ShardPath: $"output_shard_{shardIndex:D5}.csv",
            RowCount: rowCount,
            ByteCount: rowCount * 50,
            CompletedAtUtc: DateTime.UtcNow,
            SourceFingerprints: []);
    }

    private static async Task WriteTestCheckpoint(string basePath, int shardIndex, long rowCount)
    {
        string directory = Path.GetDirectoryName(basePath)!;
        string baseName = Path.GetFileNameWithoutExtension(basePath);
        string extension = Path.GetExtension(basePath);

        string shardPath = Path.Combine(directory, $"{baseName}_shard_{shardIndex:D5}{extension}");

        // Create the shard file itself (empty placeholder)
        await File.WriteAllTextAsync(shardPath, "");

        CheckpointMarker marker = new(
            ShardIndex: shardIndex,
            ShardPath: shardPath,
            RowCount: rowCount,
            ByteCount: rowCount * 50,
            CompletedAtUtc: DateTime.UtcNow,
            SourceFingerprints:
            [
                new SourceFingerprint("test", "csv", "test.csv", 12345, DateTime.UtcNow)
            ]);

        await CheckpointSerializer.WriteToFileAsync(marker, $"{shardPath}.checkpoint");
    }
}

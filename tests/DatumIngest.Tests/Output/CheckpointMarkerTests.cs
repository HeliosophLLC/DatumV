namespace DatumQuery.Tests.Output;

using DatumQuery.Output.Checkpoint;

/// <summary>
/// Tests for <see cref="CheckpointMarker"/>, <see cref="SourceFingerprint"/>,
/// and <see cref="CheckpointSerializer"/>.
/// </summary>
public sealed class CheckpointMarkerTests
{
    [Fact]
    public void Serialize_RoundTrip_PreservesAllFields()
    {
        CheckpointMarker original = new(
            ShardIndex: 3,
            ShardPath: "/output/data_shard_00003.csv",
            RowCount: 1500,
            ByteCount: 98765,
            CompletedAtUtc: new DateTime(2026, 3, 17, 12, 0, 0, DateTimeKind.Utc),
            SourceFingerprints:
            [
                new SourceFingerprint("archive", "zip", "train2017.zip", 19337442, new DateTime(2026, 1, 15, 8, 30, 0, DateTimeKind.Utc)),
                new SourceFingerprint("captions", "json", "captions.json", 2341567, new DateTime(2026, 1, 15, 8, 30, 0, DateTimeKind.Utc))
            ]);

        string json = CheckpointSerializer.Serialize(original);
        CheckpointMarker? deserialized = CheckpointSerializer.Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.ShardIndex, deserialized.ShardIndex);
        Assert.Equal(original.ShardPath, deserialized.ShardPath);
        Assert.Equal(original.RowCount, deserialized.RowCount);
        Assert.Equal(original.ByteCount, deserialized.ByteCount);
        Assert.Equal(original.CompletedAtUtc, deserialized.CompletedAtUtc);
        Assert.Equal(original.SourceFingerprints.Count, deserialized.SourceFingerprints.Count);
        Assert.Equal(original.SourceFingerprints[0].Name, deserialized.SourceFingerprints[0].Name);
        Assert.Equal(original.SourceFingerprints[0].SizeBytes, deserialized.SourceFingerprints[0].SizeBytes);
    }

    [Fact]
    public void Deserialize_KnownJson_ParsesCorrectly()
    {
        string json = """
            {
              "shardIndex": 0,
              "shardPath": "output_shard_00000.csv",
              "rowCount": 1000,
              "byteCount": 45678,
              "completedAtUtc": "2026-03-17T12:00:00Z",
              "sourceFingerprints": [
                {
                  "name": "archive",
                  "provider": "zip",
                  "path": "train2017.zip",
                  "sizeBytes": 19337442,
                  "lastModifiedUtc": "2026-01-15T08:30:00Z"
                }
              ]
            }
            """;

        CheckpointMarker? marker = CheckpointSerializer.Deserialize(json);

        Assert.NotNull(marker);
        Assert.Equal(0, marker.ShardIndex);
        Assert.Equal("output_shard_00000.csv", marker.ShardPath);
        Assert.Equal(1000, marker.RowCount);
        Assert.Equal(45678, marker.ByteCount);
        Assert.Single(marker.SourceFingerprints);
        Assert.Equal("archive", marker.SourceFingerprints[0].Name);
        Assert.Equal("zip", marker.SourceFingerprints[0].Provider);
        Assert.Equal(19337442, marker.SourceFingerprints[0].SizeBytes);
    }

    [Fact]
    public void SourceFingerprint_ValueEquality_MatchesWhenEqual()
    {
        SourceFingerprint a = new("data", "csv", "data.csv", 12345, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SourceFingerprint b = new("data", "csv", "data.csv", 12345, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(a, b);
    }

    [Fact]
    public void SourceFingerprint_ValueEquality_DiffersWhenSizeDiffers()
    {
        SourceFingerprint a = new("data", "csv", "data.csv", 12345, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SourceFingerprint b = new("data", "csv", "data.csv", 99999, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task WriteToFile_ReadFromFile_RoundTrips()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"checkpoint_test_{Guid.NewGuid():N}.checkpoint");

        try
        {
            CheckpointMarker original = new(
                ShardIndex: 1,
                ShardPath: "output_shard_00001.csv",
                RowCount: 500,
                ByteCount: 12345,
                CompletedAtUtc: DateTime.UtcNow,
                SourceFingerprints: []);

            await CheckpointSerializer.WriteToFileAsync(original, tempPath);
            CheckpointMarker? loaded = await CheckpointSerializer.ReadFromFileAsync(tempPath);

            Assert.NotNull(loaded);
            Assert.Equal(original.ShardIndex, loaded.ShardIndex);
            Assert.Equal(original.RowCount, loaded.RowCount);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}

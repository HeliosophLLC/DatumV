namespace DatumIngest.Output.Checkpoint;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Records the successful completion of a single shard write.
/// Presence of a serialized marker file indicates the corresponding shard is complete.
/// </summary>
/// <param name="ShardIndex">Zero-based index of the completed shard.</param>
/// <param name="ShardPath">Absolute or relative path to the shard file.</param>
/// <param name="RowCount">Number of rows written to this shard.</param>
/// <param name="ByteCount">Approximate byte size of the shard on disk.</param>
/// <param name="CompletedAtUtc">UTC timestamp when the shard was finalized.</param>
/// <param name="SourceFingerprints">Fingerprints of all data sources at time of write.</param>
public sealed record CheckpointMarker(
    int ShardIndex,
    string ShardPath,
    long RowCount,
    long ByteCount,
    DateTime CompletedAtUtc,
    IReadOnlyList<SourceFingerprint> SourceFingerprints);

/// <summary>
/// Identity snapshot of a data source file, used to detect whether sources
/// have changed between a failed run and a resume attempt.
/// </summary>
/// <param name="Name">Logical table name (e.g. "archive").</param>
/// <param name="Provider">Provider identifier (e.g. "csv", "zip").</param>
/// <param name="Path">File path to the data source.</param>
/// <param name="SizeBytes">File size in bytes at time of fingerprinting.</param>
/// <param name="LastModifiedUtc">Last-modified timestamp at time of fingerprinting.</param>
public sealed record SourceFingerprint(
    string Name,
    string Provider,
    string Path,
    long SizeBytes,
    DateTime LastModifiedUtc);

/// <summary>
/// Serializes and deserializes <see cref="CheckpointMarker"/> instances
/// using System.Text.Json with source generation for AOT/trimming compatibility.
/// </summary>
public static class CheckpointSerializer
{
    /// <summary>
    /// Serializes a checkpoint marker to a JSON string.
    /// </summary>
    public static string Serialize(CheckpointMarker marker)
    {
        return JsonSerializer.Serialize(marker, CheckpointJsonContext.Default.CheckpointMarker);
    }

    /// <summary>
    /// Deserializes a checkpoint marker from a JSON string.
    /// </summary>
    public static CheckpointMarker? Deserialize(string json)
    {
        return JsonSerializer.Deserialize(json, CheckpointJsonContext.Default.CheckpointMarker);
    }

    /// <summary>
    /// Writes a checkpoint marker to a file as formatted JSON.
    /// </summary>
    public static async Task WriteToFileAsync(CheckpointMarker marker, string path)
    {
        string json = Serialize(marker);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>
    /// Reads and deserializes a checkpoint marker from a file.
    /// </summary>
    public static async Task<CheckpointMarker?> ReadFromFileAsync(string path)
    {
        string json = await File.ReadAllTextAsync(path);
        return Deserialize(json);
    }
}

/// <summary>
/// Source-generated JSON serializer context for checkpoint types.
/// Enables AOT compilation and trimming support.
/// </summary>
[JsonSerializable(typeof(CheckpointMarker))]
[JsonSerializable(typeof(SourceFingerprint))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class CheckpointJsonContext : JsonSerializerContext
{
}

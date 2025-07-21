namespace DatumIngest.Manifest;

using System.Text.Json;
using System.Text.Json.Serialization;
using DatumIngest.Manifest.Insights;

/// <summary>
/// Serializes and deserializes <see cref="QueryResultsManifest"/> using System.Text.Json
/// with source generation for AOT/trimming compatibility.
/// </summary>
public static class ManifestSerializer
{
    /// <summary>
    /// Serializes a manifest to a JSON string.
    /// </summary>
    public static string Serialize(QueryResultsManifest manifest)
    {
        return JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.QueryResultsManifest);
    }

    /// <summary>
    /// Serializes a manifest to a UTF-8 byte array.
    /// </summary>
    public static byte[] SerializeToUtf8Bytes(QueryResultsManifest manifest)
    {
        return JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonContext.Default.QueryResultsManifest);
    }

    /// <summary>
    /// Deserializes a manifest from a JSON string.
    /// </summary>
    public static QueryResultsManifest? Deserialize(string json)
    {
        return JsonSerializer.Deserialize(json, ManifestJsonContext.Default.QueryResultsManifest);
    }

    /// <summary>
    /// Writes a manifest to a file as formatted JSON.
    /// </summary>
    public static async Task WriteToFileAsync(QueryResultsManifest manifest, string path)
    {
        string json = Serialize(manifest);
        await File.WriteAllTextAsync(path, json);
    }
}

/// <summary>
/// Source-generated JSON serializer context for the manifest type hierarchy.
/// Enables AOT compilation and trimming support.
/// </summary>
[JsonSerializable(typeof(QueryResultsManifest))]
[JsonSerializable(typeof(FeatureManifest))]
[JsonSerializable(typeof(NumericFeatureManifest))]
[JsonSerializable(typeof(StringFeatureManifest))]
[JsonSerializable(typeof(VectorFeatureManifest))]
[JsonSerializable(typeof(TensorFeatureManifest))]
[JsonSerializable(typeof(ImageFeatureManifest))]
[JsonSerializable(typeof(BinaryFeatureManifest))]
[JsonSerializable(typeof(TemporalFeatureManifest))]
[JsonSerializable(typeof(FrequencyEntry))]
[JsonSerializable(typeof(HistogramData))]
[JsonSerializable(typeof(NumericSummaryData))]
[JsonSerializable(typeof(ColumnInteraction))]
[JsonSerializable(typeof(QuantileData))]
[JsonSerializable(typeof(DatasetInsight))]
[JsonSerializable(typeof(InsightAction))]
[JsonSerializable(typeof(QueryAnnotation))]
[JsonSerializable(typeof(InsightThresholds))]
[JsonSerializable(typeof(QuerySynthesisOptions))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ManifestJsonContext : JsonSerializerContext
{
}

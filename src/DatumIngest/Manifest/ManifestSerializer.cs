namespace DatumIngest.Manifest;

using System.Text.Json;
using System.Text.Json.Serialization;
using DatumIngest.Manifest.Insights;

/// <summary>
/// Serializes and deserializes <see cref="SourceManifest"/> using System.Text.Json
/// with source generation for AOT/trimming compatibility.
/// </summary>
public static class ManifestSerializer
{
    /// <summary>
    /// Serializes a source manifest to a JSON string.
    /// </summary>
    public static string Serialize(SourceManifest manifest)
    {
        return JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.SourceManifest);
    }

    /// <summary>
    /// Serializes a single-table manifest to a JSON string by wrapping it in a
    /// <see cref="SourceManifest"/> keyed by <paramref name="tableName"/>.
    /// </summary>
    /// <param name="tableName">Catalog table name used as the dictionary key.</param>
    /// <param name="manifest">The single-table manifest to serialize.</param>
    public static string Serialize(string tableName, QueryResultsManifest manifest)
    {
        return Serialize(SourceManifest.Create(tableName, manifest));
    }

    /// <summary>
    /// Serializes a source manifest to a UTF-8 byte array.
    /// </summary>
    public static byte[] SerializeToUtf8Bytes(SourceManifest manifest)
    {
        return JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonContext.Default.SourceManifest);
    }

    /// <summary>
    /// Serializes a single-table manifest to a UTF-8 byte array by wrapping it in a
    /// <see cref="SourceManifest"/> keyed by <paramref name="tableName"/>.
    /// </summary>
    /// <param name="tableName">Catalog table name used as the dictionary key.</param>
    /// <param name="manifest">The single-table manifest to serialize.</param>
    public static byte[] SerializeToUtf8Bytes(string tableName, QueryResultsManifest manifest)
    {
        return SerializeToUtf8Bytes(SourceManifest.Create(tableName, manifest));
    }

    /// <summary>
    /// Deserializes a source manifest from a JSON string.
    /// </summary>
    public static SourceManifest? Deserialize(string json)
    {
        return JsonSerializer.Deserialize(json, ManifestJsonContext.Default.SourceManifest);
    }

    /// <summary>
    /// Writes a source manifest to a file as formatted JSON.
    /// </summary>
    public static async Task WriteToFileAsync(SourceManifest manifest, string path)
    {
        string json = Serialize(manifest);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>
    /// Writes a single-table manifest to a file as formatted JSON by wrapping it in a
    /// <see cref="SourceManifest"/> keyed by <paramref name="tableName"/>.
    /// </summary>
    /// <param name="tableName">Catalog table name used as the dictionary key.</param>
    /// <param name="manifest">The single-table manifest to write.</param>
    /// <param name="path">Output file path.</param>
    public static async Task WriteToFileAsync(string tableName, QueryResultsManifest manifest, string path)
    {
        await WriteToFileAsync(SourceManifest.Create(tableName, manifest), path);
    }

}

/// <summary>
/// Source-generated JSON serializer context for the manifest type hierarchy.
/// Enables AOT compilation and trimming support.
/// </summary>
[JsonSerializable(typeof(QueryResultsManifest))]
[JsonSerializable(typeof(SourceManifest))]
[JsonSerializable(typeof(FeatureManifest))]
[JsonSerializable(typeof(NumericFeatureManifest))]
[JsonSerializable(typeof(StringFeatureManifest))]
[JsonSerializable(typeof(VectorFeatureManifest))]
[JsonSerializable(typeof(TensorFeatureManifest))]
[JsonSerializable(typeof(ImageFeatureManifest))]
[JsonSerializable(typeof(BinaryFeatureManifest))]
[JsonSerializable(typeof(TemporalFeatureManifest))]
[JsonSerializable(typeof(BooleanFeatureManifest))]
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
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
internal sealed partial class ManifestJsonContext : JsonSerializerContext
{
}

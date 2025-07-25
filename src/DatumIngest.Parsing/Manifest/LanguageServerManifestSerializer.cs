namespace DatumIngest.Manifest;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Serializes and deserializes <see cref="LanguageServerManifest"/> using System.Text.Json
/// with source generation for AOT/trimming compatibility.
/// </summary>
public static class LanguageServerManifestSerializer
{
    /// <summary>
    /// Serializes a language server manifest to a JSON string.
    /// </summary>
    public static string Serialize(LanguageServerManifest manifest)
    {
        return JsonSerializer.Serialize(manifest, LanguageServerManifestJsonContext.Default.LanguageServerManifest);
    }

    /// <summary>
    /// Deserializes a language server manifest from a JSON string.
    /// </summary>
    public static LanguageServerManifest? Deserialize(string json)
    {
        return JsonSerializer.Deserialize(json, LanguageServerManifestJsonContext.Default.LanguageServerManifest);
    }

    /// <summary>
    /// Writes a language server manifest to a file as formatted JSON.
    /// </summary>
    public static async Task WriteToFileAsync(LanguageServerManifest manifest, string path)
    {
        string json = Serialize(manifest);
        await File.WriteAllTextAsync(path, json);
    }
}

/// <summary>
/// Source-generated JSON serializer context for the language server manifest types.
/// Enables AOT compilation and trimming support.
/// </summary>
[JsonSerializable(typeof(LanguageServerManifest))]
[JsonSerializable(typeof(TableSchemaEntry))]
[JsonSerializable(typeof(TableColumnEntry))]
[JsonSerializable(typeof(FunctionSignature))]
[JsonSerializable(typeof(ParameterSignature))]
[JsonSerializable(typeof(FunctionCategory))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class LanguageServerManifestJsonContext : JsonSerializerContext
{
}

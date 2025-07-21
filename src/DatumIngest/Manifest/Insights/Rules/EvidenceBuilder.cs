namespace DatumIngest.Manifest.Insights.Rules;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Fluent builder for constructing evidence dictionaries used by <see cref="RawFinding"/>.
/// Evidence maps feature names to dictionaries of statistic name → value.
/// </summary>
internal sealed class EvidenceBuilder
{
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _evidence = new();

    /// <summary>
    /// Adds a statistic for the given feature.
    /// </summary>
    internal EvidenceBuilder Add(string feature, string statistic, double value)
    {
        GetOrCreateFeature(feature)[statistic] = JsonSerializer.SerializeToElement(value, EvidenceJsonContext.Default.Double);
        return this;
    }

    /// <summary>
    /// Adds a statistic for the given feature.
    /// </summary>
    internal EvidenceBuilder Add(string feature, string statistic, long value)
    {
        GetOrCreateFeature(feature)[statistic] = JsonSerializer.SerializeToElement(value, EvidenceJsonContext.Default.Int64);
        return this;
    }

    /// <summary>
    /// Adds a statistic for the given feature.
    /// </summary>
    internal EvidenceBuilder Add(string feature, string statistic, bool value)
    {
        GetOrCreateFeature(feature)[statistic] = JsonSerializer.SerializeToElement(value, EvidenceJsonContext.Default.Boolean);
        return this;
    }

    /// <summary>
    /// Adds a string statistic for the given feature.
    /// </summary>
    internal EvidenceBuilder Add(string feature, string statistic, string value)
    {
        GetOrCreateFeature(feature)[statistic] = JsonSerializer.SerializeToElement(value, EvidenceJsonContext.Default.String);
        return this;
    }

    /// <summary>
    /// Builds the evidence as a read-only dictionary. Returns null if no evidence was added.
    /// </summary>
    internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? Build()
    {
        if (_evidence.Count == 0)
        {
            return null;
        }

        Dictionary<string, IReadOnlyDictionary<string, JsonElement>> result = new(_evidence.Count);

        foreach (KeyValuePair<string, Dictionary<string, JsonElement>> entry in _evidence)
        {
            result[entry.Key] = entry.Value;
        }

        return result;
    }

    private Dictionary<string, JsonElement> GetOrCreateFeature(string feature)
    {
        if (!_evidence.TryGetValue(feature, out Dictionary<string, JsonElement>? stats))
        {
            stats = new Dictionary<string, JsonElement>();
            _evidence[feature] = stats;
        }

        return stats;
    }
}

/// <summary>
/// Source-generated JSON context for primitive types used in evidence values.
/// Trimming-safe alternative to <c>JsonSerializer.SerializeToElement&lt;T&gt;</c>.
/// </summary>
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
internal sealed partial class EvidenceJsonContext : JsonSerializerContext
{
}

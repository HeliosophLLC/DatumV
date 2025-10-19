using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatumIngest.Ingestion.Sampling;

/// <summary>
/// Serializes and deserializes <see cref="SamplePreview"/> instances to and from JSON.
/// Uses manual <see cref="Utf8JsonWriter"/>/<see cref="JsonDocument"/> for the polymorphic
/// sample rows (which contain floats, strings, booleans, nulls, and nested arrays) and
/// a source-generated context for the feature list.
/// </summary>
public static class SamplePreviewSerializer
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    /// <summary>
    /// Serializes a <see cref="SamplePreview"/> to a JSON string.
    /// </summary>
    /// <param name="preview">The sample preview to serialize.</param>
    /// <returns>A JSON string.</returns>
    public static string Serialize(SamplePreview preview)
    {
        using MemoryStream stream = new();
        using Utf8JsonWriter writer = new(stream, WriterOptions);

        writer.WriteStartObject();

        writer.WritePropertyName("features");
        writer.WriteStartArray();
        foreach (SampleFeature feature in preview.Features)
        {
            writer.WriteStartObject();
            writer.WriteString("name", feature.Name);
            writer.WriteString("kind", feature.Kind);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("samples");
        writer.WriteStartArray();
        foreach (object?[] row in preview.Samples)
        {
            writer.WriteStartArray();
            foreach (object? value in row)
            {
                WriteValue(writer, value);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.Flush();

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Deserializes a <see cref="SamplePreview"/> from a JSON string.
    /// </summary>
    /// <param name="json">The JSON string to deserialize.</param>
    /// <returns>The deserialized sample preview, or <c>null</c> if deserialization fails.</returns>
    public static SamplePreview? Deserialize(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("features", out JsonElement featuresElement) ||
            !root.TryGetProperty("samples", out JsonElement samplesElement))
        {
            return null;
        }

        List<SampleFeature> features = new();
        foreach (JsonElement featureElement in featuresElement.EnumerateArray())
        {
            string name = featureElement.GetProperty("name").GetString() ?? string.Empty;
            string kind = featureElement.GetProperty("kind").GetString() ?? string.Empty;
            features.Add(new SampleFeature(name, kind));
        }

        List<object?[]> samples = new();
        foreach (JsonElement rowElement in samplesElement.EnumerateArray())
        {
            object?[] row = new object?[rowElement.GetArrayLength()];
            int index = 0;
            foreach (JsonElement cellElement in rowElement.EnumerateArray())
            {
                row[index++] = ReadValue(cellElement);
            }

            samples.Add(row);
        }

        return new SamplePreview
        {
            Features = features,
            Samples = samples,
        };
    }

    /// <summary>
    /// Writes a <see cref="SamplePreview"/> to a file as JSON.
    /// </summary>
    /// <param name="preview">The sample preview to write.</param>
    /// <param name="filePath">The destination file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task WriteToFileAsync(
        SamplePreview preview, string filePath, CancellationToken cancellationToken = default)
    {
        string json = Serialize(preview);
        await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a single polymorphic value to the JSON writer.
    /// Handles floats, bytes, booleans, strings, nested arrays, and null.
    /// </summary>
    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case byte b:
                writer.WriteNumberValue(b);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case object?[] array:
                writer.WriteStartArray();
                foreach (object? element in array)
                {
                    WriteValue(writer, element);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    /// <summary>
    /// Reads a single polymorphic value from a <see cref="JsonElement"/>.
    /// </summary>
    private static object? ReadValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetSingle(),
            JsonValueKind.Array => ReadArray(element),
            _ => element.ToString(),
        };
    }

    /// <summary>
    /// Reads a JSON array into an <c>object?[]</c>, recursively normalizing elements.
    /// </summary>
    private static object?[] ReadArray(JsonElement arrayElement)
    {
        object?[] result = new object?[arrayElement.GetArrayLength()];
        int index = 0;
        foreach (JsonElement item in arrayElement.EnumerateArray())
        {
            result[index++] = ReadValue(item);
        }

        return result;
    }
}

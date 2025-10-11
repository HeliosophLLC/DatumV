using System.Text.Json;

namespace DatumIngest.Serialization.Json;

/// <summary>
/// Navigates a JSON DOM to find the target array for row extraction.
/// Supports dot-delimited path navigation and primitive array detection.
/// </summary>
internal static class JsonNavigator
{
    /// <summary>
    /// Navigates from the root element to the target array using a dot-delimited path.
    /// If no path is specified, the root element must itself be an array.
    /// </summary>
    internal static JsonElement NavigateToArray(JsonElement root, string? jsonPath)
    {
        if (string.IsNullOrEmpty(jsonPath))
        {
            if (root.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    root.ValueKind == JsonValueKind.Object
                        ? "JSON root is not an array. Specify a 'json_path' option to navigate to an array property."
                        : "JSON root is not an array.");
            }

            return root;
        }

        JsonElement current = root;
        foreach (string segment in jsonPath.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException(
                    $"Cannot navigate to '{segment}': current element is not an object.");

            if (!current.TryGetProperty(segment, out JsonElement next))
                throw new InvalidOperationException(
                    $"Property '{segment}' not found in JSON path '{jsonPath}'.");

            current = next;
        }

        if (current.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"JSON path '{jsonPath}' does not resolve to an array.");

        return current;
    }

    /// <summary>
    /// Returns true if the array contains primitive values (not objects).
    /// An empty array is not considered primitive.
    /// </summary>
    internal static bool IsPrimitiveArray(JsonElement array)
    {
        foreach (JsonElement element in array.EnumerateArray())
            return element.ValueKind != JsonValueKind.Object;
        return false;
    }

    /// <summary>
    /// Infers the <see cref="DatumIngest.Model.DataKind"/> for a primitive array
    /// by sampling up to 100 elements.
    /// </summary>
    internal static DatumIngest.Model.DataKind InferPrimitiveArrayKind(JsonElement array)
    {
        DatumIngest.Model.DataKind kind = DatumIngest.Model.DataKind.String;
        int sampled = 0;

        foreach (JsonElement element in array.EnumerateArray())
        {
            if (sampled >= 100) break;

            DatumIngest.Model.DataKind detected = JsonTypeInference.InferKind(element);
            kind = sampled == 0 ? detected : JsonTypeInference.WidenKind(kind, detected);
            sampled++;
        }

        return kind;
    }
}

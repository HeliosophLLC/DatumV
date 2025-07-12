using System.Text.Json;
using DatumIngest.Model;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Shared type inference and conversion logic for JSON-based providers.
/// Used by <see cref="JsonTableProvider"/> for DOM-based JSON files and by
/// <see cref="JsonlTableProvider"/> for line-streaming newline-delimited JSON.
/// </summary>
internal static class JsonTypeInference
{
    /// <summary>
    /// Infers the <see cref="DataKind"/> for a JSON element value.
    /// </summary>
    internal static DataKind InferKind(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => DataKind.Scalar,
            JsonValueKind.String => DataKind.String,
            JsonValueKind.True or JsonValueKind.False => DataKind.Scalar,
            JsonValueKind.Null or JsonValueKind.Undefined => DataKind.String,
            JsonValueKind.Array or JsonValueKind.Object => DataKind.JsonValue,
            _ => DataKind.String
        };
    }

    /// <summary>
    /// Converts a JSON element to a <see cref="DataValue"/> based on the target column kind.
    /// </summary>
    internal static DataValue ConvertElement(JsonElement element, DataKind targetKind)
    {
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            return DataValue.Null(targetKind);
        }

        return targetKind switch
        {
            DataKind.Scalar => element.ValueKind switch
            {
                JsonValueKind.Number => DataValue.FromScalar((float)element.GetDouble()),
                JsonValueKind.True => DataValue.FromScalar(1f),
                JsonValueKind.False => DataValue.FromScalar(0f),
                _ => DataValue.Null(DataKind.Scalar)
            },
            DataKind.String => DataValue.FromString(element.GetString() ?? element.GetRawText()),
            DataKind.JsonValue => DataValue.FromJsonValue(element.GetRawText()),
            _ => DataValue.FromString(element.GetRawText())
        };
    }

    /// <summary>
    /// Resolves a type conflict between two <see cref="DataKind"/> values during
    /// schema inference. If either kind is <see cref="DataKind.JsonValue"/>, the
    /// result is <see cref="DataKind.JsonValue"/>; otherwise, widens to
    /// <see cref="DataKind.String"/>.
    /// </summary>
    internal static DataKind WidenKind(DataKind existingKind, DataKind detectedKind)
    {
        if (existingKind == detectedKind)
        {
            return existingKind;
        }

        if (existingKind == DataKind.JsonValue || detectedKind == DataKind.JsonValue)
        {
            return DataKind.JsonValue;
        }

        return DataKind.String;
    }
}

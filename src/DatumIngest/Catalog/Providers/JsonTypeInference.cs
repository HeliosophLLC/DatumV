using System.Globalization;
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
    /// String values that parse as ISO 8601 dates are inferred as Date or DateTime.
    /// </summary>
    internal static DataKind InferKind(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => DataKind.Scalar,
            JsonValueKind.String => InferStringKind(element.GetString()),
            JsonValueKind.True or JsonValueKind.False => DataKind.Scalar,
            JsonValueKind.Null or JsonValueKind.Undefined => DataKind.String,
            JsonValueKind.Array or JsonValueKind.Object => DataKind.JsonValue,
            _ => DataKind.String
        };
    }

    /// <summary>
    /// Attempts to narrow a string value to Date or DateTime via ISO 8601 parsing.
    /// </summary>
    private static DataKind InferStringKind(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return DataKind.String;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            return parsed.TimeOfDay != TimeSpan.Zero ? DataKind.DateTime : DataKind.Date;
        }

        return DataKind.String;
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
            DataKind.Date when element.ValueKind == JsonValueKind.String =>
                DateOnly.TryParse(element.GetString(), CultureInfo.InvariantCulture, out DateOnly date)
                    ? DataValue.FromDate(date)
                    : DataValue.Null(DataKind.Date),
            DataKind.DateTime when element.ValueKind == JsonValueKind.String =>
                DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTimeOffset dateTime)
                    ? DataValue.FromDateTime(dateTime)
                    : DataValue.Null(DataKind.DateTime),
            DataKind.String => DataValue.FromString(element.GetString() ?? element.GetRawText()),
            DataKind.JsonValue => DataValue.FromJsonValue(element.GetRawText()),
            _ => DataValue.FromString(element.GetRawText())
        };
    }

    /// <summary>
    /// Resolves a type conflict between two <see cref="DataKind"/> values during
    /// schema inference. Date + DateTime widens to DateTime. If either kind is
    /// <see cref="DataKind.JsonValue"/>, the result is <see cref="DataKind.JsonValue"/>;
    /// otherwise, widens to <see cref="DataKind.String"/>.
    /// </summary>
    internal static DataKind WidenKind(DataKind existingKind, DataKind detectedKind)
    {
        if (existingKind == detectedKind)
        {
            return existingKind;
        }

        // Date + DateTime → DateTime (widen to include time component).
        if ((existingKind is DataKind.Date && detectedKind is DataKind.DateTime) ||
            (existingKind is DataKind.DateTime && detectedKind is DataKind.Date))
        {
            return DataKind.DateTime;
        }

        if (existingKind == DataKind.JsonValue || detectedKind == DataKind.JsonValue)
        {
            return DataKind.JsonValue;
        }

        return DataKind.String;
    }
}

using System.Globalization;
using System.Text.Json;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Json;

/// <summary>
/// Shared type inference and conversion logic for JSON-based deserializers.
/// Used by <see cref="JsonDeserializer"/> and <see cref="Jsonl.JsonlDeserializer"/>.
/// </summary>
internal static class JsonTypeInference
{
    internal static DataKind InferKind(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out _) ? DataKind.Int64 : DataKind.Float64,
            JsonValueKind.String => InferStringKind(element.GetString()),
            JsonValueKind.True or JsonValueKind.False => DataKind.Boolean,
            JsonValueKind.Null or JsonValueKind.Undefined => DataKind.String,
            JsonValueKind.Array or JsonValueKind.Object => DataKind.JsonValue,
            _ => DataKind.String
        };
    }

    private static DataKind InferStringKind(string? value)
    {
        if (string.IsNullOrEmpty(value)) return DataKind.String;

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            return parsed.TimeOfDay != TimeSpan.Zero ? DataKind.DateTime : DataKind.Date;
        }

        return DataKind.String;
    }

    internal static DataValue ConvertElement(JsonElement element, DataKind targetKind, IValueStore store)
    {
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return DataValue.Null(targetKind);

        return targetKind switch
        {
            DataKind.Float32 => element.ValueKind switch
            {
                JsonValueKind.Number => DataValue.FromFloat32((float)element.GetDouble()),
                JsonValueKind.True => DataValue.FromFloat32(1f),
                JsonValueKind.False => DataValue.FromFloat32(0f),
                _ => DataValue.Null(DataKind.Float32)
            },
            DataKind.Float64 => element.ValueKind switch
            {
                JsonValueKind.Number => DataValue.FromFloat64(element.GetDouble()),
                JsonValueKind.True => DataValue.FromFloat64(1.0),
                JsonValueKind.False => DataValue.FromFloat64(0.0),
                _ => DataValue.Null(DataKind.Float64)
            },
            DataKind.Int64 => element.ValueKind switch
            {
                JsonValueKind.Number => element.TryGetInt64(out long int64Value)
                    ? DataValue.FromInt64(int64Value)
                    : DataValue.FromInt64((long)element.GetDouble()),
                JsonValueKind.True => DataValue.FromInt64(1),
                JsonValueKind.False => DataValue.FromInt64(0),
                _ => DataValue.Null(DataKind.Int64)
            },
            DataKind.Boolean => element.ValueKind switch
            {
                JsonValueKind.True => DataValue.FromBoolean(true),
                JsonValueKind.False => DataValue.FromBoolean(false),
                JsonValueKind.Number => DataValue.FromBoolean(element.GetDouble() != 0),
                _ => DataValue.Null(DataKind.Boolean)
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
            DataKind.String => DataValue.FromString(
                element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? element.GetRawText()
                    : element.GetRawText(), store),
            DataKind.JsonValue => DataValue.FromJsonValue(element.GetRawText(), store),
            _ => DataValue.FromString(element.GetRawText(), store)
        };
    }

    internal static DataKind WidenKind(DataKind existingKind, DataKind detectedKind)
    {
        if (existingKind == detectedKind) return existingKind;

        if (IsNumericOrBoolean(existingKind) && IsNumericOrBoolean(detectedKind))
        {
            if (existingKind is DataKind.Float64 || detectedKind is DataKind.Float64)
                return DataKind.Float64;
            if (existingKind is DataKind.Float32 || detectedKind is DataKind.Float32)
                return DataKind.Float64;
            return DataKind.Int64;
        }

        if ((existingKind is DataKind.Date && detectedKind is DataKind.DateTime) ||
            (existingKind is DataKind.DateTime && detectedKind is DataKind.Date))
            return DataKind.DateTime;

        if (existingKind == DataKind.JsonValue || detectedKind == DataKind.JsonValue)
            return DataKind.JsonValue;

        return DataKind.String;
    }

    private static bool IsNumericOrBoolean(DataKind kind)
        => kind is DataKind.Boolean or DataKind.Int64 or DataKind.Float32 or DataKind.Float64;
}
